using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// SingNextOS-owned adapter for the versioned HybridCPU external-operation ABI.
/// It translates semantic requests into the existing kernel authority lifecycle;
/// it contains no CXL topology and never derives success from transport absence.
/// </summary>
public sealed class HybridCpuExternalOperationProvider : Hc.IExternalOperationProvider,
    Hc.IExternalOperationCancellationProvider
{
    private sealed class Entry(
        Hc.ExternalOperationRequest request,
        ExternalOperationHandle operation,
        OperationBinding? binding)
    {
        public Hc.ExternalOperationRequest Request { get; } = request;
        public ExternalOperationHandle Operation { get; } = operation;
        public OperationBinding? Binding { get; set; } = binding;
        public Hc.ExternalOperationStage LastDelivered { get; set; } = Hc.ExternalOperationStage.Admitted;
    }

    private readonly object gate = new();
    private readonly RuntimeKernel kernel;
    private readonly ProcessHandle principal;
    private readonly IReadOnlyList<OperationRegionUseRequest> regionUses;
    private readonly OperationDependencySnapshot dependencies;
    private readonly ExternalServiceIdentity service;
    private readonly Hc.ExternalDomainLease scope;
    private Hc.ExternalGenerationSet generations;
    private Hc.ExternalGenerationSet? pendingGenerations;
    private int providerEffectsInFlight;
    private readonly Dictionary<Hc.ExternalRequestCorrelation, Entry> entries = [];

    public HybridCpuExternalOperationProvider(
        RuntimeKernel kernel,
        ProcessHandle principal,
        IReadOnlyList<OperationRegionUseRequest> regionUses,
        OperationDependencySnapshot dependencies,
        ExternalServiceIdentity service,
        Hc.ExternalDomainLease scope,
        Hc.ExternalGenerationSet generations)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.principal = principal;
        this.regionUses = regionUses?.ToArray() ?? throw new ArgumentNullException(nameof(regionUses));
        if (this.regionUses.Count == 0) throw new ArgumentException("At least one exact region use is required.", nameof(regionUses));
        this.dependencies = dependencies;
        this.service = service;
        this.scope = scope;
        this.generations = generations ?? throw new ArgumentNullException(nameof(generations));
        if (generations.ContractVersion != Hc.ExternalOperationContract.Version)
            throw new ArgumentException("Unsupported generation schema.", nameof(generations));
    }

    public Hc.ExternalOperationProviderPollResult Admit(Hc.ExternalOperationSemanticRequest semantic)
    {
        ArgumentNullException.ThrowIfNull(semantic);
        lock (gate)
        {
            if (semantic.ContractVersion != Hc.ExternalOperationContract.Version ||
                !Enum.IsDefined(semantic.EffectClass) ||
                !Enum.IsDefined(semantic.VisibilityRequirement) ||
                !Enum.IsDefined(semantic.CancellationMode) ||
                !Enum.IsDefined(semantic.ReplayEffectClass))
                return FaultWithoutAuthority(semantic);
            if (entries.ContainsKey(semantic.Correlation))
                return StaleFor(entries[semantic.Correlation].Request);

            var publication = semantic.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? ExternalPublicationPolicy.DirectCoherent
                : ExternalPublicationPolicy.Staged;
            var visibility = semantic.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? SingPlus.Contracts.ExternalVisibilityRequirement.ConsumerDomain
                : SingPlus.Contracts.ExternalVisibilityRequirement.PublicationFence;
            var effect = TryMapEffect(semantic.ReplayEffectClass);
            if (effect is null) return FaultWithoutAuthority(semantic);
            var prepared = kernel.PrepareExternalOperation(principal, regionUses, visibility, publication, effect);
            if (!prepared.IsSuccess)
                return FaultWithoutAuthority(semantic);
            var admitted = kernel.AdmitExternalOperation(principal, prepared.Value!.Operation, dependencies, service,
                semantic.CancellationMode == Hc.ExternalCancellationMode.Unsupported
                    ? ExternalCancellationSupport.BeforeSubmissionOnly
                    : ExternalCancellationSupport.ProviderCooperative);
            if (!admitted.IsSuccess)
            {
                _ = kernel.CancelExternalOperation(principal, prepared.Value.Operation, false);
                _ = kernel.ReleaseExternalOperation(principal, prepared.Value.Operation, new(false, false));
                return FaultWithoutAuthority(semantic);
            }

            var request = new Hc.ExternalOperationRequest(
                new(new(Guid.NewGuid()), new(prepared.Value.Operation.Generation.Value)), scope,
                semantic.ContractVersion, generations, semantic.Correlation, semantic.EffectClass,
                semantic.VisibilityRequirement, semantic.CancellationMode);
            entries.Add(semantic.Correlation, new Entry(request, prepared.Value.Operation, null));
            return Receipt(new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Succeeded));
        }
    }

    public Hc.ExternalOperationProviderPollResult Submit(Hc.ExternalOperationRequest request)
    {
        lock (gate)
        {
            if (!TryExact(request, out var entry)) return StaleFor(request);
            if (!request.Generations.Equals(generations)) return StaleFor(request);
            var submitted = kernel.RecordExternalOperationSubmission(principal, entry.Operation, dependencies);
            if (!submitted.IsSuccess)
                return Receipt(new Hc.ExternalOperationProgressReceipt(request,
                    Hc.ExternalOperationStage.Submitted, Hc.ExternalRuntimeOutcome.Faulted));
            entry.Binding = submitted.Value!;
            entry.LastDelivered = Hc.ExternalOperationStage.Submitted;
            return Receipt(new Hc.ExternalOperationProgressReceipt(request,
                Hc.ExternalOperationStage.Submitted, Hc.ExternalRuntimeOutcome.Succeeded));
        }
    }

    public Hc.ExternalOperationProviderPollResult Poll(Hc.ExternalOperationRequest request)
    {
        lock (gate)
        {
            if (!TryExact(request, out var entry) || !request.Generations.Equals(generations))
                return StaleFor(request);
            if (entry.LastDelivered == Hc.ExternalOperationStage.Released) return StaleFor(request);
            var queried = kernel.QueryExternalOperation(principal, entry.Operation);
            if (!queried.IsSuccess) return StaleFor(request);
            if (queried.Value!.Disposition == ExternalOperationDisposition.ProviderLost)
                return Failure(request, Hc.ExternalOperationProviderPollStatus.Unavailable,
                    MapStage(queried.Value.State), Hc.ExternalRuntimeOutcome.Unknown);
            if (queried.Value.Disposition is ExternalOperationDisposition.Faulted or
                ExternalOperationDisposition.Discarded or ExternalOperationDisposition.Cancelled)
                return Failure(request, Hc.ExternalOperationProviderPollStatus.Faulted,
                    MapStage(queried.Value.State), Hc.ExternalRuntimeOutcome.Faulted);
            var stage = MapStage(queried.Value!.State);
            if (stage == entry.LastDelivered)
                return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
            if (stage == Hc.ExternalOperationStage.Failed || stage < entry.LastDelivered ||
                queried.Value.State == ExternalOperationState.Released &&
                queried.Value.Disposition != ExternalOperationDisposition.Published)
                return Receipt(new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Faulted));
            var next = (Hc.ExternalOperationStage)((byte)entry.LastDelivered + 1);
            entry.LastDelivered = next;
            return Receipt(CreateReceipt(request, next));
        }
    }

    public Hc.ExternalOperationProviderPollResult Cancel(Hc.ExternalOperationRequest request)
    {
        _ = RequestCancellation(request);
        return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
    }

    public Hc.ExternalOperationCancellationReceipt RequestCancellation(Hc.ExternalOperationRequest request)
    {
        lock (gate)
        {
            if (!TryExact(request, out var entry) || !request.Generations.Equals(generations))
                return new(request, Hc.ExternalOperationCancellationOutcome.Stale, generations);
            var state = kernel.QueryExternalOperation(principal, entry.Operation);
            if (!state.IsSuccess)
                return new(request, Hc.ExternalOperationCancellationOutcome.Stale, generations);
            bool submitted = state.Value!.State >= ExternalOperationState.Submitted;
            var cancelled = kernel.CancelExternalOperation(principal, entry.Operation, providerCancellationSupported: true);
            if (!cancelled.IsSuccess)
                return new(request, Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
            return new(request, submitted
                ? Hc.ExternalOperationCancellationOutcome.ConfirmedTerminalAfterSubmit
                : Hc.ExternalOperationCancellationOutcome.ConfirmedBeforeSubmit, generations);
        }
    }

    public KernelResult RecordDeviceCompletion(Hc.ExternalRequestCorrelation correlation,
        ExternalOperationCompletionDisposition disposition = ExternalOperationCompletionDisposition.Completed)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(correlation, out var entry) || entry.Binding is null)
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact submitted operation is required.");
            if (!entry.Request.Generations.Equals(generations))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before completion.");
            var result = kernel.RecordExternalOperationCompletion(principal, new(entry.Binding.Value, disposition));
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
    }

    public KernelResult RecordVisibility(Hc.ExternalRequestCorrelation correlation, bool satisfied = true)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(correlation, out var entry) || entry.Binding is null)
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact submitted operation is required.");
            if (!entry.Request.Generations.Equals(generations))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before visibility.");
            var requirement = entry.Request.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? SingPlus.Contracts.ExternalVisibilityRequirement.ConsumerDomain
                : SingPlus.Contracts.ExternalVisibilityRequirement.PublicationFence;
            var result = kernel.RecordExternalOperationVisibility(principal,
                new(entry.Binding.Value, requirement, satisfied));
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
    }

    public KernelResult Publish(Hc.ExternalRequestCorrelation correlation, Action publicationAction)
    {
        ArgumentNullException.ThrowIfNull(publicationAction);
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(correlation, out entry!))
                return KernelResult.Fail(KernelError.ExternalOperationNotFound, "Operation correlation was not admitted.");
            if (!entry.Request.Generations.Equals(generations))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before publication.");
            providerEffectsInFlight++;
        }
        try
        {
            var policy = entry.Request.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? ExternalPublicationPolicy.DirectCoherent
                : ExternalPublicationPolicy.Staged;
            var result = kernel.PublishExternalOperation(principal, entry.Operation, dependencies,
                new(policy), publicationAction);
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
        finally
        {
            lock (gate)
            {
                providerEffectsInFlight--;
                if (providerEffectsInFlight == 0 && pendingGenerations is { } pending)
                {
                    generations = pending;
                    pendingGenerations = null;
                }
            }
        }
    }

    public KernelResult Release(Hc.ExternalRequestCorrelation correlation, bool providerResourcesClosed,
        bool providerUnavailable = false, bool providerEffectContained = false)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(correlation, out var entry))
                return KernelResult.Fail(KernelError.ExternalOperationNotFound, "Operation correlation was not admitted.");
            if (!entry.Request.Generations.Equals(generations))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before release.");
            if (providerUnavailable)
            {
                var current = kernel.QueryExternalOperation(principal, entry.Operation);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (current.Value!.Disposition != ExternalOperationDisposition.ProviderLost)
                {
                    var lost = kernel.RecordExternalOperationProviderLoss(principal, entry.Operation);
                    if (!lost.IsSuccess) return KernelResult.Fail(lost.Error, lost.Message!);
                }
            }
            var result = kernel.ReleaseExternalOperation(principal, entry.Operation,
                new(providerResourcesClosed, providerUnavailable, providerEffectContained));
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
    }

    public void Reconfigure(Hc.ExternalGenerationSet currentGenerations)
    {
        ArgumentNullException.ThrowIfNull(currentGenerations);
        if (currentGenerations.ContractVersion != Hc.ExternalOperationContract.Version)
            throw new ArgumentException("Unsupported generation schema.", nameof(currentGenerations));
        lock (gate)
        {
            if (providerEffectsInFlight == 0) generations = currentGenerations;
            else pendingGenerations = currentGenerations;
        }
    }

    private bool TryExact(Hc.ExternalOperationRequest request, out Entry entry) =>
        entries.TryGetValue(request.Correlation, out entry!) && entry.Request == request;

    private Hc.ExternalOperationProviderPollResult Receipt(Hc.ExternalOperationReceipt receipt) =>
        new(Hc.ExternalOperationProviderPollStatus.Receipt, generations, receipt);

    private Hc.ExternalOperationProviderPollResult StaleFor(Hc.ExternalOperationRequest request) =>
        new(Hc.ExternalOperationProviderPollStatus.Stale, generations,
            new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Stale));

    private Hc.ExternalOperationProviderPollResult FaultWithoutAuthority(Hc.ExternalOperationSemanticRequest semantic)
    {
        var placeholder = new Hc.ExternalOperationRequest(new(new(Guid.NewGuid()), new(1)), scope,
            Hc.ExternalOperationContract.Version, generations, semantic.Correlation,
            Hc.ExternalEffectClass.NonIdempotent, Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.Unsupported);
        return new(Hc.ExternalOperationProviderPollStatus.Faulted, generations,
            new Hc.ExternalOperationAdmissionReceipt(placeholder, Hc.ExternalRuntimeOutcome.Faulted));
    }

    private Hc.ExternalOperationProviderPollResult Failure(Hc.ExternalOperationRequest request,
        Hc.ExternalOperationProviderPollStatus status, Hc.ExternalOperationStage stage,
        Hc.ExternalRuntimeOutcome outcome) => new(status, generations, stage switch
        {
            Hc.ExternalOperationStage.Submitted or Hc.ExternalOperationStage.Visible =>
                new Hc.ExternalOperationProgressReceipt(request, stage, outcome),
            Hc.ExternalOperationStage.DeviceComplete =>
                new Hc.ExternalOperationCompletionReceipt(request, outcome),
            Hc.ExternalOperationStage.Published =>
                new Hc.ExternalOperationPublicationReceipt(request, outcome),
            Hc.ExternalOperationStage.Released =>
                new Hc.ExternalOperationReleaseReceipt(request, outcome),
            _ => new Hc.ExternalOperationAdmissionReceipt(request, outcome)
        });

    private static Hc.ExternalOperationReceipt CreateReceipt(Hc.ExternalOperationRequest request,
        Hc.ExternalOperationStage stage) => stage switch
    {
        Hc.ExternalOperationStage.DeviceComplete => new Hc.ExternalOperationCompletionReceipt(request, Hc.ExternalRuntimeOutcome.Succeeded),
        Hc.ExternalOperationStage.Visible => new Hc.ExternalOperationProgressReceipt(request, stage, Hc.ExternalRuntimeOutcome.Succeeded),
        Hc.ExternalOperationStage.Published => new Hc.ExternalOperationPublicationReceipt(request, Hc.ExternalRuntimeOutcome.Succeeded),
        Hc.ExternalOperationStage.Released => new Hc.ExternalOperationReleaseReceipt(request, Hc.ExternalRuntimeOutcome.Closed),
        _ => new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Faulted)
    };

    private static Hc.ExternalOperationStage MapStage(ExternalOperationState state) => state switch
    {
        ExternalOperationState.Prepared => Hc.ExternalOperationStage.Prepared,
        ExternalOperationState.Admitted => Hc.ExternalOperationStage.Admitted,
        ExternalOperationState.Submitted => Hc.ExternalOperationStage.Submitted,
        ExternalOperationState.DeviceComplete => Hc.ExternalOperationStage.DeviceComplete,
        ExternalOperationState.Visible => Hc.ExternalOperationStage.Visible,
        ExternalOperationState.Published => Hc.ExternalOperationStage.Published,
        ExternalOperationState.Released => Hc.ExternalOperationStage.Released,
        _ => Hc.ExternalOperationStage.Failed
    };

    private static ExternalEffectPolicy? TryMapEffect(Hc.ExternalReplayEffectClass effect) => effect switch
    {
        Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish =>
            new(ExternalEffectClass.StagedReversibleUntilPublish, ExternalReplayProtection.None, false),
        Hc.ExternalReplayEffectClass.SnapshotOrIdempotenceRequired =>
            new(ExternalEffectClass.SnapshotOrIdempotenceRequired, ExternalReplayProtection.Idempotent, true),
        Hc.ExternalReplayEffectClass.IrreversibleBarrier =>
            new(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true),
        _ => null
    };
}


