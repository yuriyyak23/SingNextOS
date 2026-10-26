using System.Collections.ObjectModel;
using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class ExternalOperationAuthority
{
    private sealed class Record
    {
        public required OperationPreparation Preparation { get; init; }
        public required ExternalOperationState State { get; set; }
        public required ExternalOperationDisposition Disposition { get; set; }
        public OperationAdmissionSnapshot? Admission { get; set; }
        public OperationBinding? Binding { get; set; }
        public ExternalEffectBoundaryState EffectBoundary { get; set; }
        public ExternalOperationResourceBinding? ResourceBinding { get; set; }
        public ExternalPublicationDecisionV1? PublicationDecision { get; set; }
        public bool PublicationInFlight { get; set; }
        public bool PublicationEffectAmbiguous { get; set; }
        public bool RegionInvalidationUnproven { get; set; }
        public bool SoftwareNoEffect { get; init; }
        public PlatformSoftwareSecureAdmissionRequest? SoftwareRequest { get; init; }
        public SoftwareSecureInitializedLoadIntent? InitializedLoad { get; init; }
        public SoftwareSecureInitializedLoadObservation? InitializedLoadClosure { get; set; }
        public bool SoftwareClosureProven { get; set; }
        public List<ExternalOperationTransition> Transitions { get; } = [];
        public ulong NextTransitionSequence { get; set; } = 1;
    }

    private readonly object _gate = new();
    private readonly RegionAuthority _regions;
    private readonly Dictionary<ExternalOperationId, Record> _operations = [];
    private readonly Dictionary<ExternalOperationId, Record> _ownedOperations = [];
    private ulong _nextOperationId = 1;
    private ulong _nextBindingId = 1;

    internal ExternalOperationAuthority(RegionAuthority regions) => _regions = regions;

    public KernelResult<OperationPreparation> Prepare(
        RegionOwner principal,
        IReadOnlyList<OperationRegionUseRequest> regionUses,
        ExternalVisibilityRequirement visibilityRequirement,
        ExternalPublicationPolicy publicationPolicy,
        ExternalEffectPolicy? effectPolicy = null) =>
        PrepareCore(principal, regionUses, visibilityRequirement, publicationPolicy, effectPolicy, null);

    // Register-local software requests have no Region effects. Generic callers still require Regions.
    internal KernelResult<OperationPreparation> PrepareSoftwareNoEffect(RegionOwner principal, PlatformSoftwareSecureAdmissionRequest request) =>
        PrepareCore(principal, [], ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged, null, request);

    private KernelResult<OperationPreparation> PrepareCore(RegionOwner principal,
        IReadOnlyList<OperationRegionUseRequest> regionUses, ExternalVisibilityRequirement visibilityRequirement,
        ExternalPublicationPolicy publicationPolicy, ExternalEffectPolicy? effectPolicy, PlatformSoftwareSecureAdmissionRequest? softwareRequest,
        SoftwareSecureInitializedLoadIntent? initializedLoad = null)
    {
        ArgumentNullException.ThrowIfNull(regionUses);
        ReadOnlyCollection<OperationRegionUseRequest> requests = Array.AsReadOnly(regionUses.ToArray());
        lock (_gate)
        {
            if (principal.DomainId.Value == 0 || principal.ProcessGeneration == 0)
                return KernelResult<OperationPreparation>.Fail(KernelError.WrongRegionOwner, "External operation principal is invalid.");
            if (requests.Count == 0 && softwareRequest is null)
                return KernelResult<OperationPreparation>.Fail(KernelError.InvalidRegionState, "External operations require at least one region-use request.");
            if (!Enum.IsDefined(visibilityRequirement) || !Enum.IsDefined(publicationPolicy))
                return KernelResult<OperationPreparation>.Fail(KernelError.InvalidTransition, "External operation visibility or publication policy is invalid.");
            ExternalEffectPolicy selectedEffect = effectPolicy ?? new ExternalEffectPolicy(
                ExternalEffectClass.StagedReversibleUntilPublish,
                ExternalReplayProtection.None,
                false);
            KernelResult effectValidation = ValidateEffectPolicy(publicationPolicy, selectedEffect);
            if (!effectValidation.IsSuccess)
                return KernelResult<OperationPreparation>.Fail(effectValidation.Error, effectValidation.Message!);
            if (_nextOperationId == 0)
                return KernelResult<OperationPreparation>.Fail(KernelError.CapacityExhausted, "External operation identity space is exhausted.");

            ExternalOperationHandle handle = new ExternalOperationHandle(new ExternalOperationId(_nextOperationId++), new OperationGeneration(1));
            OperationPreparation preparation = new OperationPreparation(handle, principal, requests, visibilityRequirement, publicationPolicy, selectedEffect);
            Record record = new Record
            {
                Preparation = preparation,
                SoftwareNoEffect = softwareRequest is { ExecuteRegisterLocal: false },
                SoftwareRequest = softwareRequest,
                InitializedLoad = initializedLoad,
                State = ExternalOperationState.Prepared,
                Disposition = ExternalOperationDisposition.Active
            };
            AddTransition(record, ExternalOperationState.Prepared, "Prepared");
            _ownedOperations.EnsureCapacity(_ownedOperations.Count + 1);
            _operations.EnsureCapacity(_operations.Count + 1);
            _ownedOperations.Add(handle.OperationId, record);
            _operations.Add(handle.OperationId, record);
            return KernelResult<OperationPreparation>.Ok(preparation);
        }
    }

    internal static KernelResult ValidateCancellationSupport(ExternalCancellationSupport support) =>
        Enum.IsDefined(support) ? KernelResult.Ok() :
        KernelResult.Fail(KernelError.InvalidTransition, "External operation cancellation support is unknown.");

    internal KernelResult ConfirmSoftwareNoEffectClosure(ExternalOperationHandle operation,
        PlatformSoftwareSecureAdmissionObservation observation)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.SoftwareRequest is null || !observation.ResourcesClosed ||
                !ReferenceEquals(resolved.Value.SoftwareRequest, observation.Request))
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Exact original software closure proof required.");
            resolved.Value.SoftwareClosureProven = true;
            return KernelResult.Ok();
        }
    }

    public KernelResult<OperationAdmissionSnapshot> Admit(
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity = default,
        ExternalCancellationSupport cancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly,
        CancellationScopeHandle? cancellationScope = null) =>
        AdmitCore(operation, dependencies, serviceIdentity, cancellationSupport, cancellationScope, allowExactPlatformMappings: false);

    internal KernelResult<ExternalOperationSnapshot> PublishSoftwareRegisterLocal(ExternalOperationHandle operation,
        PlatformSoftwareSecureAdmissionObservation proof, OperationDependencySnapshot dependencies,
        Action<ExternalPublicationDecisionV1> action,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>> sourceCommit,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>> sourceAdmission)
    {
        lock (_gate)
        {
            var current = Resolve(operation);
            if (!current.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(current.Error, current.Message!);
            if (current.Value!.SoftwareRequest is not { ExecuteRegisterLocal: true } original ||
                !ReferenceEquals(original, proof.Request) || !current.Value.SoftwareClosureProven ||
                proof.RegisterLocalValue is not { } value || proof.RegisterLocalCommit == 0 || proof.MachineAttempt == 0 ||
                value != unchecked(original.Left + original.Right))
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformDenied, "Exact retired arithmetic proof required.");
        }
        return Publish(operation, dependencies, new(ExternalPublicationPolicy.Staged), action,
            sourceCommit, sourceAdmission, softwarePublication: true);
    }

    internal KernelResult<OperationAdmissionSnapshot> AdmitForExactPlatformMappings(
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity = default,
        ExternalCancellationSupport cancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly,
        CancellationScopeHandle? cancellationScope = null) =>
        AdmitCore(operation, dependencies, serviceIdentity, cancellationSupport, cancellationScope, allowExactPlatformMappings: true);

    private KernelResult<OperationAdmissionSnapshot> AdmitCore(
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity,
        ExternalCancellationSupport cancellationSupport,
        CancellationScopeHandle? cancellationScope,
        bool allowExactPlatformMappings)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<OperationAdmissionSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.State != ExternalOperationState.Prepared || record.Disposition != ExternalOperationDisposition.Active)
                return InvalidTransition<OperationAdmissionSnapshot>(record, "Only an active Prepared operation can be admitted.");
            KernelResult cancellationValidation = ValidateCancellationSupport(cancellationSupport);
            if (!cancellationValidation.IsSuccess)
                return KernelResult<OperationAdmissionSnapshot>.Fail(cancellationValidation.Error, cancellationValidation.Message!);

            List<RegionUseDescriptor> acquired = new List<RegionUseDescriptor>();
            foreach (OperationRegionUseRequest request in record.Preparation.RegionUses)
            {
                KernelResult<RegionUseDescriptor> use = allowExactPlatformMappings
                    ? _regions.AcquireUseForExactPlatformMapping(request.Region, record.Preparation.Principal,
                        request.Mode, request.Range, operation)
                    : _regions.AcquireUseForExternalOperation(request.Region,
                        record.Preparation.Principal, request.Mode, request.Range, operation);
                if (!use.IsSuccess)
                {
                    KernelResult cleanup = _regions.ReleaseUsesAtomically(
                        acquired.Select(static prior => prior.Handle).ToArray(),
                        record.Preparation.Principal, operation);
                    if (!cleanup.IsSuccess)
                    {
                        // A failed local rollback still owns every acquired use. Keep
                        // those pins attached to this operation for explicit inspection
                        // and release; no provider submission was authorized.
                        record.Admission = new OperationAdmissionSnapshot(operation,
                            record.Preparation.Principal, Array.AsReadOnly(acquired.ToArray()),
                            dependencies, serviceIdentity, cancellationSupport,
                            record.Preparation.EffectPolicy.EffectClass,
                            record.Preparation.PublicationPolicy, cancellationScope);
                        record.Disposition = ExternalOperationDisposition.Cancelled;
                        AddTransition(record, record.State, "CancelledBeforeSubmit");
                        return KernelResult<OperationAdmissionSnapshot>.Fail(cleanup.Error,
                            $"Admission failed and acquired Region uses remain pinned to this operation: {cleanup.Message}");
                    }
                    return KernelResult<OperationAdmissionSnapshot>.Fail(use.Error, use.Message!);
                }
                acquired.Add(use.Value!);
            }

            OperationAdmissionSnapshot admission = new OperationAdmissionSnapshot(
                operation,
                record.Preparation.Principal,
                Array.AsReadOnly(acquired.ToArray()),
                dependencies,
                serviceIdentity,
                cancellationSupport,
                record.Preparation.EffectPolicy.EffectClass,
                record.Preparation.PublicationPolicy,
                cancellationScope);
            record.Admission = admission;
            Move(record, ExternalOperationState.Admitted, "Admitted");
            return KernelResult<OperationAdmissionSnapshot>.Ok(admission);
        }
    }

    public KernelResult<OperationBinding> RecordSubmission(
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<OperationBinding>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.State != ExternalOperationState.Admitted || record.Disposition != ExternalOperationDisposition.Active || record.Admission is null)
                return InvalidTransition<OperationBinding>(record, "Submission requires an active Admitted operation.");
            if (record.Admission.Dependencies != currentDependencies)
                return KernelResult<OperationBinding>.Fail(KernelError.StaleGeneration, "Operation dependency generation changed before provider submission.");
            KernelResult uses = ValidateUses(record);
            if (!uses.IsSuccess) return KernelResult<OperationBinding>.Fail(uses.Error, uses.Message!);
            if (_nextBindingId == 0)
                return KernelResult<OperationBinding>.Fail(KernelError.CapacityExhausted, "External operation binding identity space is exhausted.");

            OperationBinding binding = new OperationBinding(operation, new OperationBindingId(_nextBindingId++), 1);
            record.Binding = binding;
            record.EffectBoundary = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
                ? ExternalEffectBoundaryState.StagedPending
                : record.Preparation.EffectPolicy.EffectClass == ExternalEffectClass.IrreversibleBarrier
                    ? ExternalEffectBoundaryState.Irreversible
                    : ExternalEffectBoundaryState.ExternallyVisible;
            Move(record, ExternalOperationState.Submitted, "Submitted");
            return KernelResult<OperationBinding>.Ok(binding);
        }
    }

    public KernelResult<ExternalOperationSnapshot> RecordCompletion(OperationCompletion completion)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(completion.Binding.Operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.State != ExternalOperationState.Submitted || record.Binding != completion.Binding)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Completion does not match the exact active Submitted binding.");
            if (!Enum.IsDefined(completion.Disposition))
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, "Provider completion disposition is invalid.");
            if (HasProviderLoss(record))
                return InvalidTransition<ExternalOperationSnapshot>(record,
                    "A terminal response cannot erase the recorded provider-loss consequence.");

            record.Disposition = completion.Disposition switch
            {
                ExternalOperationCompletionDisposition.Completed => ExternalOperationDisposition.Completed,
                ExternalOperationCompletionDisposition.Cancelled => ExternalOperationDisposition.Cancelled,
                _ => ExternalOperationDisposition.Faulted
            };
            Move(record, ExternalOperationState.DeviceComplete, completion.Disposition == ExternalOperationCompletionDisposition.Completed ? "DeviceComplete" : $"DeviceComplete:{completion.Disposition}");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> RecordVisibility(OperationVisibilityEvidence evidence)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(evidence.Binding.Operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.State != ExternalOperationState.DeviceComplete ||
                record.Disposition != ExternalOperationDisposition.Completed ||
                record.Binding != evidence.Binding ||
                evidence.Requirement != record.Preparation.VisibilityRequirement)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Visibility evidence does not match the exact completed operation and requirement.");
            if (!evidence.Satisfied)
            {
                record.Disposition = ExternalOperationDisposition.Faulted;
                AddTransition(record, record.State, "VisibilityFailed");
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, "Required external-operation visibility was not satisfied; publication remains forbidden.");
            }

            Move(record, ExternalOperationState.Visible, "Visible");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> Publish(
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        PublicationPlan plan,
        Action publicationAction) =>
        Publish(operation, currentDependencies, plan, _ => publicationAction());

    public KernelResult<ExternalOperationSnapshot> Publish(
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        PublicationPlan plan,
        Action<ExternalPublicationDecisionV1> publicationAction)
        => Publish(operation, currentDependencies, plan, publicationAction, null);

    internal KernelResult<ExternalOperationSnapshot> Publish(
        ExternalOperationHandle operation, OperationDependencySnapshot currentDependencies, PublicationPlan plan,
        Action<ExternalPublicationDecisionV1> publicationAction,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>>? sourceCommit,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>>? sourceAdmission = null,
        bool softwarePublication = false)
    {
        ArgumentNullException.ThrowIfNull(publicationAction);
        if (sourceAdmission is not null && plan.Policy == ExternalPublicationPolicy.DirectCoherent)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformUnsupported,
                "The source admission hook requires a staged action contour.");
        ExternalPublicationDecisionV1 decision = default;
        KernelResult<ExternalOperationSnapshot>? ownerAdmission = null;
        Record? admittedOwner = null;
        ExternalOperationSnapshot? directPublication = null;
        bool admissionScopeOpen = true;
        int admissionThread = Environment.CurrentManagedThreadId;
        KernelResult<ExternalOperationSnapshot> AdmitPublication()
        {
          lock (_gate)
          {
            if (!admissionScopeOpen || Environment.CurrentManagedThreadId != admissionThread || ownerAdmission is not null)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition,
                    "The exact publication admission scope is unavailable.");
                KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
                Record record = resolved.Value!;
            if (record.State != ExternalOperationState.Visible || record.Disposition != ExternalOperationDisposition.Completed || record.Admission is null)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Publication requires a completed and Visible operation.");
            if ((record.SoftwareRequest is not null || record.InitializedLoad is not null) && (!softwarePublication || record.SoftwareNoEffect))
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformUnsupported, "No-effect software admission cannot publish.");
            if (plan.Policy != record.Preparation.PublicationPolicy)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition, "Publication plan does not match the admitted policy.");
            if (record.PublicationInFlight)
                return InvalidTransition<ExternalOperationSnapshot>(record, "An exact publication decision is already in flight.");
            if (record.Admission.Dependencies != currentDependencies)
            {
                FailPublicationRevalidation(record);
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration, "Operation dependency generation changed before publication.");
            }
                KernelResult uses = ValidateUses(record);
            if (!uses.IsSuccess)
            {
                FailPublicationRevalidation(record);
                return KernelResult<ExternalOperationSnapshot>.Fail(uses.Error, uses.Message!);
            }

            // Direct/coherent effects are already externally observable at their effect boundary.
            // Published is bookkeeping for that contour; no fictitious withheld-action gate exists.
            if (record.Preparation.PublicationPolicy == ExternalPublicationPolicy.DirectCoherent)
            {
                if (sourceCommit is not null)
                    return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformUnsupported,
                        "The source publication commit hook requires a staged action contour.");
                record.Disposition = ExternalOperationDisposition.Published;
                Move(record, ExternalOperationState.Published, "DirectPublicationObserved");
                directPublication = Snapshot(record);
                ownerAdmission = KernelResult<ExternalOperationSnapshot>.Ok(directPublication);
                return ownerAdmission.Value;
            }

            if (record.Binding is not { } binding || record.NextTransitionSequence == 0)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.CapacityExhausted,
                    "Publication decision generation is unavailable.");
            decision = new(ExternalPublicationDecisionV1.CurrentVersion, binding,
                currentDependencies, plan.Policy, record.NextTransitionSequence);
            record.PublicationDecision = decision;
            record.PublicationInFlight = true;
            admittedOwner = record;
            ownerAdmission = KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
            return ownerAdmission.Value;
          }
        }
        KernelResult<ExternalOperationSnapshot> admission;
        try { admission = sourceAdmission is null ? AdmitPublication() : sourceAdmission(AdmitPublication); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            admission = KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformDenied,
                $"Publication source admission failed before action: {exception.Message}");
        }
        lock (_gate)
        {
            admissionScopeOpen = false;
            if (directPublication is not null) return KernelResult<ExternalOperationSnapshot>.Ok(directPublication);
            if (!admission.IsSuccess || ownerAdmission is null)
            {
                // No publication action has run. Clear only this exact decision's
                // interlock; source replies cannot synthesize owner admission.
                if (admittedOwner is { } record &&
                    record.PublicationInFlight && record.PublicationDecision == decision)
                {
                    record.PublicationInFlight = false;
                    record.PublicationDecision = null;
                }
                return admission.IsSuccess
                    ? KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition,
                        "Publication source reply has no owner admission fact.") : admission;
            }
            // A source callback can change routing after owner admission. Refuse
            // before action and balance only the captured original decision.
            KernelResult<Record> current = Resolve(operation);
            bool ready = current.IsSuccess && ReferenceEquals(current.Value, admittedOwner) &&
                admittedOwner!.PublicationInFlight && admittedOwner.PublicationDecision == decision &&
                admittedOwner.State == ExternalOperationState.Visible &&
                admittedOwner.Disposition == ExternalOperationDisposition.Completed;
            KernelResult uses = ready ? ValidateUses(admittedOwner!) :
                KernelResult.Fail(KernelError.StaleGeneration, "Original publication owner changed before action.");
            if (!uses.IsSuccess)
            {
                if (admittedOwner is { } original && original.PublicationInFlight && original.PublicationDecision == decision)
                {
                    original.PublicationInFlight = false;
                    original.PublicationDecision = null;
                }
                return KernelResult<ExternalOperationSnapshot>.Fail(uses.Error, uses.Message!);
            }
        }

        try
        {
            publicationAction(decision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_gate)
            {
                // Possible effect belongs to the captured owner even if its
                // discovery route disappeared during the action.
                Record record = admittedOwner!;
                if (!record.PublicationInFlight || record.PublicationDecision != decision)
                    return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration,
                        "Publication decision changed while provider action was in flight.");
                record.PublicationInFlight = false;
                record.PublicationEffectAmbiguous = true;
                record.Disposition = ExternalOperationDisposition.Faulted;
                record.EffectBoundary = ExternalEffectBoundaryState.PossiblyExternallyVisible;
                AddTransition(record, record.State, "PublicationEffectAmbiguous");
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                    $"Publication action may have taken effect before failure: {exception.Message}");
            }
        }

        KernelResult<ExternalOperationSnapshot>? ownerPublication = null;
        bool sourceScopeOpen = true;
        int sourceThread = Environment.CurrentManagedThreadId;
        KernelResult<ExternalOperationSnapshot> CommitPublication()
        {
          lock (_gate)
          {
            if (!sourceScopeOpen || Environment.CurrentManagedThreadId != sourceThread)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition,
                    "The exact publication source commit scope has ended.");
                KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
                Record record = resolved.Value!;
            if (record.PublicationInFlight && record.PublicationDecision == decision && HasProviderLoss(record))
            {
                // Loss may arrive while the action is outside the owner lock. Its
                // possible effect cannot be forgotten or committed as Published.
                record.PublicationInFlight = false;
                record.PublicationEffectAmbiguous = true;
                record.Disposition = ExternalOperationDisposition.Faulted;
                record.EffectBoundary = ExternalEffectBoundaryState.PossiblyExternallyVisible;
                AddTransition(record, record.State, "PublicationEffectAmbiguous");
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                    "Provider loss crossed the publication action; exact effect closure remains required.");
            }
            if (!record.PublicationInFlight || record.PublicationDecision != decision ||
                record.State != ExternalOperationState.Visible || record.Disposition != ExternalOperationDisposition.Completed)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration,
                    "Publication state changed while the exact provider action was in flight.");
                KernelResult liveUses = ValidateUses(record);
            if (!liveUses.IsSuccess)
            {
                // The callback may already have published externally. A concurrent
                // Region damage or revoke cannot turn that effect into success.
                record.PublicationInFlight = false;
                record.PublicationEffectAmbiguous = true;
                record.Disposition = ExternalOperationDisposition.Faulted;
                record.EffectBoundary = ExternalEffectBoundaryState.PossiblyExternallyVisible;
                AddTransition(record, record.State, "PublicationEffectAmbiguous");
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                    $"Region use changed during publication; exact effect closure is required: {liveUses.Message}");
            }
            record.PublicationInFlight = false;
            record.Disposition = ExternalOperationDisposition.Published;
            if (record.EffectBoundary == ExternalEffectBoundaryState.StagedPending)
                record.EffectBoundary = ExternalEffectBoundaryState.ExternallyVisible;
            Move(record, ExternalOperationState.Published, "Published");
            ownerPublication = KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
            return ownerPublication.Value;
          }
        }
        KernelResult<ExternalOperationSnapshot> committed;
        try { committed = sourceCommit is null ? CommitPublication() : sourceCommit(CommitPublication); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            committed = KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                $"Publication source commit threw after possible effect: {exception.Message}");
        }
        lock (_gate)
        {
            sourceScopeOpen = false;
            // Only this owner's linearized transition is a publication fact. A hook
            // reply cannot manufacture success or undo an already committed fact.
            if (ownerPublication is { } published) return published;
            if (admittedOwner is { } record && record.PublicationInFlight && record.PublicationDecision == decision)
            {
                record.PublicationInFlight = false;
                record.PublicationEffectAmbiguous = true;
                record.Disposition = ExternalOperationDisposition.Faulted;
                record.EffectBoundary = ExternalEffectBoundaryState.PossiblyExternallyVisible;
                AddTransition(record, record.State, "PublicationEffectAmbiguous");
            }
        }
        return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
            $"Publication source refused after possible effect; exact closure remains required: {committed.Message}");
    }

    public KernelResult<ExternalOperationSnapshot> Cancel(
        ExternalOperationHandle operation,
        bool providerCancellationSupported)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.PublicationEffectAmbiguous)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                    "Ambiguous publication requires exact effect closure before cancellation.");
            if (record.PublicationInFlight)
                return InvalidTransition<ExternalOperationSnapshot>(record,
                    "Cancellation cannot cross an in-flight publication decision.");
            if (record.State == ExternalOperationState.Released)
                return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
            if (record.State == ExternalOperationState.Published)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Published results cannot be cancelled or unpublished.");
            if (record.Disposition is ExternalOperationDisposition.Cancelled or ExternalOperationDisposition.Discarded or
                ExternalOperationDisposition.CancellationPending or ExternalOperationDisposition.ProviderLost or
                ExternalOperationDisposition.Faulted)
                return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));

            switch (record.State)
            {
                case ExternalOperationState.Prepared:
                case ExternalOperationState.Admitted:
                    record.Disposition = ExternalOperationDisposition.Cancelled;
                    AddTransition(record, record.State, "CancelledBeforeSubmit");
                    break;
                case ExternalOperationState.Submitted:
                    record.Disposition = ExternalOperationDisposition.CancellationPending;
                    bool cooperative = record.Admission?.CancellationSupport == ExternalCancellationSupport.ProviderCooperative;
                    AddTransition(record, record.State, cooperative ? "ProviderCancellationRequested" : "DrainRequired");
                    break;
                case ExternalOperationState.DeviceComplete:
                case ExternalOperationState.Visible:
                    record.Disposition = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
                        ? ExternalOperationDisposition.Discarded
                        : ExternalOperationDisposition.Faulted;
                    AddTransition(record, record.State, record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged ? "StagedResultDiscarded" : "DirectWriteCannotBeUndone");
                    break;
                default:
                    return InvalidTransition<ExternalOperationSnapshot>(record, "Operation cannot be cancelled from its current state.");
            }

            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> RecordProviderLoss(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.State is not (ExternalOperationState.Submitted or ExternalOperationState.DeviceComplete or ExternalOperationState.Visible))
                return InvalidTransition<ExternalOperationSnapshot>(record, "Provider loss is relevant only after submission and before publication.");
            if (HasProviderLoss(record))
                return record.RegionInvalidationUnproven
                    ? KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                        "Provider loss remains recorded with unproven Region use invalidation.")
                    : KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));

            if (record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged)
            {
                record.Disposition = ExternalOperationDisposition.ProviderLost;
                KernelResult? invalidationFailure = null;
                foreach (RegionUseDescriptor use in record.Admission?.RegionUses ?? [])
                {
                    KernelResult invalidation = _regions.InvalidateUseAfterProviderLoss(use.Handle,
                        record.Preparation.Principal, operation);
                    if (!invalidation.IsSuccess)
                        invalidationFailure ??= invalidation;
                }
                AddTransition(record, record.State, "ProviderLost");
                if (invalidationFailure is { } failure)
                {
                    record.RegionInvalidationUnproven = true;
                    return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                        $"Provider loss is recorded, but exact Region use invalidation failed: {failure.Message}");
                }
            }
            else
            {
                record.Disposition = ExternalOperationDisposition.Faulted;
                AddTransition(record, record.State, "ProviderLost");
            }
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    // Only the existing owner history, under _gate, decides whether loss was committed.
    private static bool HasProviderLoss(Record record) =>
        record.Transitions.Any(transition => transition.Event == "ProviderLost");

    public KernelResult<ExternalOperationSnapshot> Release(
        ExternalOperationHandle operation,
        ReleasePlan plan)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.PublicationEffectAmbiguous)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                    "Ambiguous publication requires exact effect closure before release.");
            if (record.PublicationInFlight)
                return InvalidTransition<ExternalOperationSnapshot>(record,
                    "Release cannot cross an in-flight publication decision.");
            if (record.State == ExternalOperationState.Released)
                return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
            if (!CanRelease(record, plan))
                return InvalidTransition<ExternalOperationSnapshot>(record, "Operation cannot release local authority until cancellation/publication and provider closure or loss are explicit.");

            KernelResult releasedUses = _regions.ReleaseUsesAtomically(
                (record.Admission?.RegionUses ?? []).Select(static use => use.Handle).ToArray(),
                record.Preparation.Principal, operation);
            if (!releasedUses.IsSuccess)
                return KernelResult<ExternalOperationSnapshot>.Fail(releasedUses.Error, releasedUses.Message!);

            Move(record, ExternalOperationState.Released, "Released");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult ValidateFabricDrainPreflight(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            return resolved.Value!.PublicationInFlight
                ? KernelResult.Fail(KernelError.PlatformBindingDraining, "Original external operation publication is in flight.")
                : KernelResult.Ok();
        }
    }

    public KernelResult<ExternalOperationSnapshot> Query(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            return resolved.IsSuccess
                ? KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(resolved.Value!))
                : KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult<ExternalOperationSnapshot> ReconcileFailedPublication(
        ExternalOperationHandle operation, ExternalPublicationDecisionV1 decision,
        Func<KernelResult> confirmNoPublication)
    {
        ArgumentNullException.ThrowIfNull(confirmNoPublication);
        ulong transitionSequence;
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (!record.PublicationEffectAmbiguous || record.PublicationInFlight ||
                record.PublicationDecision != decision || record.State != ExternalOperationState.Visible)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Exact ambiguous publication decision is required for reconciliation.");
            transitionSequence = record.NextTransitionSequence;
        }
        KernelResult closure;
        try { closure = confirmNoPublication(); }
        catch (Exception exception)
        { return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformDenied, exception.Message); }
        if (!closure.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(closure.Error, closure.Message!);
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (!record.PublicationEffectAmbiguous || record.PublicationInFlight ||
                record.PublicationDecision != decision || record.State != ExternalOperationState.Visible ||
                record.NextTransitionSequence != transitionSequence)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration,
                    "Publication state or provider consequence changed while exact closure was confirmed.");
            record.PublicationEffectAmbiguous = false;
            record.Disposition = ExternalOperationDisposition.Discarded;
            record.EffectBoundary = ExternalEffectBoundaryState.StagedPending;
            AddTransition(record, record.State, "PublicationEffectClosedWithoutPublication");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<OperationPreparation> QueryPreparation(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            return resolved.IsSuccess
                ? KernelResult<OperationPreparation>.Ok(resolved.Value!.Preparation)
                : KernelResult<OperationPreparation>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult ValidatePreparedUses(
        ExternalOperationHandle operation,
        RegionOwner principal,
        IReadOnlyList<OperationRegionUseRequest> exactUses)
    {
        ArgumentNullException.ThrowIfNull(exactUses);
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            Record record = resolved.Value!;
            if (record.State != ExternalOperationState.Prepared || record.Preparation.Principal != principal)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Compute ExternalOperation is stale or belongs to another exact principal generation.");
            return record.Preparation.RegionUses.SequenceEqual(exactUses)
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.InvalidRegionState,
                    "Compute ExternalOperation Region uses do not exactly match the live plan.");
        }
    }

    internal ExternalOperationSnapshot[] InspectionSnapshot()
    {
        lock (_gate)
            return _ownedOperations.Values
                .Select(Snapshot)
                .OrderBy(static operation => operation.Operation.OperationId.Value)
                .ToArray();
    }

    internal KernelResult AdvanceForTeardown(RegionOwner principal)
    {
        lock (_gate)
        {
            foreach (Record? record in _ownedOperations.Values.Where(record => record.Preparation.Principal == principal && record.State != ExternalOperationState.Released))
            {
                if (record.PublicationInFlight)
                    return KernelResult.Fail(KernelError.PlatformBindingDraining,
                        "An exact publication action is still in flight; local lifetime cannot advance before it returns.");
                if (record.PublicationEffectAmbiguous)
                    return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                        "Ambiguous staged publication requires exact effect closure before teardown.");
                if (record.State == ExternalOperationState.Submitted && record.Disposition != ExternalOperationDisposition.ProviderLost)
                {
                    record.Disposition = ExternalOperationDisposition.CancellationPending;
                    AddTransition(record, record.State, "TeardownDrainRequired");
                    return KernelResult.Fail(KernelError.PlatformBindingDraining, "Submitted external operation must reach exact completion or provider-loss containment before process reclaim.");
                }

                if (record.Disposition == ExternalOperationDisposition.ProviderLost)
                {
                    return KernelResult.Fail(
                        KernelError.ExternalEffectUncontained,
                        "Provider loss after submission leaves the external effect ambiguous until exact closure or containment is recorded.");
                }

                if (record.State is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible && record.Disposition == ExternalOperationDisposition.Completed)
                {
                    record.Disposition = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
                        ? ExternalOperationDisposition.Discarded
                        : ExternalOperationDisposition.Faulted;
                    AddTransition(record, record.State, "TeardownResultDiscarded");
                }
                else if (record.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted)
                {
                    record.Disposition = ExternalOperationDisposition.Cancelled;
                    AddTransition(record, record.State, "TeardownCancelledBeforeSubmit");
                }

                KernelResult<ExternalOperationSnapshot> release = Release(
                    record.Preparation.Operation,
                    new ReleasePlan(
                        ProviderResourcesClosed: record.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted,
                        ProviderUnavailable: record.Disposition == ExternalOperationDisposition.ProviderLost));
                if (!release.IsSuccess)
                {
                    return release.Error == KernelError.InvalidTransition
                        ? KernelResult.Fail(
                            KernelError.PlatformBindingDraining,
                            "External operation reached local completion or cancellation, but exact provider closure or containment remains pending.")
                        : KernelResult.Fail(release.Error, release.Message!);
                }
            }
            return KernelResult.Ok();
        }
    }

    private KernelResult ValidateUses(Record record)
    {
        if (record.Admission is null)
            return KernelResult.Fail(KernelError.InvalidTransition, "Operation has no admission snapshot.");
        foreach (RegionUseDescriptor use in record.Admission.RegionUses)
        {
            KernelResult<RegionUseDescriptor> validation = _regions.ValidateUse(use.Handle, record.Preparation.Principal);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        }
        return KernelResult.Ok();
    }

    private KernelResult<Record> Resolve(ExternalOperationHandle operation)
    {
        if (!_operations.TryGetValue(operation.OperationId, out Record? record))
            return KernelResult<Record>.Fail(KernelError.ExternalOperationNotFound, "External operation was not found.");
        if (!_ownedOperations.TryGetValue(operation.OperationId, out Record? original) || !ReferenceEquals(original, record))
            return KernelResult<Record>.Fail(KernelError.ExternalOperationNotFound, "External operation route is not the original owner.");
        if (record.Preparation.Operation.Generation != operation.Generation)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "External operation generation is stale.");
        if (record.Preparation.Operation != operation)
            return KernelResult<Record>.Fail(KernelError.InvalidMessage, "External operation handle is forged.");
        return KernelResult<Record>.Ok(record);
    }

    internal object? CaptureOriginalAccountingLifetime(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            KernelResult<Record> resolved = Resolve(operation);
            return resolved.IsSuccess ? resolved.Value : null;
        }
    }

    internal bool HasEndedOriginalAccountingLifetime(object owner, ExternalOperationHandle operation)
    {
        lock (_gate)
            return _ownedOperations.TryGetValue(operation.OperationId, out Record? original) &&
                ReferenceEquals(original, owner) && original.Preparation.Operation == operation &&
                original.State == ExternalOperationState.Released;
    }

    private static bool CanRelease(Record record, ReleasePlan plan)
    {
        if (record.SoftwareRequest is not null && record.Binding is not null && !record.SoftwareClosureProven) return false;
        if (record.InitializedLoad is not null && record.Binding is not null && record.InitializedLoadClosure is null) return false;
        if (record.PublicationEffectAmbiguous || record.RegionInvalidationUnproven) return false;
        // Region reclaim for a resource-bound submitted effect must wait for the
        // separate budget owner to complete exact settlement. Provider closure
        // alone cannot turn a quarantined or in-flight charge into settlement.
        if (record.ResourceBinding is { } resource &&
            record.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) &&
            resource.State != ExternalResourceBindingState.Settled)
            return false;
        if (record.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted)
            return record.Disposition == ExternalOperationDisposition.Cancelled;
        if (record.State == ExternalOperationState.Published)
            return record.Disposition == ExternalOperationDisposition.Published && plan.ProviderResourcesClosed;
        if (record.State is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible)
            return (record.Disposition is ExternalOperationDisposition.Cancelled ||
                    record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged &&
                    record.Disposition is ExternalOperationDisposition.Discarded or ExternalOperationDisposition.Faulted or ExternalOperationDisposition.ProviderLost) &&
                   plan.ProviderResourcesClosed;
        if (record.State == ExternalOperationState.Submitted)
            return record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged &&
                   record.Disposition == ExternalOperationDisposition.ProviderLost &&
                   plan.ProviderResourcesClosed;
        return false;
    }

    private static void FailPublicationRevalidation(Record record)
    {
        record.Disposition = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
            ? ExternalOperationDisposition.Discarded
            : ExternalOperationDisposition.Faulted;
        AddTransition(record, record.State, "PublicationRevalidationFailed");
    }

    private static void Move(Record record, ExternalOperationState next, string eventName)
    {
        ExternalOperationState prior = record.State;
        record.State = next;
        record.Transitions.Add(new ExternalOperationTransition(record.NextTransitionSequence++, prior, next, eventName));
    }

    private static void AddTransition(Record record, ExternalOperationState state, string eventName) =>
        record.Transitions.Add(new ExternalOperationTransition(record.NextTransitionSequence++, state, state, eventName));

    private static KernelResult ValidateEffectPolicy(ExternalPublicationPolicy publication, ExternalEffectPolicy effect)
    {
        if (!Enum.IsDefined(effect.EffectClass) || !Enum.IsDefined(effect.ReplayProtection))
            return KernelResult.Fail(KernelError.InvalidMessage, "External effect classification is invalid.");
        if (publication == ExternalPublicationPolicy.Staged)
            return effect.EffectClass == ExternalEffectClass.StagedReversibleUntilPublish && effect.ReplayProtection == ExternalReplayProtection.None
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.InvalidMessage, "Staged publication requires the reversible-until-publish effect class.");
        if (!effect.ReplayConsumerAcknowledged)
            return KernelResult.Fail(KernelError.PlatformDenied, "Direct output requires replay-consumer acknowledgement of an already-visible effect.");
        if (effect.EffectClass == ExternalEffectClass.SnapshotOrIdempotenceRequired && effect.ReplayProtection != ExternalReplayProtection.None)
            return KernelResult.Ok();
        if (effect.EffectClass == ExternalEffectClass.IrreversibleBarrier && effect.ReplayProtection == ExternalReplayProtection.None)
            return KernelResult.Ok();
        return KernelResult.Fail(KernelError.PlatformDenied, "Direct output requires snapshot/idempotence protection or an explicit irreversible barrier.");
    }

    private static ExternalOperationSnapshot Snapshot(Record record) => new(
        record.Preparation.Operation,
        record.Preparation.Principal,
        record.State,
        record.Disposition,
        record.Admission,
        record.Binding,
        record.Preparation.VisibilityRequirement,
        record.Preparation.PublicationPolicy,
        record.Preparation.EffectPolicy,
        record.EffectBoundary,
        Array.AsReadOnly(record.Transitions.ToArray()));

    private static KernelResult<T> InvalidTransition<T>(Record record, string message) =>
        KernelResult<T>.Fail(KernelError.InvalidTransition, $"{message} Current state is {record.State}/{record.Disposition}.");
}
