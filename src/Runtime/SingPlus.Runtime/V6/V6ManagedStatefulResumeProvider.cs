using System.Security.Cryptography;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct V6ManagedCapturedStateHandle(Guid Id, ulong CaptureGeneration);

internal enum V6ManagedCapturedStateStatus
{
    Captured = 1,
    ResumeInFlight,
    Resumed,
    Discarded,
    Quarantined,
}

internal sealed record V6ManagedCapturedStateReceipt(
    V6ManagedCapturedStateHandle Handle,
    ResumeBindingV1 Binding,
    ulong CapturedStateBytes,
    V6ManagedCapturedStateStatus Status)
{
    internal bool AuthorizesResume => false;
    internal bool PreservesCapability => false;
}

/// <summary>
/// Deterministic managed state-capture model provider. It qualifies capture/restore
/// lifecycle and generation behavior only; it makes no physical safe-point latency claim.
/// </summary>
internal sealed class V6ManagedStatefulResumeProvider(
    string providerIdentity,
    ulong providerGeneration = 1,
    ulong runtimeGeneration = 1)
{
    private sealed class Record(V6ManagedCapturedStateReceipt receipt, byte[] payload)
    {
        internal V6ManagedCapturedStateReceipt Receipt { get; set; } = receipt;
        internal byte[] Payload { get; set; } = payload;
    }

    internal const ulong MaximumCapturedStateBytes = 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Record> _records = [];
    private ulong _providerGeneration = providerGeneration != 0 ? providerGeneration :
        throw new ArgumentOutOfRangeException(nameof(providerGeneration));
    private ulong _runtimeGeneration = runtimeGeneration != 0 ? runtimeGeneration :
        throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
    private ulong _nextCaptureGeneration;
    private bool _failNextRestore;
    private bool _failNextDiscard;
    private int _correlationQueries;

    internal string ProviderIdentity { get; } = ValidateToken(providerIdentity);
    internal ulong ProviderGeneration { get { lock (_sync) return _providerGeneration; } }
    internal ulong RuntimeGeneration { get { lock (_sync) return _runtimeGeneration; } }
    internal int CorrelationQueries { get { lock (_sync) return _correlationQueries; } }
    internal ulong RetainedPayloadBytes
    {
        get { lock (_sync) return _records.Values.Aggregate(0UL,
            static (total, record) => checked(total + (ulong)record.Payload.Length)); }
    }

    internal KernelResult<V6ManagedCapturedStateReceipt> Capture(
        string operationCorrelation,
        ulong operationGeneration,
        string semanticBindingDigest,
        ReadOnlyMemory<byte> opaqueState)
    {
        try
        {
            ValidateToken(operationCorrelation);
            ValidateDigest(semanticBindingDigest);
        }
        catch (ArgumentException exception)
        { return KernelResult<V6ManagedCapturedStateReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (operationGeneration == 0 || opaqueState.IsEmpty || (ulong)opaqueState.Length > MaximumCapturedStateBytes)
            return KernelResult<V6ManagedCapturedStateReceipt>.Fail(KernelError.InvalidMessage,
                "Managed captured state requires a non-zero operation generation and bounded non-empty payload.");

        byte[] payload = opaqueState.ToArray();
        var retained = false;
        try
        {
            string capturedDigest = Convert.ToHexStringLower(SHA256.HashData(payload));
            lock (_sync)
            {
                if (_records.Values.Any(record =>
                        record.Receipt.Binding.OperationCorrelation == operationCorrelation &&
                        record.Receipt.Status is V6ManagedCapturedStateStatus.Captured or
                            V6ManagedCapturedStateStatus.ResumeInFlight or
                            V6ManagedCapturedStateStatus.Quarantined))
                    return KernelResult<V6ManagedCapturedStateReceipt>.Fail(KernelError.DuplicateIdentity,
                        "The managed provider already holds live captured state for this operation correlation.");
                var captureGeneration = checked(++_nextCaptureGeneration);
                var binding = new ResumeBindingV1(ResumeBindingV1.CurrentVersion, operationCorrelation,
                    capturedDigest, semanticBindingDigest, operationGeneration, captureGeneration,
                    _providerGeneration, _runtimeGeneration).Validate();
                var handle = new V6ManagedCapturedStateHandle(Guid.NewGuid(), captureGeneration);
                var receipt = new V6ManagedCapturedStateReceipt(handle, binding,
                    checked((ulong)payload.Length), V6ManagedCapturedStateStatus.Captured);
                _records.Add(handle.Id, new(receipt, payload));
                retained = true;
                return KernelResult<V6ManagedCapturedStateReceipt>.Ok(receipt);
            }
        }
        finally { if (!retained) CryptographicOperations.ZeroMemory(payload); }
    }

    internal KernelResult AdmitRestore(V6ManagedCapturedStateHandle handle, ResumeBindingV1 binding)
    {
        lock (_sync)
        {
            var record = Resolve(handle, binding);
            if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
            return record.Value!.Receipt.Status == V6ManagedCapturedStateStatus.Captured &&
                   binding.ProviderGeneration == _providerGeneration && binding.RuntimeGeneration == _runtimeGeneration
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.StaleGeneration,
                    "Managed captured state is not current and restorable.");
        }
    }

    internal KernelResult Restore(V6ManagedCapturedStateHandle handle, ResumeBindingV1 binding)
    {
        Record record;
        lock (_sync)
        {
            var resolved = Resolve(handle, binding);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            if (record.Receipt.Status != V6ManagedCapturedStateStatus.Captured ||
                binding.ProviderGeneration != _providerGeneration || binding.RuntimeGeneration != _runtimeGeneration)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Managed captured state changed before restore.");
            record.Receipt = record.Receipt with { Status = V6ManagedCapturedStateStatus.ResumeInFlight };
            if (_failNextRestore)
            {
                _failNextRestore = false;
                record.Receipt = record.Receipt with { Status = V6ManagedCapturedStateStatus.Quarantined };
                return KernelResult.Fail(KernelError.PlatformUnavailable, "Injected managed restore loss.");
            }
            if (Convert.ToHexStringLower(SHA256.HashData(record.Payload)) != binding.CapturedStateDigest)
            {
                record.Receipt = record.Receipt with { Status = V6ManagedCapturedStateStatus.Quarantined };
                return KernelResult.Fail(KernelError.CheckpointInvalid,
                    "Managed captured-state digest changed before restore.");
            }
            CryptographicOperations.ZeroMemory(record.Payload);
            record.Payload = [];
            record.Receipt = record.Receipt with { Status = V6ManagedCapturedStateStatus.Resumed };
            return KernelResult.Ok();
        }
    }

    internal KernelResult<V6ManagedCapturedStateReceipt> Query(V6ManagedCapturedStateHandle handle)
    {
        lock (_sync)
        {
            if (handle.Id == Guid.Empty || !_records.TryGetValue(handle.Id, out var record) ||
                record.Receipt.Handle != handle)
                return KernelResult<V6ManagedCapturedStateReceipt>.Fail(KernelError.StaleGeneration,
                    "Managed captured-state handle is missing or stale.");
            return KernelResult<V6ManagedCapturedStateReceipt>.Ok(record.Receipt);
        }
    }

    internal KernelResult<V6ManagedCapturedStateReceipt> QueryByCorrelation(string operationCorrelation)
    {
        lock (_sync)
        {
            _correlationQueries++;
            var matches = _records.Values.Where(record =>
                record.Receipt.Binding.OperationCorrelation == operationCorrelation)
                .OrderByDescending(static record => record.Receipt.Handle.CaptureGeneration).ToArray();
            return matches.Length == 0
                ? KernelResult<V6ManagedCapturedStateReceipt>.Fail(KernelError.StaleGeneration,
                    "Managed captured-state correlation is missing.")
                : KernelResult<V6ManagedCapturedStateReceipt>.Ok(matches[0].Receipt);
        }
    }

    internal KernelResult<V6ManagedCapturedStateReceipt> QueryByCorrelation(
        string operationCorrelation, ResumeBindingV1 expectedBinding)
    {
        try { expectedBinding.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6ManagedCapturedStateReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
        lock (_sync)
        {
            _correlationQueries++;
            var matches = _records.Values.Where(record =>
                record.Receipt.Binding.OperationCorrelation == operationCorrelation &&
                record.Receipt.Binding == expectedBinding).ToArray();
            return matches.Length == 1
                ? KernelResult<V6ManagedCapturedStateReceipt>.Ok(matches[0].Receipt)
                : KernelResult<V6ManagedCapturedStateReceipt>.Fail(
                    matches.Length == 0 ? KernelError.StaleGeneration : KernelError.DuplicateIdentity,
                    "No unique managed captured state matches the owner-validated recovery binding.");
        }
    }

    internal KernelResult Discard(V6ManagedCapturedStateHandle handle, ResumeBindingV1 binding)
    {
        lock (_sync)
        {
            var resolved = Resolve(handle, binding);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.Receipt.Status is V6ManagedCapturedStateStatus.ResumeInFlight or V6ManagedCapturedStateStatus.Resumed)
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "In-flight or restored managed state cannot be discarded as captured state.");
            if (_failNextDiscard)
            {
                _failNextDiscard = false;
                resolved.Value.Receipt = resolved.Value.Receipt with
                { Status = V6ManagedCapturedStateStatus.Quarantined };
                return KernelResult.Fail(KernelError.PlatformUnavailable,
                    "Injected managed discard loss.");
            }
            resolved.Value.Receipt = resolved.Value.Receipt with { Status = V6ManagedCapturedStateStatus.Discarded };
            CryptographicOperations.ZeroMemory(resolved.Value.Payload);
            resolved.Value.Payload = [];
            return KernelResult.Ok();
        }
    }

    internal void ResetProvider()
    {
        lock (_sync)
        {
            _providerGeneration = checked(_providerGeneration + 1);
            QuarantineLive();
        }
    }

    internal void ResetRuntime()
    {
        lock (_sync)
        {
            _runtimeGeneration = checked(_runtimeGeneration + 1);
            QuarantineLive();
        }
    }

    internal void FailNextRestoreForTest()
    {
        lock (_sync) _failNextRestore = true;
    }

    internal void FailNextDiscardForTest()
    {
        lock (_sync) _failNextDiscard = true;
    }

    private KernelResult<Record> Resolve(V6ManagedCapturedStateHandle handle, ResumeBindingV1 binding)
    {
        try { binding.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<Record>.Fail(KernelError.InvalidMessage, exception.Message); }
        return handle.Id != Guid.Empty && _records.TryGetValue(handle.Id, out var record) &&
               record.Receipt.Handle == handle && record.Receipt.Binding == binding
            ? KernelResult<Record>.Ok(record)
            : KernelResult<Record>.Fail(KernelError.StaleGeneration,
                "Managed captured-state handle or binding is stale.");
    }

    private void QuarantineLive()
    {
        foreach (var record in _records.Values.Where(static record =>
                     record.Receipt.Status is V6ManagedCapturedStateStatus.Captured or V6ManagedCapturedStateStatus.ResumeInFlight))
            record.Receipt = record.Receipt with { Status = V6ManagedCapturedStateStatus.Quarantined };
    }

    private static string ValidateToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) || value.Length > 256)
            throw new ArgumentException("Managed provider identity or operation correlation is not canonical.");
        return value;
    }

    private static void ValidateDigest(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Managed semantic binding digest must be canonical SHA-256 hex.");
    }
}

internal sealed class V6ManagedStatefulResumeContour(
    V6StatefulResumeAccounting accounting,
    V6ManagedStatefulResumeProvider provider)
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, V6ManagedCapturedStateReceipt> _providerBindings = [];
    private bool _failNextBindingPublicationForTest;

    internal void FailNextBindingPublicationForTest()
    {
        lock (_sync) _failNextBindingPublicationForTest = true;
    }

    internal KernelResult<V6StatefulSuspensionReceipt> CaptureAndSuspend(
        ProcessHandle owner,
        string operationCorrelation,
        ulong operationGeneration,
        string semanticBindingDigest,
        ReadOnlyMemory<byte> opaqueState)
    {
        var budgetOwner = accounting.ValidateCaptureBudgetOwner(owner);
        if (!budgetOwner.IsSuccess)
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(budgetOwner.Error,
                budgetOwner.Message!);
        var captured = provider.Capture(operationCorrelation, operationGeneration,
            semanticBindingDigest, opaqueState);
        if (!captured.IsSuccess)
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(captured.Error, captured.Message!);
        var receipt = captured.Value!;
        var admitted = accounting.AdmitCapturedState(owner, receipt.Binding,
            receipt.CapturedStateBytes, () => provider.ProviderGeneration, () => provider.RuntimeGeneration);
        if (!admitted.IsSuccess)
        {
            var discarded = provider.Discard(receipt.Handle, receipt.Binding);
            return discarded.IsSuccess
                ? admitted
                : KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.PlatformFaulted,
                    "Storage admission failed and provider capture cleanup is unproven; the captured payload remains quarantined.");
        }
        try
        {
            lock (_sync)
            {
                if (_failNextBindingPublicationForTest)
                {
                    _failNextBindingPublicationForTest = false;
                    throw new ArgumentException("Injected binding publication failure.");
                }
                _providerBindings.Add(admitted.Value!.Handle.Id, receipt);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        {
            var discarded = accounting.DiscardWithProvider(owner, admitted.Value!.Handle,
                () => provider.Discard(receipt.Handle, receipt.Binding));
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(
                discarded.IsSuccess ? KernelError.CapacityExhausted : KernelError.PlatformFaulted,
                discarded.IsSuccess ? exception.Message :
                    "Binding publication failed and provider capture cleanup is unproven; storage remains quarantined.");
        }
        return admitted;
    }

    internal KernelResult<V6StatefulSuspensionReceipt> Resume(
        ProcessHandle owner,
        V6StatefulSuspensionHandle suspension,
        Func<KernelResult> singNextAdmission,
        Func<KernelResult> runtimeLegality)
    {
        V6ManagedCapturedStateReceipt captured;
        lock (_sync)
            if (!_providerBindings.TryGetValue(suspension.Id, out captured!))
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "Managed provider binding for the suspension is missing.");
        return accounting.Resume(owner, suspension, singNextAdmission,
            () => provider.AdmitRestore(captured.Handle, captured.Binding), runtimeLegality,
            () => provider.ProviderGeneration, () => provider.RuntimeGeneration,
            () => provider.Restore(captured.Handle, captured.Binding));
    }

    internal KernelResult<V6StatefulSuspensionReceipt> Discard(
        ProcessHandle owner, V6StatefulSuspensionHandle suspension)
    {
        V6ManagedCapturedStateReceipt captured;
        lock (_sync)
            if (!_providerBindings.TryGetValue(suspension.Id, out captured!))
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "Managed provider binding for the suspension is missing.");
        return accounting.DiscardWithProvider(owner, suspension,
            () => provider.Discard(captured.Handle, captured.Binding));
    }

    internal KernelResult<V6StatefulSuspensionReceipt> ReconcileQuarantinedDiscard(
        ProcessHandle owner, V6StatefulSuspensionHandle suspension)
    {
        V6ManagedCapturedStateReceipt captured;
        lock (_sync)
            if (!_providerBindings.TryGetValue(suspension.Id, out captured!))
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "Managed provider binding for the suspension is missing.");
        return accounting.ReconcileQuarantinedDiscardWithProvider(owner, suspension,
            () => provider.Discard(captured.Handle, captured.Binding));
    }

    internal KernelResult<V6StatefulSuspensionReceipt> ReconcileUnpublishedCapture(
        ProcessHandle owner, string operationCorrelation)
    {
        var owned = accounting.FindRecoverySuspensionByCorrelation(owner, operationCorrelation);
        if (!owned.IsSuccess)
            return owned;
        var captured = provider.QueryByCorrelation(operationCorrelation,
            owned.Value!.ResumeBinding);
        if (!captured.IsSuccess)
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(captured.Error, captured.Message!);
        var providerReceipt = captured.Value!;
        if (providerReceipt.Status is not (V6ManagedCapturedStateStatus.Quarantined or
            V6ManagedCapturedStateStatus.Discarded))
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                "Only a quarantined or previously discarded provider capture can be reconciled.");
        var suspension = accounting.FindRecoverySuspension(owner, providerReceipt.Binding);
        if (!suspension.IsSuccess)
            return suspension;
        if (suspension.Value!.Handle != owned.Value!.Handle)
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                "Provider capture does not match the owner-validated recovery suspension.");
        lock (_sync)
            if (_providerBindings.ContainsKey(suspension.Value!.Handle.Id))
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                    "Published captures use their exact suspension handle for reconciliation.");
        return accounting.ReconcileQuarantinedDiscardWithProvider(owner,
            suspension.Value!.Handle,
            () => provider.Discard(providerReceipt.Handle, providerReceipt.Binding));
    }
}
