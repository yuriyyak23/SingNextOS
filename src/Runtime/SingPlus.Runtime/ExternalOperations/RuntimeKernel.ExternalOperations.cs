using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<OperationPreparation> PrepareExternalOperation(
        ProcessHandle principal,
        IReadOnlyList<OperationRegionUseRequest> regionUses,
        ExternalVisibilityRequirement visibilityRequirement,
        ExternalPublicationPolicy publicationPolicy,
        ExternalEffectPolicy? effectPolicy = null,
        TraceCausalContext? traceContext = null)
        => PrepareExternalOperationCore(principal, regionUses, visibilityRequirement, publicationPolicy,
            effectPolicy, out _, traceContext);

    internal KernelResult<OperationPreparation> PrepareExternalOperationCore(
        ProcessHandle principal,
        IReadOnlyList<OperationRegionUseRequest> regionUses,
        ExternalVisibilityRequirement visibilityRequirement,
        ExternalPublicationPolicy publicationPolicy,
        ExternalEffectPolicy? effectPolicy,
        out OperationPreparation? originalPreparation,
        TraceCausalContext? traceContext = null)
    {
        originalPreparation = null;
        KernelResult<RegionOwner> resolved = ResolveExternalOperationPrincipal(principal, requireNewEffect: true);
        if (!resolved.IsSuccess) return KernelResult<OperationPreparation>.Fail(resolved.Error, resolved.Message!);
        ArgumentNullException.ThrowIfNull(regionUses);
        OperationRegionUseRequest[] requests = regionUses.ToArray();
        KernelResult<OperationPreparation> prepared;
        lock (_platformMemoryUseGate)
        {
            KernelResult<RegionOwner> current = ResolveExternalOperationPrincipal(principal, requireNewEffect: true);
            if (!current.IsSuccess) return KernelResult<OperationPreparation>.Fail(current.Error, current.Message!);
            prepared = ExternalOperations.Prepare(current.Value!, requests, visibilityRequirement, publicationPolicy, effectPolicy);
            if (prepared.IsSuccess) originalPreparation = prepared.Value;
        }
        if (prepared.IsSuccess)
        {
            RecordTrace(principal, TraceEventKind.ExternalOperationAdmitted, traceContext, "external-operation",
                $"{prepared.Value!.Operation.OperationId.Value}:{prepared.Value.Operation.Generation.Value}", "prepared", "prepared");
            lock (_platformMemoryUseGate)
            {
                KernelResult valid = ValidateExternalOperationPrincipal(principal, prepared.Value.Operation, requireNewEffect: true);
                if (!valid.IsSuccess) return KernelResult<OperationPreparation>.Fail(valid.Error, valid.Message!);
                KernelResult<ExternalOperationSnapshot> current = ExternalOperations.Query(prepared.Value.Operation);
                if (!current.IsSuccess) return KernelResult<OperationPreparation>.Fail(current.Error, current.Message!);
                if (current.Value!.State != ExternalOperationState.Prepared || current.Value.Disposition != ExternalOperationDisposition.Active)
                    return KernelResult<OperationPreparation>.Fail(KernelError.InvalidTransition, "Operation changed during preparation observation.");
            }
        }
        return prepared;
    }

    public KernelResult<OperationAdmissionSnapshot> AdmitExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity = default,
        ExternalCancellationSupport cancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly,
        CancellationScopeHandle? cancellationScope = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        TraceCausalContext? traceContext = null)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
        if (!validation.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult cancellationValidation = ExternalOperationAuthority.ValidateCancellationSupport(cancellationSupport);
        if (!cancellationValidation.IsSuccess)
            return KernelResult<OperationAdmissionSnapshot>.Fail(cancellationValidation.Error, cancellationValidation.Message!);
        if (cancellationScope is { } scope)
        {
            KernelResult binding = CancellationScopes.BindConsumer(principal, scope,
                $"external:{operation.OperationId.Value}:{operation.Generation.Value}");
            if (!binding.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(binding.Error, binding.Message!);
            long timestamp = CancellationScopes.CaptureTimestamp(scope);
            KernelResult<CancellationObservation> temporal = CancellationScopes.Observe(principal, scope, timestamp);
            if (!temporal.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(temporal.Error, temporal.Message!);
            if (temporal.Value!.CancellationRequested)
            {
                CancellationDisposition outcome;
                lock (_platformMemoryUseGate)
                {
                    KernelResult fresh = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
                    if (!fresh.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(fresh.Error, fresh.Message!);
                    KernelResult<ExternalOperationSnapshot> original = ExternalOperations.Query(operation);
                    if (!original.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(original.Error, original.Message!);
                    if (original.Value!.State == ExternalOperationState.Published)
                        outcome = CancellationDisposition.TooLateEffectMayExist;
                    else if (original.Value.State == ExternalOperationState.Released)
                        outcome = ReleasedCancellationOutcome(original.Value);
                    else
                    {
                        KernelResult<ExternalOperationSnapshot> cancelled = ExternalOperations.Cancel(operation, providerCancellationSupported: false);
                        if (!cancelled.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(cancelled.Error, cancelled.Message!);
                        outcome = cancelled.Value!.Binding is null &&
                            cancelled.Value.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted &&
                            cancelled.Value.Disposition == ExternalOperationDisposition.Cancelled
                            ? CancellationDisposition.CancelledBeforeEffect
                            : CancellationDisposition.ProviderClosurePending;
                    }
                }
                _ = CancellationScopes.RecordDisposition(principal, scope, outcome, timestamp);
                return KernelResult<OperationAdmissionSnapshot>.Fail(KernelError.DeadlineExpired, "Cancellation interlocked admission; original owner facts determine effect containment.");
            }
        }
        KernelResult<OperationAdmissionSnapshot> admitted = CommitExternalOperationAdmissionBudget(principal, operation, qosHint,
            () => ExternalOperations.Admit(operation, dependencies, serviceIdentity, cancellationSupport, cancellationScope));
        if (!admitted.IsSuccess) return admitted;
        RecordTrace(principal, TraceEventKind.ExternalOperationAdmitted, traceContext, "external-operation",
            $"{operation.OperationId.Value}:{operation.Generation.Value}", "admitted", "admitted");
        return ValidateExternalAdmissionPublication(principal, operation, admitted);
    }

    internal KernelResult<OperationAdmissionSnapshot> AdmitExternalOperationForVirtualContext(
        ProcessHandle principal, ExternalOperationHandle operation, OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity = default,
        ExternalCancellationSupport cancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly,
        CancellationScopeHandle? cancellationScope = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        ulong initializedStorageBytes = 0)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
        if (!validation.IsSuccess)
            return KernelResult<OperationAdmissionSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult cancellationValidation = ExternalOperationAuthority.ValidateCancellationSupport(cancellationSupport);
        if (!cancellationValidation.IsSuccess)
            return KernelResult<OperationAdmissionSnapshot>.Fail(cancellationValidation.Error, cancellationValidation.Message!);
        if (cancellationScope is { } scope)
        {
            KernelResult binding = CancellationScopes.BindConsumer(principal, scope,
                $"external:{operation.OperationId.Value}:{operation.Generation.Value}");
            if (!binding.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(binding.Error, binding.Message!);
            KernelResult<CancellationObservation> temporal = CancellationScopes.Observe(principal, scope);
            if (!temporal.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(temporal.Error, temporal.Message!);
            if (temporal.Value!.CancellationRequested)
            {
                _ = ExternalOperations.Cancel(operation, providerCancellationSupported: false);
                _ = CancellationScopes.RecordDisposition(principal, scope, CancellationDisposition.CancelledBeforeEffect);
                return KernelResult<OperationAdmissionSnapshot>.Fail(KernelError.DeadlineExpired, "Cancellation was requested before external-operation admission; no provider effect was admitted.");
            }
        }
        return CommitExternalOperationAdmissionBudget(principal, operation, qosHint,
            () => ExternalOperations.AdmitForExactPlatformMappings(operation, dependencies, serviceIdentity, cancellationSupport, cancellationScope), initializedStorageBytes);
    }

    private KernelResult<OperationAdmissionSnapshot> CommitExternalOperationAdmissionBudget(
        ProcessHandle principal, ExternalOperationHandle operation, AdmissionQosHint qosHint,
        Func<KernelResult<OperationAdmissionSnapshot>> commit, ulong initializedStorageBytes = 0)
    {
        List<BudgetTraceObservation> observations = new List<BudgetTraceObservation>();
        KernelResult<OperationAdmissionSnapshot> admitted;
        lock (_platformMemoryUseGate)
        {
            KernelResult valid = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
            if (!valid.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(valid.Error, valid.Message!);
            if (initializedStorageBytes is not (0 or 4096))
                return KernelResult<OperationAdmissionSnapshot>.Fail(KernelError.PlatformUnsupported, "One initialized storage page is supported.");
            BudgetAmount[] amounts = initializedStorageBytes == 0 ? [new(ServiceBudgetDimension.ExternalOperations, 1)] :
                [new(ServiceBudgetDimension.ExternalOperations, 1), new(ServiceBudgetDimension.OwnedMemoryBytes, initializedStorageBytes)];
            KernelResult<BudgetReservationHandle?> budget = ReserveAttachedBudget(principal, amounts,
                BudgetReservationLifetime.ExternalEffect, qosHint, observations);
            if (!budget.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(budget.Error, budget.Message!);
            if (initializedStorageBytes != 0 && budget.Value is null)
                return KernelResult<OperationAdmissionSnapshot>.Fail(KernelError.PlatformDenied, "Initialized storage requires an attached budget owner.");
            if (budget.Value is { } originalReservation)
            {
                object? originalOperation = ExternalOperations.CaptureOriginalAccountingLifetime(operation);
                KernelResult bound = originalOperation is null
                    ? KernelResult.Fail(KernelError.ExternalOperationNotFound, "Original operation accounting owner is unavailable.")
                    : Budgets.BindOriginalExternalOperationCharge(principal, originalReservation, operation, originalOperation);
                if (!bound.IsSuccess)
                {
                    _ = ReleaseAttachedBudget(principal, budget.Value, observations);
                    return KernelResult<OperationAdmissionSnapshot>.Fail(bound.Error, bound.Message!);
                }
            }
            admitted = commit();
            if (!admitted.IsSuccess) _ = ReleaseAttachedBudget(principal, budget.Value, observations);
            else if (budget.Value is { } reservation)
                lock (_externalOperationBudgetReservationsGate)
                    _externalOperationBudgetReservations.Add(operation, (principal, reservation));
        }
        EmitBudgetObservations(observations);
        if (!admitted.IsSuccess) return admitted;
        return ValidateExternalAdmissionPublication(principal, operation, admitted);
    }

    private KernelResult<OperationAdmissionSnapshot> ValidateExternalAdmissionPublication(
        ProcessHandle principal, ExternalOperationHandle operation, KernelResult<OperationAdmissionSnapshot> admitted)
    {
        lock (_platformMemoryUseGate)
        {
            KernelResult valid = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
            if (!valid.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(valid.Error, valid.Message!);
            KernelResult<ExternalOperationSnapshot> current = ExternalOperations.Query(operation);
            if (!current.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(current.Error, current.Message!);
            if (current.Value!.State != ExternalOperationState.Admitted || current.Value.Disposition != ExternalOperationDisposition.Active)
                return KernelResult<OperationAdmissionSnapshot>.Fail(KernelError.InvalidTransition, "Operation changed during admission budget observation.");
            return admitted;
        }
    }

    public KernelResult<OperationBinding> RecordExternalOperationSubmission(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        TraceCausalContext? traceContext = null)
        => RecordExternalOperationSubmissionCore(principal, operation, currentDependencies, out _, traceContext);

    internal KernelResult<OperationBinding> RecordExternalOperationSubmissionCore(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        out OperationBinding? originalBinding,
        TraceCausalContext? traceContext = null)
    {
        originalBinding = null;
        KernelResult<OperationBinding> submitted;
        lock (_platformMemoryUseGate)
        {
            KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
            if (!validation.IsSuccess) return KernelResult<OperationBinding>.Fail(validation.Error, validation.Message!);
            submitted = ExternalOperations.RecordSubmission(operation, currentDependencies);
            if (submitted.IsSuccess) originalBinding = submitted.Value;
        }
        if (submitted.IsSuccess)
        {
            RecordTrace(principal, TraceEventKind.ExternalOperationSubmitted, traceContext, "external-operation",
                $"{operation.OperationId.Value}:{operation.Generation.Value}", "submitted", "accepted");
            lock (_platformMemoryUseGate)
            {
                KernelResult valid = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
                if (!valid.IsSuccess) return KernelResult<OperationBinding>.Fail(valid.Error, valid.Message!);
                KernelResult<ExternalOperationSnapshot> current = ExternalOperations.Query(operation);
                if (!current.IsSuccess) return KernelResult<OperationBinding>.Fail(current.Error, current.Message!);
                if (current.Value!.Binding != submitted.Value || current.Value.State != ExternalOperationState.Submitted ||
                    current.Value.Disposition != ExternalOperationDisposition.Active)
                    return KernelResult<OperationBinding>.Fail(KernelError.InvalidTransition, "Submission changed during observation; original binding remains owned for cleanup.");
            }
        }
        return submitted;
    }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationCompletion(
        ProcessHandle principal,
        OperationCompletion completion,
        TraceCausalContext? traceContext = null)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, completion.Binding.Operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult<ExternalOperationSnapshot> completed = ExternalOperations.RecordCompletion(completion);
        if (completed.IsSuccess)
        {
            RecordTrace(principal, TraceEventKind.ExternalOperationCompleted, traceContext, "external-operation",
                $"{completion.Binding.Operation.OperationId.Value}:{completion.Binding.Operation.Generation.Value}", "device-complete", "completed");
            EmitExternalOperationTrace(completion.Binding.Operation);
        }
        return completed;
    }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationVisibility(
        ProcessHandle principal,
        OperationVisibilityEvidence evidence,
        TraceCausalContext? traceContext = null)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, evidence.Binding.Operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult<ExternalOperationSnapshot> visible = ExternalOperations.RecordVisibility(evidence);
        if (visible.IsSuccess)
        {
            RecordTrace(principal, TraceEventKind.ExternalOperationVisible, traceContext, "external-operation",
                $"{evidence.Binding.Operation.OperationId.Value}:{evidence.Binding.Operation.Generation.Value}", "visible", "observed");
            EmitExternalOperationTrace(evidence.Binding.Operation);
        }
        return visible;
    }

    public KernelResult<ExternalOperationSnapshot> PublishExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        PublicationPlan plan,
        Action publicationAction,
        TraceCausalContext? traceContext = null)
    {
        ArgumentNullException.ThrowIfNull(publicationAction);
        lock (_platformMemoryUseGate)
            if (_softwareSecureOwners.Any(x => x.Operation == operation))
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformUnsupported,
                    "Software no-effect admission has no OS publication contour.");
        return PublishExternalOperation(principal, operation, currentDependencies, plan,
            _ => publicationAction(), traceContext);
    }

    public KernelResult<ExternalOperationSnapshot> PublishExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        PublicationPlan plan,
        Action<ExternalPublicationDecisionV1> publicationAction,
        TraceCausalContext? traceContext = null)
        => PublishExternalOperation(principal, operation, currentDependencies, plan, publicationAction, null, traceContext);

    internal Action? ExternalPublicationBeforeAdmissionQualificationHook { private get; set; }

    internal KernelResult<ExternalOperationSnapshot> PublishExternalOperation(
        ProcessHandle principal, ExternalOperationHandle operation, OperationDependencySnapshot currentDependencies,
        PublicationPlan plan, Action<ExternalPublicationDecisionV1> publicationAction,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>>? sourceCommit,
        TraceCausalContext? traceContext = null,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>>? sourceAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(publicationAction);
        bool staged = plan.Policy == ExternalPublicationPolicy.Staged;
        if (!staged && sourceAdmission is not null)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformUnsupported,
                "The source admission hook requires a staged action contour.");
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: staged);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult<ExternalOperationSnapshot> CommitProcessPublication(Func<KernelResult<ExternalOperationSnapshot>> commit)
        {
            // Pure owner commit only. The source hook and publication action run
            // outside this process stop/publication lock.
            lock (_platformMemoryUseGate)
            {
                KernelResult current = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
                return current.IsSuccess ? commit() :
                    KernelResult<ExternalOperationSnapshot>.Fail(current.Error, current.Message!);
            }
        }
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>>? finalCommit = sourceCommit;
        if (staged)
            finalCommit = sourceCommit is null ? CommitProcessPublication :
                commit => sourceCommit(() => CommitProcessPublication(commit));
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>>? initialAdmission = null;
        if (staged)
            initialAdmission = sourceAdmission is null ? CommitProcessPublication :
                admit => sourceAdmission(() => CommitProcessPublication(admit));
        if (staged) ExternalPublicationBeforeAdmissionQualificationHook?.Invoke();
        KernelResult<ExternalOperationSnapshot> published = ExternalOperations.Publish(operation, currentDependencies, plan, publicationAction, finalCommit,
            initialAdmission);
        if (published.IsSuccess)
        {
            RecordTrace(principal, TraceEventKind.ExternalOperationPublished, traceContext, "external-operation",
                $"{operation.OperationId.Value}:{operation.Generation.Value}", "published", "published");
        }
        // A failed publication callback may have crossed the external-effect boundary.
        // Project the committed owner transition even when the call returns failure.
        EmitExternalOperationTrace(operation);
        return published;
    }

    internal KernelResult<ExternalOperationSnapshot> ReconcileFailedExternalPublication(
        ProcessHandle principal, ExternalOperationHandle operation,
        ExternalPublicationDecisionV1 decision, Func<KernelResult> confirmNoPublication)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult<ExternalOperationSnapshot> reconciled = ExternalOperations.ReconcileFailedPublication(operation, decision,
            confirmNoPublication);
        if (reconciled.IsSuccess)
            EmitExternalOperationTrace(operation);
        return reconciled;
    }

    public KernelResult<ExternalOperationSnapshot> CancelExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        bool providerCancellationSupported)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult<ExternalOperationSnapshot> cancelled = ExternalOperations.Cancel(operation, providerCancellationSupported);
        if (cancelled.IsSuccess)
            EmitExternalOperationTrace(operation);
        return cancelled;
    }

    public KernelResult<CancellationObservation> RequestExternalOperationCancellation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        CancellationScopeHandle scope,
        bool providerCancellationSupported)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<CancellationObservation>.Fail(validation.Error, validation.Message!);
        KernelResult<ExternalOperationSnapshot> before = ExternalOperations.Query(operation);
        if (!before.IsSuccess) return KernelResult<CancellationObservation>.Fail(before.Error, before.Message!);
        if (before.Value!.Admission?.CancellationScope != scope)
            return KernelResult<CancellationObservation>.Fail(KernelError.StaleGeneration, "External operation is not bound to the exact cancellation scope generation.");

        KernelResult<CancellationObservation> requested = CancellationScopes.Request(principal, scope);
        if (!requested.IsSuccess || requested.Value!.Disposition == CancellationDisposition.Stale)
            return requested;
        if (requested.Value.Disposition is CancellationDisposition.CancelledBeforeEffect or
            CancellationDisposition.TooLateEffectMayExist or CancellationDisposition.CompletedBeforeCancellation or
            CancellationDisposition.ProviderEffectContained or CancellationDisposition.Unsupported)
            return requested;

        // Request observation may invoke the clock; use the actual owner after it.
        KernelResult<ExternalOperationSnapshot> current = ExternalOperations.Query(operation);
        if (!current.IsSuccess) return KernelResult<CancellationObservation>.Fail(current.Error, current.Message!);
        if (current.Value!.State == ExternalOperationState.Published ||
            current.Value.State == ExternalOperationState.Released && current.Value.Disposition == ExternalOperationDisposition.Published)
            return CancellationScopes.RecordDisposition(principal, scope, CancellationDisposition.TooLateEffectMayExist);

        KernelResult<ExternalOperationSnapshot> cancelled = ExternalOperations.Cancel(operation, providerCancellationSupported);
        if (!cancelled.IsSuccess)
            return KernelResult<CancellationObservation>.Fail(cancelled.Error, cancelled.Message!);

        CancellationDisposition disposition = cancelled.Value!.State == ExternalOperationState.Released
            ? ReleasedCancellationOutcome(cancelled.Value)
            : cancelled.Value.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted &&
              cancelled.Value.Disposition == ExternalOperationDisposition.Cancelled
            ? CancellationDisposition.CancelledBeforeEffect
            : CancellationDisposition.ProviderClosurePending;
        return CancellationScopes.RecordDisposition(principal, scope, disposition);
    }

    private static CancellationDisposition ReleasedCancellationOutcome(ExternalOperationSnapshot released) =>
        released.Binding is null && released.Disposition == ExternalOperationDisposition.Cancelled
            ? CancellationDisposition.CancelledBeforeEffect
            : released.Disposition == ExternalOperationDisposition.Published
                ? CancellationDisposition.CompletedBeforeCancellation
                : CancellationDisposition.ProviderEffectContained;

    internal Action? ResourceProviderLossAfterBindingReadQualificationHook { private get; set; }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationProviderLoss(
        ProcessHandle principal,
        ExternalOperationHandle operation)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        KernelResult<ExternalOperationSnapshot> lost = ExternalOperations.RecordProviderLoss(operation);
        KernelResult<ExternalOperationSnapshot> observed = lost.IsSuccess ? lost : ExternalOperations.Query(operation);
        bool lossRecorded = observed.IsSuccess &&
            observed.Value!.State is (ExternalOperationState.Submitted or ExternalOperationState.DeviceComplete or ExternalOperationState.Visible) &&
            observed.Value.Transitions.Any(transition => transition.Event == "ProviderLost");
        KernelResult? resourceQuarantineFailure = null;
        if (lossRecorded && ExternalOperations.QueryResourceBinding(operation) is { IsSuccess: true, Value: { } resource })
        {
            if (resource.State != ExternalResourceBindingState.Settled)
            {
                ResourceProviderLossAfterBindingReadQualificationHook?.Invoke();
                KernelResult<BudgetReservationSnapshot> budgetQuarantine = Budgets.QuarantineLease(resource.BudgetOwner, resource.Lease);
                KernelResult<ExternalOperationResourceBinding> bindingQuarantine = ExternalOperations.MarkResourceQuarantined(operation);
                KernelResult<ExternalOperationResourceBinding> current = ExternalOperations.QueryResourceBinding(operation);
                bool exactSettlementAlreadyCommitted = current.IsSuccess &&
                    current.Value!.Operation == operation &&
                    current.Value.TerminalReceiptSequence is not null &&
                    current.Value.TerminalEvidenceId is not null &&
                    current.Value.SettledAmount is { } amount &&
                    (current.Value.State is ExternalResourceBindingState.Quarantined or
                        ExternalResourceBindingState.Settled) &&
                    Budgets.IsExactSettledExternalLease(current.Value.BudgetOwner, current.Value.Lease, amount) &&
                    current.Value.BudgetOwner == resource.BudgetOwner &&
                    current.Value.Lease == resource.Lease;
                if (!budgetQuarantine.IsSuccess && !exactSettlementAlreadyCommitted)
                    resourceQuarantineFailure = KernelResult.Fail(budgetQuarantine.Error, budgetQuarantine.Message!);
                if (!bindingQuarantine.IsSuccess && !exactSettlementAlreadyCommitted)
                    resourceQuarantineFailure ??= KernelResult.Fail(bindingQuarantine.Error, bindingQuarantine.Message!);
            }
        }
        if (lossRecorded)
        {
            RecordTrace(principal, TraceEventKind.ExternalOperationFaulted, null, "external-operation",
                operation.OperationId.Value.ToString(), "provider-lost", "effect-closure-ambiguous");
            EmitExternalOperationTrace(operation);
        }
        if (resourceQuarantineFailure is { } failure && lost.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                $"Provider loss is recorded, but exact resource quarantine failed: {failure.Message}");
        return lost;
    }

    public KernelResult<ExternalOperationSnapshot> ReleaseExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        ReleasePlan plan,
        TraceCausalContext? traceContext = null)
        => ReleaseExternalOperationCore(principal, operation, plan, null, traceContext);

    internal KernelResult<ExternalOperationSnapshot> ReleaseExternalOperationCore(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        ReleasePlan plan,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>>? sourceCommit,
        TraceCausalContext? traceContext = null)
    {
        ExternalOperationSnapshot? before = null;
        KernelResult<ExternalOperationSnapshot> CommitRelease()
        {
            lock (_platformMemoryUseGate)
            {
                if (!IntactKernelInitializedLoads() || _originalKernelInitializedLoads.Any(x => x.Operation == operation &&
                    (!x.HasOriginalBufferLease || x.Invoked && (x.Observation is not { ResourcesClosed: true } memoryProof || !ReferenceEquals(memoryProof.Intent, x.Intent)))))
                    return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                        "Original initialized-load custody remains unknown.");
                if (_softwareSecureOwners.Any(x => x.Operation == operation &&
                    (!HasOriginalSoftwareSecureRecord(x) || x.Invoked &&
                     (x.Observation is not { ResourcesClosed: true } proof || !ReferenceEquals(proof.Request, x.Request)))))
                    return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                        "Original software provider closure remains unknown; caller release flags cannot replace it.");
                KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
                if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
                KernelResult<ExternalOperationSnapshot> current = ExternalOperations.Query(operation);
                if (!current.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(current.Error, current.Message!);
                before = current.Value!;
                return ExternalOperations.Release(operation, plan);
            }
        }
        KernelResult<ExternalOperationSnapshot> released = sourceCommit is null ? CommitRelease() : sourceCommit(CommitRelease);
        if (released.IsSuccess && before is null)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition, "Release source did not commit the original owner.");
        // Scope, budget and trace observations remain outside the pure source/owner commit.
        if (released.IsSuccess && before!.Admission?.CancellationScope is { } scope)
        {
            KernelResult<CancellationObservation> temporal = CancellationScopes.Observe(principal, scope);
            if (temporal.IsSuccess && temporal.Value!.CancellationRequested)
            {
                CancellationDisposition outcome = ReleasedCancellationOutcome(released.Value!);
                _ = CancellationScopes.RecordDisposition(principal, scope, outcome);
            }
        }
        if (released.IsSuccess)
            ReleaseExternalOperationBudget(principal, operation);
        if (released.IsSuccess)
        {
            RecordTrace(principal, TraceEventKind.ExternalOperationReleased, traceContext, "external-operation",
                $"{operation.OperationId.Value}:{operation.Generation.Value}", "released", "closed-or-contained");
            EmitExternalOperationTrace(operation);
            CompleteExternalOperationTrace(operation);
        }
        return released;
    }

    public KernelResult<ExternalOperationSnapshot> QueryExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation)
    {
        KernelResult validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        return validation.IsSuccess
            ? ExternalOperations.Query(operation)
            : KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
    }

    private KernelResult ValidateExternalOperationPrincipal(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        bool requireNewEffect)
    {
        KernelResult<RegionOwner> resolved = ResolveExternalOperationPrincipal(principal, requireNewEffect);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        KernelResult<ExternalOperationSnapshot> snapshot = ExternalOperations.Query(operation);
        if (!snapshot.IsSuccess) return KernelResult.Fail(snapshot.Error, snapshot.Message!);
        return snapshot.Value!.Principal == resolved.Value!
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.WrongRegionOwner, "External operation belongs to a different principal.");
    }

    private KernelResult<RegionOwner> ResolveExternalOperationPrincipal(
        ProcessHandle principal,
        bool requireNewEffect)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult<RegionOwner>.Fail(resolved.Error, resolved.Message!);
        if (requireNewEffect)
        {
            KernelResult effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
            if (!effect.IsSuccess) return KernelResult<RegionOwner>.Fail(effect.Error, effect.Message!);
        }
        return KernelResult<RegionOwner>.Ok(new RegionOwner(resolved.Value!.DomainId, principal.Generation));
    }
}
