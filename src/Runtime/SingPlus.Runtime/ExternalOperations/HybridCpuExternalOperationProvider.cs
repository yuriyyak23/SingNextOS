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
        public bool TransitionInFlight { get; set; }
        public bool GenerationInvalidated { get; set; }
        public bool AdmissionFailed { get; set; }
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
    private bool pendingGenerationDrift;
    private int providerEffectsInFlight;
    private readonly Dictionary<Hc.ExternalRequestCorrelation, Entry> entries = [];
    private readonly HashSet<Hc.ExternalRequestCorrelation> pendingAdmissions = [];

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
        Hc.ExternalGenerationSet generationSnapshot;
        lock (gate)
        {
            if (pendingGenerations is not null)
                return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
            if (semantic.ContractVersion != Hc.ExternalOperationContract.Version ||
                !Enum.IsDefined(semantic.EffectClass) ||
                !Enum.IsDefined(semantic.VisibilityRequirement) ||
                !Enum.IsDefined(semantic.CancellationMode) ||
                !Enum.IsDefined(semantic.ReplayEffectClass))
                return FaultWithoutAuthority(semantic);
            if (entries.ContainsKey(semantic.Correlation) ||
                pendingAdmissions.Contains(semantic.Correlation))
            {
                if (!entries.TryGetValue(semantic.Correlation, out var duplicate))
                    return FaultWithoutAuthority(semantic);
                return StaleFor(duplicate.Request);
            }

            pendingAdmissions.Add(semantic.Correlation);
            providerEffectsInFlight++;
            generationSnapshot = generations;
        }

        try
        {
            var publication = semantic.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? ExternalPublicationPolicy.DirectCoherent
                : ExternalPublicationPolicy.Staged;
            var visibility = semantic.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? SingPlus.Contracts.ExternalVisibilityRequirement.ConsumerDomain
                : SingPlus.Contracts.ExternalVisibilityRequirement.PublicationFence;
            var effect = TryMapEffect(semantic.ReplayEffectClass);
            if (effect is null) return FaultWithoutAuthority(semantic);
            var prepared = kernel.PrepareExternalOperationCore(principal, regionUses, visibility, publication, effect,
                out var originalPreparation);
            if (!prepared.IsSuccess)
                return originalPreparation is null ? FaultWithoutAuthority(semantic)
                    : CleanupFailedAdmission(semantic, generationSnapshot, originalPreparation.Operation);
            bool sourceCurrent;
            lock (gate) sourceCurrent = ProviderSourceMatches(generationSnapshot);
            if (!sourceCurrent)
                return CleanupFailedAdmission(semantic, generationSnapshot, prepared.Value!.Operation);
            var request = new Hc.ExternalOperationRequest(
                new(new(Guid.NewGuid()), new(prepared.Value!.Operation.Generation.Value)), scope,
                semantic.ContractVersion, generationSnapshot, semantic.Correlation, semantic.EffectClass,
                semantic.VisibilityRequirement, semantic.CancellationMode);
            var admitted = kernel.AdmitExternalOperation(principal, prepared.Value!.Operation, dependencies, service,
                semantic.CancellationMode == Hc.ExternalCancellationMode.Unsupported
                    ? ExternalCancellationSupport.BeforeSubmissionOnly
                    : ExternalCancellationSupport.ProviderCooperative);
            if (!admitted.IsSuccess)
                return CleanupFailedAdmission(semantic, generationSnapshot, prepared.Value.Operation);
            lock (gate)
            {
                var stale = !ProviderSourceMatches(generationSnapshot);
                entries.Add(semantic.Correlation, new Entry(request, prepared.Value.Operation, null)
                { AdmissionFailed = stale });
                if (stale) return StaleFor(request);
                return new(Hc.ExternalOperationProviderPollStatus.Receipt, generationSnapshot,
                    new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Succeeded));
            }
        }
        finally
        {
            List<Entry> invalidated;
            lock (gate)
            {
                pendingAdmissions.Remove(semantic.Correlation);
                invalidated = CompleteProviderEffectLocked();
            }
            ProjectInvalidatedEntries(invalidated);
        }
    }

    public Hc.ExternalOperationProviderPollResult Submit(Hc.ExternalOperationRequest request)
    {
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!)) return StaleFor(request);
            if (entry.AdmissionFailed)
                return Failure(request, Hc.ExternalOperationProviderPollStatus.Faulted,
                    Hc.ExternalOperationStage.Submitted, Hc.ExternalRuntimeOutcome.Faulted);
            if (!IsCurrent(entry) || entry.LastDelivered == Hc.ExternalOperationStage.Released)
                return StaleFor(request);
            if (!BeginTransitionLocked(entry))
                return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
        }
        try
        {
            var submitted = kernel.RecordExternalOperationSubmission(principal, entry.Operation, dependencies);
            if (!submitted.IsSuccess)
            {
                // A late publication refusal does not erase an owner-recorded submission.
                // Retain its exact binding only as a cleanup fact, never as admission.
                var original = kernel.ExternalOperations.Query(entry.Operation);
                if (original.IsSuccess && original.Value!.Binding is { } binding && binding.Operation == entry.Operation)
                {
                    lock (gate)
                    {
                        entry.Binding = binding;
                        if (entry.LastDelivered < Hc.ExternalOperationStage.Submitted)
                            entry.LastDelivered = Hc.ExternalOperationStage.Submitted;
                    }
                }
                return Receipt(new Hc.ExternalOperationProgressReceipt(request,
                    Hc.ExternalOperationStage.Submitted, Hc.ExternalRuntimeOutcome.Faulted));
            }
            lock (gate)
            {
                entry.Binding = submitted.Value!;
                entry.LastDelivered = Hc.ExternalOperationStage.Submitted;
                if (!IsCurrent(entry) || !ProviderSourceMatches(entry.Request.Generations))
                    return Failure(request, Hc.ExternalOperationProviderPollStatus.Stale,
                        Hc.ExternalOperationStage.Submitted, Hc.ExternalRuntimeOutcome.Stale);
                return Receipt(new Hc.ExternalOperationProgressReceipt(request,
                    Hc.ExternalOperationStage.Submitted, Hc.ExternalRuntimeOutcome.Succeeded));
            }
        }
        finally
        {
            FinishTransition(entry);
        }
    }

    internal Action? BeforePollPublicationQualificationHook { private get; set; }

    public Hc.ExternalOperationProviderPollResult Poll(Hc.ExternalOperationRequest request)
    {
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!) || !IsCurrent(entry))
                return StaleFor(request);
            if (entry.LastDelivered == Hc.ExternalOperationStage.Released) return StaleFor(request);
            if (!BeginTransitionLocked(entry))
                return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
        }
        try
        {
            var queried = kernel.QueryExternalOperation(principal, entry.Operation);
            if (!queried.IsSuccess) return StaleFor(request);
            BeforePollPublicationQualificationHook?.Invoke();
            lock (gate)
            {
                if (!TryExact(request, out var current) || !ReferenceEquals(current, entry) ||
                    !IsCurrent(entry) || !ProviderSourceMatches(request.Generations))
                    return StaleFor(request);
                // Pure existing owner query only. No callback runs under the provider gate.
                queried = kernel.QueryExternalOperation(principal, entry.Operation);
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
        finally
        {
            FinishTransition(entry);
        }
    }

    public Hc.ExternalOperationProviderPollResult Cancel(Hc.ExternalOperationRequest request)
    {
        _ = RequestCancellation(request);
        return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
    }

    public Hc.ExternalOperationCancellationReceipt RequestCancellation(Hc.ExternalOperationRequest request)
    {
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!) || !IsCurrent(entry))
                return new(request, Hc.ExternalOperationCancellationOutcome.Stale, generations);
            if (!BeginTransitionLocked(entry))
                return new(request, Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
        }
        try
        {
            var state = kernel.QueryExternalOperation(principal, entry.Operation);
            if (!state.IsSuccess)
                return new(request, Hc.ExternalOperationCancellationOutcome.Stale, generations);
            bool beforeSubmit = state.Value!.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted;
            var cancelled = kernel.CancelExternalOperation(principal, entry.Operation, providerCancellationSupported: true);
            if (!cancelled.IsSuccess)
                return new(request, Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
            if (beforeSubmit && cancelled.Value!.Disposition == ExternalOperationDisposition.Cancelled)
            {
                var released = kernel.ReleaseExternalOperation(principal, entry.Operation,
                    new(ProviderResourcesClosed: false, ProviderUnavailable: false));
                if (released.IsSuccess)
                {
                    lock (gate) entry.LastDelivered = Hc.ExternalOperationStage.Released;
                }
                return new(request, released.IsSuccess
                    ? Hc.ExternalOperationCancellationOutcome.ConfirmedBeforeSubmit
                    : Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
            }
            // The existing owner only records CancellationPending after submit. A local
            // request is not provider terminality or effect containment.
            return new(request, Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
        }
        finally
        {
            FinishTransition(entry);
        }
    }

    public KernelResult RecordDeviceCompletion(Hc.ExternalOperationRequest request,
        ExternalOperationCompletionDisposition disposition = ExternalOperationCompletionDisposition.Completed)
    {
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!) || entry.Binding is null)
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact submitted operation is required.");
            if (!IsCurrent(entry))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before completion.");
            if (!BeginTransitionLocked(entry))
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact operation transition is already in flight.");
        }
        try
        {
            var result = kernel.RecordExternalOperationCompletion(principal, new(entry.Binding.Value, disposition));
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
        finally
        {
            FinishTransition(entry);
        }
    }

    public KernelResult RecordVisibility(Hc.ExternalOperationRequest request, bool satisfied = true)
    {
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!) || entry.Binding is null)
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact submitted operation is required.");
            if (!IsCurrent(entry))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before visibility.");
            if (!BeginTransitionLocked(entry))
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact operation transition is already in flight.");
        }
        try
        {
            var requirement = entry.Request.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? SingPlus.Contracts.ExternalVisibilityRequirement.ConsumerDomain
                : SingPlus.Contracts.ExternalVisibilityRequirement.PublicationFence;
            var result = kernel.RecordExternalOperationVisibility(principal,
                new(entry.Binding.Value, requirement, satisfied));
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
        finally
        {
            FinishTransition(entry);
        }
    }

    public KernelResult Publish(Hc.ExternalOperationRequest request, Action publicationAction)
    {
        ArgumentNullException.ThrowIfNull(publicationAction);
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!))
                return KernelResult.Fail(KernelError.ExternalOperationNotFound, "Operation correlation was not admitted.");
            if (entry.AdmissionFailed)
                return KernelResult.Fail(KernelError.PlatformDenied, "Failed admission retains only cleanup access.");
            if (!IsCurrent(entry))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before publication.");
            if (!BeginTransitionLocked(entry))
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact operation transition is already in flight.");
        }
        try
        {
            var policy = entry.Request.VisibilityRequirement == Hc.ExternalVisibilityRequirement.Coherent
                ? ExternalPublicationPolicy.DirectCoherent
                : ExternalPublicationPolicy.Staged;
            KernelResult<ExternalOperationSnapshot> CommitProvider(Func<KernelResult<ExternalOperationSnapshot>> commit)
            {
                lock (gate)
                {
                    if (!TryExact(request, out var current) || !ReferenceEquals(current, entry) ||
                        !entry.TransitionInFlight || !IsCurrent(entry) || pendingGenerationDrift ||
                        pendingGenerations is { } pending && !pending.Equals(entry.Request.Generations))
                        return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration,
                            "Exact provider source changed before staged admission or publication.");
                    // Pure local process/owner admission or commit only; action and generation
                    // projection callbacks remain outside this provider gate.
                    return commit();
                }
            }
            var result = policy == ExternalPublicationPolicy.Staged
                ? kernel.PublishExternalOperation(principal, entry.Operation, dependencies,
                    new(policy), _ => publicationAction(), CommitProvider, sourceAdmission: CommitProvider)
                : kernel.PublishExternalOperation(principal, entry.Operation, dependencies,
                    new(policy), publicationAction);
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
        finally
        {
            FinishTransition(entry);
        }
    }

    public KernelResult Release(Hc.ExternalOperationRequest request, bool providerResourcesClosed,
        bool providerUnavailable = false, bool providerEffectContained = false)
    {
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!))
                return KernelResult.Fail(KernelError.ExternalOperationNotFound, "Operation correlation was not admitted.");
            if (!IsCurrent(entry))
                return KernelResult.Fail(KernelError.StaleGeneration, "External provider generations changed before release.");
            if (!BeginTransitionLocked(entry))
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact operation transition is already in flight.");
        }
        try
        {
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
            KernelResult<ExternalOperationSnapshot> CommitSource(Func<KernelResult<ExternalOperationSnapshot>> commit)
            {
                lock (gate)
                {
                    if (!TryExact(request, out var exact) || !ReferenceEquals(exact, entry) ||
                        !entry.TransitionInFlight || !IsCurrent(entry) || !ProviderSourceMatches(request.Generations))
                        return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration,
                            "Exact provider source changed before operation release commit.");
                    return commit();
                }
            }
            var result = kernel.ReleaseExternalOperationCore(principal, entry.Operation,
                new(providerResourcesClosed, providerUnavailable, providerEffectContained), CommitSource);
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        }
        finally
        {
            FinishTransition(entry);
        }
    }

    public void Reconfigure(Hc.ExternalGenerationSet currentGenerations)
    {
        ArgumentNullException.ThrowIfNull(currentGenerations);
        if (currentGenerations.ContractVersion != Hc.ExternalOperationContract.Version)
            throw new ArgumentException("Unsupported generation schema.", nameof(currentGenerations));
        List<Entry> invalidated;
        lock (gate)
        {
            if (providerEffectsInFlight == 0) invalidated = ApplyGenerationsLocked(currentGenerations);
            else
            {
                // Retain every observed drift while a provider effect is in flight.
                // The last snapshot alone would erase A -> B -> A reconfiguration.
                pendingGenerationDrift |= !generations.Equals(currentGenerations);
                pendingGenerations = currentGenerations;
                invalidated = [];
            }
        }
        ProjectInvalidatedEntries(invalidated);
    }

    /// <summary>
    /// Reconciles an operation invalidated by a provider generation change. The old
    /// request identifies the operation; it is never used as permission to submit,
    /// publish, or release. Resource closure remains an explicit provider assertion.
    /// </summary>
    internal Action? BeforeReconciliationReleaseQualificationHook { private get; set; }

    public KernelResult ReconcileReconfiguredOperation(Hc.ExternalOperationRequest request,
        Hc.ExternalGenerationSet observedCurrentGenerations, bool providerResourcesClosed)
    {
        ArgumentNullException.ThrowIfNull(observedCurrentGenerations);
        Entry entry;
        lock (gate)
        {
            if (!TryExact(request, out entry!))
                return KernelResult.Fail(KernelError.ExternalOperationNotFound,
                    "Operation correlation was not admitted.");
            if (!observedCurrentGenerations.Equals(generations) ||
                !entry.GenerationInvalidated || pendingGenerations is not null)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Exact current provider generation is required for reconfiguration reconciliation.");
            if (!BeginTransitionLocked(entry))
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "Exact operation transition is already in flight.");
        }
        try
        {
            var current = kernel.QueryExternalOperation(principal, entry.Operation);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value!.State is not (ExternalOperationState.Submitted or
                ExternalOperationState.DeviceComplete or ExternalOperationState.Visible))
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "Reconfiguration reconciliation applies after submit and before publication.");
            if (current.Value.Disposition != ExternalOperationDisposition.ProviderLost)
            {
                var lost = kernel.RecordExternalOperationProviderLoss(principal, entry.Operation);
                if (!lost.IsSuccess) return KernelResult.Fail(lost.Error, lost.Message!);
            }
            if (!providerResourcesClosed) return KernelResult.Ok();
            BeforeReconciliationReleaseQualificationHook?.Invoke();
            KernelResult<ExternalOperationSnapshot> CommitSource(Func<KernelResult<ExternalOperationSnapshot>> commit)
            {
                lock (gate)
                {
                    if (!TryExact(request, out var exact) || !ReferenceEquals(exact, entry) ||
                        !entry.TransitionInFlight || !entry.GenerationInvalidated ||
                        !ProviderSourceMatches(observedCurrentGenerations))
                        return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration,
                            "Reconciliation source changed before original owner release.");
                    return commit();
                }
            }
            var released = kernel.ReleaseExternalOperationCore(principal, entry.Operation,
                new(ProviderResourcesClosed: true, ProviderUnavailable: true), CommitSource);
            return released.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(released.Error, released.Message!);
        }
        finally
        {
            FinishTransition(entry);
        }
    }

    private bool TryExact(Hc.ExternalOperationRequest request, out Entry entry) =>
        entries.TryGetValue(request.Correlation, out entry!) && entry.Request == request;

    private bool IsCurrent(Entry entry) =>
        !entry.GenerationInvalidated && entry.Request.Generations.Equals(generations);

    // Called under the existing provider gate. A deferred A -> B -> A is still drift.
    private bool ProviderSourceMatches(Hc.ExternalGenerationSet expected) =>
        generations.Equals(expected) && !pendingGenerationDrift &&
        (pendingGenerations is null || pendingGenerations.Equals(expected));

    private List<Entry> ApplyGenerationsLocked(Hc.ExternalGenerationSet next,
        bool forceInvalidate = false)
    {
        List<Entry> invalidated = [];
        if (forceInvalidate || !generations.Equals(next))
        {
            foreach (var entry in entries.Values)
            {
                if (entry.GenerationInvalidated ||
                    !forceInvalidate && entry.Request.Generations.Equals(next)) continue;
                entry.GenerationInvalidated = true;
                if (BeginTransitionLocked(entry)) invalidated.Add(entry);
            }
        }
        generations = next;
        return invalidated;
    }

    private bool BeginTransitionLocked(Entry entry)
    {
        if (entry.TransitionInFlight || pendingGenerations is not null) return false;
        entry.TransitionInFlight = true;
        providerEffectsInFlight++;
        return true;
    }

    private List<Entry> EndTransitionLocked(Entry entry)
    {
        entry.TransitionInFlight = false;
        return CompleteProviderEffectLocked();
    }

    private List<Entry> CompleteProviderEffectLocked()
    {
        providerEffectsInFlight--;
        if (providerEffectsInFlight == 0 && pendingGenerations is { } pending)
        {
            var drifted = pendingGenerationDrift;
            pendingGenerations = null;
            pendingGenerationDrift = false;
            return ApplyGenerationsLocked(pending, drifted);
        }
        return [];
    }

    private void FinishTransition(Entry entry)
    {
        List<Entry> invalidated;
        lock (gate) invalidated = EndTransitionLocked(entry);
        ProjectInvalidatedEntries(invalidated);
    }

    private void ProjectInvalidatedEntries(IEnumerable<Entry> initial)
    {
        var queue = new Queue<Entry>(initial);
        while (queue.TryDequeue(out var entry))
        {
            try
            {
                var current = kernel.QueryExternalOperation(principal, entry.Operation);
                if (!current.IsSuccess) continue;
                if (current.Value!.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted)
                {
                    var cancelled = kernel.CancelExternalOperation(principal, entry.Operation, false);
                    if (cancelled.IsSuccess)
                        _ = kernel.ReleaseExternalOperation(principal, entry.Operation, new(false, false));
                }
                else if (current.Value.State is (ExternalOperationState.Submitted or
                    ExternalOperationState.DeviceComplete or ExternalOperationState.Visible) &&
                    current.Value.Disposition != ExternalOperationDisposition.ProviderLost)
                {
                    _ = kernel.RecordExternalOperationProviderLoss(principal, entry.Operation);
                }
            }
            finally
            {
                List<Entry> next;
                lock (gate) next = EndTransitionLocked(entry);
                foreach (var affected in next) queue.Enqueue(affected);
            }
        }
    }

    private Hc.ExternalOperationProviderPollResult Receipt(Hc.ExternalOperationReceipt receipt) =>
        new(Hc.ExternalOperationProviderPollStatus.Receipt, generations, receipt);

    private Hc.ExternalOperationProviderPollResult StaleFor(Hc.ExternalOperationRequest request) =>
        new(Hc.ExternalOperationProviderPollStatus.Stale, generations,
            new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Stale));

    private Hc.ExternalOperationProviderPollResult CleanupFailedAdmission(
        Hc.ExternalOperationSemanticRequest semantic, Hc.ExternalGenerationSet generationSnapshot,
        ExternalOperationHandle operation)
    {
        _ = kernel.CancelExternalOperation(principal, operation, false);
        _ = kernel.ReleaseExternalOperation(principal, operation, new(false, false));
        var original = kernel.ExternalOperations.Query(operation);
        if (!original.IsSuccess || original.Value!.State == ExternalOperationState.Released)
            return FaultWithoutAuthority(semantic);
        var request = new Hc.ExternalOperationRequest(
            new(new(Guid.NewGuid()), new(operation.Generation.Value)), scope,
            semantic.ContractVersion, generationSnapshot, semantic.Correlation, semantic.EffectClass,
            semantic.VisibilityRequirement, semantic.CancellationMode);
        // The exact owner fact survives refusal; this entry grants cleanup access only.
        lock (gate)
            entries.Add(semantic.Correlation, new Entry(request, operation, original.Value.Binding)
            { AdmissionFailed = true });
        return new(Hc.ExternalOperationProviderPollStatus.Faulted, generationSnapshot,
            new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Faulted));
    }

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


