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
        observer.Pending.Enqueue(new SemanticTraceEventV1(
            SemanticTraceEventV1.CurrentVersion, observer.Correlation, sequence, kind,
            "singnext.platform-dma", observer.GenerationDigest, evidence).Validate());
    }

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
