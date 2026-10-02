using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// Best-effort delivery of the lossless semantic projection after authoritative owner transitions.
/// Delivery state is observational only and is never consulted by execution or publication paths.
/// </summary>
public sealed partial class RuntimeKernel
{
    private sealed class ExternalOperationTraceRegistration(
        ISemanticTraceSinkV1 sink,
        string generationVectorDigest)
    {
        internal ISemanticTraceSinkV1 Sink { get; } = sink;
        internal string GenerationVectorDigest { get; } = generationVectorDigest;
        internal int ReservedEventCount { get; set; }
        internal bool DeliveryFailed { get; set; }
        internal bool DeliveryInProgress { get; set; }
        internal bool DeliveryRequested { get; set; }
        internal bool CompletionRequested { get; set; }
    }

    private readonly object _externalOperationTraceGate = new();
    private readonly Dictionary<ExternalOperationHandle, ExternalOperationTraceRegistration>
        _externalOperationTraceRegistrations = [];

    private void EmitProcessTeardownExternalOperationTraces(ProcessHandle process)
    {
        try
        {
            ExternalOperationHandle[] registered;
            lock (_externalOperationTraceGate)
                registered = _externalOperationTraceRegistrations.Keys.ToArray();
            if (registered.Length == 0) return;
            var resolved = Processes.Resolve(process);
            if (!resolved.IsSuccess) return;
            var principal = new RegionOwner(resolved.Value!.DomainId, process.Generation);
            // Snapshot committed owner facts, then deliver outside teardown/owner locks.
            // Existing registration interlock handles overlapping and reentrant delivery.
            foreach (var operation in registered)
            {
                var current = ExternalOperations.Query(operation);
                if (!current.IsSuccess || current.Value!.Principal != principal) continue;
                var snapshot = current.Value;
                EmitExternalOperationTrace(snapshot.Operation);
                if (snapshot.State == ExternalOperationState.Released)
                    CompleteExternalOperationTrace(snapshot.Operation);
                else
                    DiscardExternalOperationTraceIfPreSubmit(snapshot.Operation);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Observation cannot replace the authoritative teardown result.
        }
    }

    private void RegisterExternalOperationTrace(
        ExternalOperationHandle operation,
        string generationVectorDigest,
        ISemanticTraceSinkV1 sink)
    {
        try
        {
            lock (_externalOperationTraceGate)
            {
                if (_externalOperationTraceRegistrations.ContainsKey(operation))
                    return;

                var snapshot = ExternalOperations.Query(operation);
                if (!snapshot.IsSuccess)
                    return;
                // Projection validates the digest before any event can be delivered.
                _ = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
                    snapshot.Value!, generationVectorDigest);
                _externalOperationTraceRegistrations.Add(operation,
                    new ExternalOperationTraceRegistration(sink, generationVectorDigest));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Registration is observation only and cannot veto the managed submission.
        }
    }

    private void ObserveFailedPreSubmitTrace(ResourceAdmissionCommit commit,
        SemanticExecutionBindingV1 baseBinding, OperationObligationsV1 obligations,
        SemanticBindingExtensionSetV1 extensionBinding, ISemanticTraceSinkV1? sink,
        Func<ulong, KernelResult> validateCapturedSidecar)
    {
        if (sink is null) return;
        // This is attribution to the captured contour, never a fresh provider read
        // or admission. Only the existing submit/close winner invokes this callback.
        if (commit.Operation != baseBinding.Operation || commit.Operation != obligations.Operation ||
            commit.Principal != obligations.Principal || commit.Lease != baseBinding.Lease ||
            commit.BudgetOwner != baseBinding.BudgetOwner || commit.Envelope != baseBinding.ResourceEnvelope)
            return;
        var generation = extensionBinding.Generations.Single(item => item.Owner == "provider").Generation;
        if (!validateCapturedSidecar(generation).IsSuccess) return;
        var snapshot = ExternalOperations.Query(commit.Operation);
        var process = Processes.Resolve(commit.Principal);
        if (!snapshot.IsSuccess || !process.IsSuccess ||
            snapshot.Value!.Principal != new RegionOwner(process.Value!.DomainId, commit.Principal.Generation) ||
            snapshot.Value.Disposition != ExternalOperationDisposition.Cancelled ||
            snapshot.Value.EffectBoundary != ExternalEffectBoundaryState.NotCrossed ||
            snapshot.Value.Binding is not null || snapshot.Value.Transitions.Any(item => item.Event == "Submitted"))
            return;
        RegisterExternalOperationTrace(commit.Operation, extensionBinding.Digest.Value, sink);
        EmitExternalOperationTrace(commit.Operation);
    }

    private void EmitExternalOperationTrace(ExternalOperationHandle operation)
    {
        ExternalOperationTraceRegistration registration;
        lock (_externalOperationTraceGate)
        {
            if (!_externalOperationTraceRegistrations.TryGetValue(operation, out registration!))
                return;
            if (registration.DeliveryFailed)
                return;
            if (registration.DeliveryInProgress)
            {
                registration.DeliveryRequested = true;
                return;
            }
            registration.DeliveryInProgress = true;
            registration.DeliveryRequested = false;
        }
        var releasedDelivery = false;
        try
        {
            while (true)
            {
                IReadOnlyList<SemanticTraceEventV1> projection;
                try
                {
                    var snapshot = ExternalOperations.Query(operation);
                    if (!snapshot.IsSuccess)
                        return;
                    projection = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
                        snapshot.Value!, registration.GenerationVectorDigest);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    return;
                }

                SemanticTraceEventV1[] pending;
                lock (_externalOperationTraceGate)
                {
                    if (!_externalOperationTraceRegistrations.TryGetValue(operation, out var current) ||
                        !ReferenceEquals(current, registration) || registration.DeliveryFailed)
                        return;
                    if (projection.Count <= current.ReservedEventCount)
                    {
                        if (registration.DeliveryRequested)
                        {
                            registration.DeliveryRequested = false;
                            continue;
                        }
                        registration.DeliveryInProgress = false;
                        if (registration.CompletionRequested)
                            _externalOperationTraceRegistrations.Remove(operation);
                        releasedDelivery = true;
                        return;
                    }
                    pending = projection.Skip(current.ReservedEventCount).ToArray();
                    // Reserve before callbacks; a concurrent or reentrant Emit requests
                    // another owner read without waiting for an observation callback.
                    current.ReservedEventCount = projection.Count;
                    current.DeliveryRequested = false;
                }

                foreach (var traceEvent in pending)
                {
                    try
                    {
                        if (registration.Sink.TryRecord(traceEvent))
                            continue;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        // Observation is explicitly non-authoritative.
                    }
                    // A rejected event makes every later event an invalid suffix.
                    lock (_externalOperationTraceGate)
                        registration.DeliveryFailed = true;
                    return;
                }
            }
        }
        finally
        {
            if (!releasedDelivery)
            {
                lock (_externalOperationTraceGate)
                {
                    if (_externalOperationTraceRegistrations.TryGetValue(operation, out var current) &&
                        ReferenceEquals(current, registration))
                    {
                        registration.DeliveryInProgress = false;
                        if (registration.CompletionRequested)
                            _externalOperationTraceRegistrations.Remove(operation);
                    }
                }
            }
        }
    }

    private void DiscardExternalOperationTraceIfPreSubmit(ExternalOperationHandle operation)
    {
        try
        {
            var snapshot = ExternalOperations.Query(operation);
            // Cancellation is a stable no-submit boundary, but local release may
            // still follow. Retain observation until that actual owner transition.
            if (!snapshot.IsSuccess || snapshot.Value!.State != ExternalOperationState.Released ||
                snapshot.Value.Disposition != ExternalOperationDisposition.Cancelled ||
                snapshot.Value.Transitions.Any(
                    transition => transition.Event == "Submitted"))
                return;
            CompleteExternalOperationTrace(operation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Cleanup remains observational.
        }
    }

    private void CompleteExternalOperationTrace(ExternalOperationHandle operation)
    {
        lock (_externalOperationTraceGate)
        {
            if (!_externalOperationTraceRegistrations.TryGetValue(operation, out var registration))
                return;
            registration.CompletionRequested = true;
            if (registration.DeliveryInProgress)
            {
                registration.DeliveryRequested = true;
                return;
            }
            _externalOperationTraceRegistrations.Remove(operation);
        }
    }
}
