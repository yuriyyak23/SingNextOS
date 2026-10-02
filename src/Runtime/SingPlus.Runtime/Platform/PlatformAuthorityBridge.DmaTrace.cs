using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    // Observation only. DMA authority never reads this queue or its delivery state.
    internal sealed class DmaTraceObserver(
        ISemanticTraceSinkV1 sink, string correlation, string generationDigest,
        ulong providerGeneration, ulong backendEpoch)
    {
        public ISemanticTraceSinkV1 Sink { get; } = sink;
        public string Correlation { get; } = correlation;
        public string GenerationDigest { get; set; } = generationDigest;
        public ulong ProviderGeneration { get; set; } = providerGeneration;
        public ulong BackendEpoch { get; set; } = backendEpoch;
        public ulong NextSequence { get; set; } = 1;
        public Queue<SemanticTraceEventV1> Pending { get; } = [];
        public object DeliveryGate { get; } = new();
        public bool DeliveryInProgress { get; set; }
        public bool DeliveryFailed { get; set; }
        public SemanticTraceEventV1? LastQueuedEvent { get; set; }
    }

    private void RegisterDmaTraceLocked(DmaSubmissionRecord record, ISemanticTraceSinkV1 sink)
    {
        try
        {
            var binding = record.SemanticBinding!.Value;
            var digest = DmaTraceGenerationDigest(binding, binding.ProviderGeneration, _backendEpoch);
            var correlation = FormattableString.Invariant(
                $"dma:{record.Submission.OperationId.Value}:{record.Submission.Generation.Value}");
            var observer = new DmaTraceObserver(sink, correlation, digest,
                binding.ProviderGeneration, _backendEpoch);
            record.Trace = observer;
            QueueDmaTraceLocked(record, SemanticTraceEventKindV1.Submit);
            QueueDmaTraceLocked(record, SemanticTraceEventKindV1.EffectPossible);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            record.Trace = null;
        }
    }

    private static DmaTraceObserver? CreateAmbiguousDmaSubmitTraceLocked(
        ISemanticTraceSinkV1 sink, DmaExecutionBindingV1 binding,
        PlatformDmaOperationId operationId,
        PlatformProviderIncarnation submitIncarnation, PlatformBackendEpoch submitEpoch,
        PlatformProviderIncarnation observedIncarnation, PlatformBackendEpoch observedEpoch)
    {
        try
        {
            var correlation = FormattableString.Invariant($"dma:{operationId.Value}:1");
            var observer = new DmaTraceObserver(sink, correlation,
                DmaTraceGenerationDigest(binding, submitIncarnation.Value, submitEpoch.Value),
                submitIncarnation.Value, submitEpoch.Value);
            QueueDmaTraceLocked(observer, binding, SemanticTraceEventKindV1.Submit);
            QueueDmaTraceLocked(observer, binding, SemanticTraceEventKindV1.EffectPossible);
            if ((observedIncarnation.Value > 0 && observedIncarnation != submitIncarnation) ||
                (observedEpoch.Value > 0 && observedEpoch != submitEpoch))
                QueueDmaTraceLocked(observer, binding, SemanticTraceEventKindV1.GenerationChanged,
                    observedIncarnation, observedEpoch);
            QueueDmaTraceLocked(observer, binding, SemanticTraceEventKindV1.Quarantined);
            return observer;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return null;
        }
    }

    private static void QueueDmaTraceLocked(
        DmaSubmissionRecord record, SemanticTraceEventKindV1 kind,
        PlatformProviderIncarnation? providerIncarnation = null,
        PlatformBackendEpoch? backendEpoch = null)
    {
        var observer = record.Trace;
        if (observer is null) return;
        try
        {
            QueueDmaTraceLocked(observer, record.SemanticBinding!.Value, kind,
                providerIncarnation, backendEpoch);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            record.Trace = null;
        }
    }

    private static void QueueDmaTraceLocked(DmaTraceObserver observer,
        DmaExecutionBindingV1 binding, SemanticTraceEventKindV1 kind,
        PlatformProviderIncarnation? providerIncarnation = null,
        PlatformBackendEpoch? backendEpoch = null)
    {
        if (providerIncarnation is { Value: > 0 } incarnation)
            observer.ProviderGeneration = incarnation.Value;
        if (backendEpoch is { Value: > 0 } epoch)
            observer.BackendEpoch = epoch.Value;
        if (providerIncarnation is not null || backendEpoch is not null)
            observer.GenerationDigest = DmaTraceGenerationDigest(binding,
                observer.ProviderGeneration, observer.BackendEpoch);
        var sequence = observer.NextSequence++;
        var evidence = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant(
                $"dma-owner/v1|{observer.Correlation}|{sequence}|{(byte)kind}|{observer.GenerationDigest}"))));
        var item = new SemanticTraceEventV1(
            SemanticTraceEventV1.CurrentVersion, observer.Correlation, sequence, kind,
            "singnext.platform-dma", observer.GenerationDigest, evidence).Validate();
        observer.Pending.Enqueue(item);
        observer.LastQueuedEvent = item;
    }

    private void CaptureVisibleDmaTraceLocked(DmaSubmissionRecord record)
    {
        // One bounded observation per existing grant, overwritten on each exact visible cycle.
        var grant = _dmaGrants[record.Submission.GrantId];
        grant.LastVisibleTrace = record.Trace?.LastQueuedEvent;
        grant.LastVisibleProviderGeneration = record.Trace?.ProviderGeneration ?? 0;
        grant.LastVisibleBackendEpoch = record.Trace?.BackendEpoch ?? 0;
    }

    internal KernelResult<DmaGrantClosureObservationV1> QueryDmaGrantClosureObservation(
        PlatformDmaGrant grant, PlatformDomainIdentity subject)
    {
        lock (_dmaCompletionGate)
        {
            var identity = ValidateDmaGrantIdentity(grant, subject);
            if (!identity.IsSuccess)
                return KernelResult<DmaGrantClosureObservationV1>.Fail(identity.Error, identity.Message!);
            var record = _dmaGrants[grant.GrantId];
            if (!record.PlatformClosed)
                return KernelResult<DmaGrantClosureObservationV1>.Fail(KernelError.PlatformBindingDraining,
                    "Exact grant closure has not committed.");
            if (record.LastVisibleTrace is not { } visible ||
                record.LastVisibleProviderGeneration != record.ProviderIncarnation.Value ||
                record.LastVisibleBackendEpoch != record.ClosureBackendEpoch)
                return KernelResult<DmaGrantClosureObservationV1>.Fail(KernelError.PlatformUnsupported,
                    "No exact traced visibility prefix is available for this grant closure.");
            return KernelResult<DmaGrantClosureObservationV1>.Ok(new(
                DmaGrantClosureObservationV1.CurrentVersion, DmaGrantIdentityDigest(grant),
                record.ProviderIncarnation.Value, record.ClosureBackendEpoch, visible));
        }
    }

    internal static string DmaGrantIdentityDigest(PlatformDmaGrant grant) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"dma-grant/v1|{grant.GrantId.Value}|{grant.Generation.Value}|{grant.DeviceLease.LeaseId.Value}|{grant.DeviceLease.Generation.Value}|{grant.DeviceLease.DomainBinding.BindingId.Value}|{grant.DeviceLease.DomainBinding.Generation.Value}|{grant.DeviceLease.DomainBinding.Subject.DomainId.Value}|{grant.DeviceLease.DomainBinding.Subject.ProcessId.Value}|{grant.DeviceLease.DomainBinding.Subject.ProcessGeneration}|{grant.Mapping.Mapping.MappingId.Value}|{grant.Mapping.Mapping.Generation.Value}|{grant.Mapping.Region.RegionId.Value}|{grant.Mapping.Region.Generation.Value}|{grant.Mapping.Offset}|{grant.Mapping.Length}|{grant.Range.Offset}|{grant.Range.Length}|{(int)grant.Direction}"))));

    private static string DmaTraceGenerationDigest(
        DmaExecutionBindingV1 binding, ulong providerGeneration, ulong backendEpoch)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            (binding with
            {
                EffectState = DmaEffectStateV1.Admitted,
                ProviderGeneration = providerGeneration,
            }).SerializeCanonical()));
        return backendEpoch == 1 ? digest : Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"dma-owner/backend-epoch/v1|{digest}|{backendEpoch}"))));
    }

    internal DmaTraceObserver? CaptureDmaTrace(PlatformDmaSubmission submission)
    {
        lock (_dmaCompletionGate)
            return _activeDmaSubmissions.TryGetValue(submission.GrantId, out var record) &&
                       record.Submission == submission ? record.Trace : null;
    }

    internal void FlushDmaTrace(DmaTraceObserver? observer)
    {
        if (observer is null) return;
        lock (observer.DeliveryGate)
        {
            if (observer.DeliveryInProgress) return;
            observer.DeliveryInProgress = true;
            try
            {
                while (!observer.DeliveryFailed)
                {
                    SemanticTraceEventV1 next;
                    lock (_dmaCompletionGate)
                    {
                        if (observer.Pending.Count == 0) return;
                        next = observer.Pending.Dequeue();
                    }
                    try
                    {
                        if (observer.Sink.TryRecord(next)) continue;
                    }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    {
                        // A broken sink cannot alter DMA authority or receive a misleading suffix.
                    }
                    observer.DeliveryFailed = true;
                    return;
                }
            }
            finally { observer.DeliveryInProgress = false; }
        }
    }

    internal void FlushPendingDmaTraces()
    {
        try
        {
            DmaTraceObserver[] observers;
            lock (_dmaCompletionGate)
                observers = _activeDmaSubmissions.Values
                    .Select(static record => record.Trace)
                    .OfType<DmaTraceObserver>()
                    .ToArray();
            foreach (var observer in observers)
                FlushDmaTrace(observer);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            // Reset quarantine is authoritative even when observer enumeration fails.
        }
    }
}
