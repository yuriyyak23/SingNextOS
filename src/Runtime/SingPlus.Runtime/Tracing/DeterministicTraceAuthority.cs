using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal sealed class DeterministicTraceAuthority
{
    internal readonly record struct TelemetrySummary(int Sessions, int BufferedEvents, ulong DroppedEvents);
    private sealed class ProducerRecord(TraceProducerHandle handle)
    {
        public object Gate { get; } = new();
        public TraceProducerHandle Handle { get; } = handle;
        public Queue<SemanticTraceEvent> Events { get; } = new();
        public ulong NextSequence { get; set; } = 1;
        public ulong Dropped { get; set; }
    }

    private sealed class SessionRecord(TraceSessionAdmission admission, ProducerRecord producer)
    {
        public TraceSessionAdmission Admission { get; set; } = admission;
        public ProducerRecord Producer { get; } = producer;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TraceSessionId, SessionRecord> _sessions = [];
    private readonly TimeProvider _timeProvider;
    private SessionRecord[] _active = [];
    private ulong _nextSessionId = 1;
    private ulong _nextProducerId = 1;

    internal DeterministicTraceAuthority(TimeProvider timeProvider) => _timeProvider = timeProvider;

    internal KernelResult<TraceSessionAdmission> Start(
        ProcessHandle owner,
        int capacity,
        TraceOverflowPolicy overflow,
        TraceVisibilityClass visibility)
    {
        if (capacity <= 0 || capacity > DeterministicTraceContract.MaximumProducerCapacity ||
            !Enum.IsDefined(overflow) || !Enum.IsDefined(visibility))
            return KernelResult<TraceSessionAdmission>.Fail(KernelError.InvalidMessage, "Trace configuration is invalid or unbounded.");

        lock (_gate)
        {
            if (_nextSessionId == 0 || _nextProducerId == 0)
                return KernelResult<TraceSessionAdmission>.Fail(KernelError.CapacityExhausted, "Trace identity space is exhausted.");
            var session = new TraceSessionHandle(new(_nextSessionId++), new(1));
            var producerHandle = new TraceProducerHandle(session, new(_nextProducerId++), new(1));
            var admission = new TraceSessionAdmission(session, producerHandle, owner, visibility, overflow, capacity, TraceSessionState.Active);
            var record = new SessionRecord(admission, new ProducerRecord(producerHandle));
            _sessions.Add(session.SessionId, record);
            PublishActiveLocked();
            return KernelResult<TraceSessionAdmission>.Ok(admission);
        }
    }

    internal KernelResult Record(
        ProcessHandle subject,
        TraceEventKind kind,
        CausalCorrelationId correlation,
        CausalCorrelationId? parent,
        TraceSemanticData data)
    {
        if (!Enum.IsDefined(kind) || !Valid(correlation.Value) ||
            (parent is { } parentValue && !Valid(parentValue.Value)) ||
            !Valid(data.ResourceClass) || !Valid(data.ResourceCorrelation) ||
            !Valid(data.State) || !Valid(data.Outcome) ||
            (data.MetadataDigest is { } digest && !Valid(digest)))
            return KernelResult.Fail(KernelError.InvalidMessage, "Trace semantic metadata must be non-empty and bounded; payloads and credentials are not accepted.");
        var active = Volatile.Read(ref _active);
        if (active.Length == 0) return KernelResult.Ok();
        KernelResult result = KernelResult.Ok();
        foreach (var session in active)
        {
            if (session.Admission.Owner != subject) continue;
            var recorded = RecordOne(session, subject, kind, correlation, parent, data);
            if (!recorded.IsSuccess) result = recorded;
        }
        return result;
    }

    private KernelResult RecordOne(SessionRecord session, ProcessHandle subject, TraceEventKind kind,
        CausalCorrelationId correlation, CausalCorrelationId? parent, TraceSemanticData data)
    {
        var producer = session.Producer;
        lock (producer.Gate)
        {
            if (session.Admission.State != TraceSessionState.Active)
                return KernelResult.Fail(KernelError.TraceStopped, "Trace session is stopped.");
            if (producer.Events.Count >= session.Admission.ProducerCapacity)
            {
                if (session.Admission.OverflowPolicy == TraceOverflowPolicy.BackpressureTestMode)
                    return KernelResult.Fail(KernelError.TraceBackpressure, "The bounded trace producer is full.");
                if (session.Admission.OverflowPolicy == TraceOverflowPolicy.StopSession)
                {
                    producer.Dropped++;
                    session.Admission = session.Admission with { State = TraceSessionState.OverflowStopped };
                    return KernelResult.Fail(KernelError.TraceStopped, "Trace session stopped on overflow.");
                }

                producer.Dropped++;
                producer.Events.Dequeue();
                producer.Events.Enqueue(NewEvent(producer, subject, TraceEventKind.BufferOverflow,
                    correlation, parent, new("trace-buffer", session.Admission.Session.SessionId.Value.ToString(),
                        "incomplete", "drop-with-marker"), incomplete: true));
                return KernelResult.Ok();
            }

            producer.Events.Enqueue(NewEvent(producer, subject, kind, correlation, parent, data, incomplete: false));
            return KernelResult.Ok();
        }
    }

    private SemanticTraceEvent NewEvent(ProducerRecord producer, ProcessHandle subject, TraceEventKind kind,
        CausalCorrelationId correlation, CausalCorrelationId? parent, TraceSemanticData data, bool incomplete)
    {
        if (producer.NextSequence == 0) throw new InvalidOperationException("Trace producer sequence space is exhausted.");
        return new(producer.Handle, new(producer.NextSequence++), _timeProvider.GetTimestamp(), kind,
            correlation, parent, subject, data, incomplete);
    }

    internal KernelResult<TraceSnapshot> Snapshot(TraceSessionHandle handle)
    {
        lock (_gate)
        {
            var resolved = ResolveLocked(handle);
            if (!resolved.IsSuccess) return KernelResult<TraceSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            lock (record.Producer.Gate)
                return KernelResult<TraceSnapshot>.Ok(new(record.Admission, record.Producer.Events.ToArray(),
                    record.Producer.Dropped, record.Producer.Dropped == 0));
        }
    }

    internal KernelResult<TraceSessionAdmission> Stop(ProcessHandle owner, TraceSessionHandle handle)
    {
        lock (_gate)
        {
            var resolved = ResolveLocked(handle);
            if (!resolved.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.Admission.Owner != owner)
                return KernelResult<TraceSessionAdmission>.Fail(KernelError.ProjectionDenied, "Trace session belongs to another process generation.");
            resolved.Value.Admission = resolved.Value.Admission with { State = TraceSessionState.Stopped };
            PublishActiveLocked();
            return KernelResult<TraceSessionAdmission>.Ok(resolved.Value.Admission);
        }
    }

    internal TraceSessionHandle[] StopAllForProcess(ProcessHandle owner)
    {
        lock (_gate)
        {
            var stopped = _sessions.Values
                .Where(record => record.Admission.Owner == owner && record.Admission.State == TraceSessionState.Active)
                .ToArray();
            foreach (var record in stopped)
                record.Admission = record.Admission with { State = TraceSessionState.Stopped };
            if (stopped.Length != 0) PublishActiveLocked();
            return stopped.Select(static record => record.Admission.Session).ToArray();
        }
    }

    internal TelemetrySummary InspectionSummary(ProcessHandle owner)
    {
        lock (_gate)
        {
            var selected = _sessions.Values.Where(record => record.Admission.Owner == owner).ToArray();
            var buffered = 0;
            ulong dropped = 0;
            foreach (var record in selected)
            {
                lock (record.Producer.Gate)
                {
                    buffered = checked(buffered + record.Producer.Events.Count);
                    dropped = checked(dropped + record.Producer.Dropped);
                }
            }
            return new(selected.Length, buffered, dropped);
        }
    }

    private KernelResult<SessionRecord> ResolveLocked(TraceSessionHandle handle)
    {
        if (!_sessions.TryGetValue(handle.SessionId, out var record))
            return KernelResult<SessionRecord>.Fail(KernelError.TraceSessionNotFound, "Trace session does not exist.");
        return record.Admission.Session == handle
            ? KernelResult<SessionRecord>.Ok(record)
            : KernelResult<SessionRecord>.Fail(KernelError.StaleGeneration, "Trace session generation is stale.");
    }

    private void PublishActiveLocked() =>
        Volatile.Write(ref _active, _sessions.Values.Where(static x => x.Admission.State == TraceSessionState.Active).ToArray());

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}
