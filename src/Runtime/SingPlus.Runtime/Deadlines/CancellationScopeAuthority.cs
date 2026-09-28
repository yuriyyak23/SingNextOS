using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// Owns temporal cancellation coordination only. It never closes provider effects,
/// releases ownership, or authorizes reclaim.
/// </summary>
public sealed class CancellationScopeAuthority
{
    private sealed class Record
    {
        public required CancellationScopeHandle Handle { get; init; }
        public required ProcessHandle Owner { get; init; }
        public CancellationScopeHandle? Parent { get; init; }
        public MonotonicDeadline? EffectiveDeadline { get; init; }
        public bool Requested { get; set; }
        public CancellationDisposition Disposition { get; set; }
        public ulong Sequence { get; set; } = 1;
        public string? ConsumerBinding { get; set; }
    }

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<CancellationScopeId, Record> _records = [];
    private ulong _nextId = 1;

    internal CancellationScopeAuthority(TimeProvider timeProvider) => _timeProvider = timeProvider;

    internal KernelResult<CancellationObservation> Create(
        ProcessHandle owner,
        MonotonicDeadline? deadline,
        CancellationScopeHandle? parent)
    {
        lock (_gate)
        {
            if (_nextId == 0)
                return KernelResult<CancellationObservation>.Fail(KernelError.CapacityExhausted, "Cancellation scope identity space is exhausted.");

            Record? parentRecord = null;
            if (parent is { } parentHandle)
            {
                var resolved = ResolveExact(parentHandle);
                if (!resolved.IsSuccess)
                    return KernelResult<CancellationObservation>.Fail(resolved.Error, resolved.Message!);
                parentRecord = resolved.Value!;
                RefreshDeadline(parentRecord);
                if (parentRecord.Owner != owner)
                    return KernelResult<CancellationObservation>.Fail(KernelError.WrongSessionOwner, "Child cancellation scope must have the exact same process-generation owner as its parent.");
            }

            var effective = deadline ?? parentRecord?.EffectiveDeadline;
            if (effective is { Timestamp: <= 0 })
                return KernelResult<CancellationObservation>.Fail(KernelError.InvalidMessage, "Monotonic deadline timestamp must be positive.");
            if (parentRecord?.EffectiveDeadline is { } parentDeadline &&
                effective is { } childDeadline && childDeadline.Timestamp > parentDeadline.Timestamp)
                return KernelResult<CancellationObservation>.Fail(KernelError.InvalidMessage, "A child deadline cannot extend its parent deadline.");

            var handle = new CancellationScopeHandle(new CancellationScopeId(_nextId++), new CancellationScopeGeneration(1));
            var record = new Record
            {
                Handle = handle,
                Owner = owner,
                Parent = parent,
                EffectiveDeadline = effective,
                Requested = parentRecord?.Requested == true,
                Disposition = parentRecord?.Requested == true
                    ? CancellationDisposition.CancellationRequested
                    : CancellationDisposition.Active,
            };
            _records.Add(handle.ScopeId, record);
            RefreshDeadline(record);
            return KernelResult<CancellationObservation>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<CancellationObservation> Request(ProcessHandle owner, CancellationScopeHandle scope)
    {
        lock (_gate)
        {
            var resolved = ResolveForOwner(scope, owner);
            if (!resolved.IsSuccess) return StaleOrFailure(scope, owner, resolved);
            var record = resolved.Value!;
            RefreshDeadline(record);
            if (!IsTerminal(record.Disposition))
            {
                record.Requested = true;
                if (record.Disposition == CancellationDisposition.Active)
                    SetDisposition(record, CancellationDisposition.CancellationRequested);
                PropagateRequest(record.Handle);
            }
            return KernelResult<CancellationObservation>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<CancellationObservation> Observe(ProcessHandle owner, CancellationScopeHandle scope)
    {
        lock (_gate)
        {
            var resolved = ResolveForOwner(scope, owner);
            if (!resolved.IsSuccess) return StaleOrFailure(scope, owner, resolved);
            RefreshAncestors(resolved.Value!);
            return KernelResult<CancellationObservation>.Ok(Snapshot(resolved.Value!));
        }
    }

    internal KernelResult<CancellationObservation> RecordDisposition(
        ProcessHandle owner,
        CancellationScopeHandle scope,
        CancellationDisposition disposition)
    {
        lock (_gate)
        {
            if (!Enum.IsDefined(disposition) || disposition is CancellationDisposition.Active or CancellationDisposition.Stale)
                return KernelResult<CancellationObservation>.Fail(KernelError.InvalidTransition, "Cancellation disposition is not a recordable consumer outcome.");
            var resolved = ResolveForOwner(scope, owner);
            if (!resolved.IsSuccess) return StaleOrFailure(scope, owner, resolved);
            var record = resolved.Value!;
            RefreshAncestors(record);

            if (IsTerminal(record.Disposition))
            {
                if (record.Disposition != disposition)
                    return KernelResult<CancellationObservation>.Fail(KernelError.InvalidTransition, $"Cancellation scope is terminal as {record.Disposition}.");
                return KernelResult<CancellationObservation>.Ok(Snapshot(record));
            }
            SetDisposition(record, disposition);
            return KernelResult<CancellationObservation>.Ok(Snapshot(record));
        }
    }

    internal KernelResult BindConsumer(
        ProcessHandle owner,
        CancellationScopeHandle scope,
        string consumerBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerBinding);
        lock (_gate)
        {
            var resolved = ResolveForOwner(scope, owner);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.ConsumerBinding is null)
            {
                record.ConsumerBinding = consumerBinding;
                record.Sequence++;
                return KernelResult.Ok();
            }
            return string.Equals(record.ConsumerBinding, consumerBinding, StringComparison.Ordinal)
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.StaleGeneration, "Cancellation scope generation is already bound to another operation.");
        }
    }

    internal KernelResult ClaimConsumer(
        ProcessHandle owner,
        CancellationScopeHandle scope,
        string consumerBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerBinding);
        lock (_gate)
        {
            var resolved = ResolveForOwner(scope, owner);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.ConsumerBinding is not null)
                return KernelResult.Fail(KernelError.StaleGeneration, "Cancellation scope generation was already consumed by an operation.");
            record.ConsumerBinding = consumerBinding;
            record.Sequence++;
            return KernelResult.Ok();
        }
    }

    internal CancellationObservation[] InspectionSnapshot(ProcessHandle owner)
    {
        lock (_gate)
        {
            var records = _records.Values.Where(record => record.Owner == owner).ToArray();
            foreach (var record in records) RefreshAncestors(record);
            return records.OrderBy(static record => record.Handle.ScopeId.Value).Select(Snapshot).ToArray();
        }
    }

    private void RefreshAncestors(Record record)
    {
        if (record.Parent is { } parentHandle && ResolveExact(parentHandle) is { IsSuccess: true, Value: { } parent })
        {
            RefreshAncestors(parent);
            if (parent.Requested && !record.Requested && !IsTerminal(record.Disposition))
            {
                record.Requested = true;
                SetDisposition(record, CancellationDisposition.CancellationRequested);
            }
        }
        RefreshDeadline(record);
    }

    private void RefreshDeadline(Record record)
    {
        if (record.EffectiveDeadline is not { } deadline || _timeProvider.GetTimestamp() < deadline.Timestamp || IsTerminal(record.Disposition))
            return;
        if (!record.Requested)
        {
            record.Requested = true;
            SetDisposition(record, CancellationDisposition.CancellationRequested);
            PropagateRequest(record.Handle);
        }
    }

    private void PropagateRequest(CancellationScopeHandle parent)
    {
        foreach (var child in _records.Values.Where(candidate => candidate.Parent == parent && !IsTerminal(candidate.Disposition)))
        {
            if (!child.Requested)
            {
                child.Requested = true;
                SetDisposition(child, CancellationDisposition.CancellationRequested);
            }
            PropagateRequest(child.Handle);
        }
    }

    private KernelResult<Record> ResolveForOwner(CancellationScopeHandle scope, ProcessHandle owner)
    {
        var resolved = ResolveExact(scope);
        if (!resolved.IsSuccess) return resolved;
        return resolved.Value!.Owner == owner
            ? resolved
            : KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Cancellation scope belongs to a different process generation.");
    }

    private KernelResult<Record> ResolveExact(CancellationScopeHandle scope)
    {
        if (!_records.TryGetValue(scope.ScopeId, out var record))
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Cancellation scope is unknown or stale.");
        return record.Handle == scope
            ? KernelResult<Record>.Ok(record)
            : KernelResult<Record>.Fail(KernelError.StaleGeneration, "Cancellation scope generation is stale.");
    }

    private static KernelResult<CancellationObservation> StaleOrFailure(
        CancellationScopeHandle scope,
        ProcessHandle owner,
        KernelResult<Record> failure) => failure.Error == KernelError.StaleGeneration
            ? KernelResult<CancellationObservation>.Ok(new(
                scope, owner, null, null, false, TimeoutDisposition.NotExpired,
                CancellationDisposition.Stale, 0))
            : KernelResult<CancellationObservation>.Fail(failure.Error, failure.Message!);

    private CancellationObservation Snapshot(Record record) => new(
        record.Handle,
        record.Owner,
        record.Parent,
        record.EffectiveDeadline,
        record.Requested,
        record.EffectiveDeadline is { } deadline && _timeProvider.GetTimestamp() >= deadline.Timestamp
            ? TimeoutDisposition.ExpiredWaitingMayStop
            : TimeoutDisposition.NotExpired,
        record.Disposition,
        record.Sequence);

    private static bool IsTerminal(CancellationDisposition disposition) => disposition is
        CancellationDisposition.CancelledBeforeEffect or
        CancellationDisposition.TooLateEffectMayExist or
        CancellationDisposition.CompletedBeforeCancellation or
        CancellationDisposition.ProviderEffectContained or
        CancellationDisposition.Unsupported;

    private static void SetDisposition(Record record, CancellationDisposition disposition)
    {
        if (record.Disposition == disposition) return;
        record.Disposition = disposition;
        record.Sequence++;
    }
}
