using SingPlus.Contracts;

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
    {
        var resolved = ResolveExternalOperationPrincipal(principal, requireNewEffect: true);
        if (!resolved.IsSuccess) return KernelResult<OperationPreparation>.Fail(resolved.Error, resolved.Message!);
        var prepared = ExternalOperations.Prepare(resolved.Value!, regionUses, visibilityRequirement, publicationPolicy, effectPolicy);
        if (prepared.IsSuccess)
            RecordTrace(principal, TraceEventKind.ExternalOperationAdmitted, traceContext, "external-operation",
                $"{prepared.Value!.Operation.OperationId.Value}:{prepared.Value.Operation.Generation.Value}", "prepared", "prepared");
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
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
        if (!validation.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(validation.Error, validation.Message!);
        if (cancellationScope is { } scope)
        {
            var binding = CancellationScopes.BindConsumer(principal, scope,
                $"external:{operation.OperationId.Value}:{operation.Generation.Value}");
            if (!binding.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(binding.Error, binding.Message!);
            var temporal = CancellationScopes.Observe(principal, scope);
            if (!temporal.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(temporal.Error, temporal.Message!);
            if (temporal.Value!.CancellationRequested)
            {
                _ = ExternalOperations.Cancel(operation, providerCancellationSupported: false);
                _ = CancellationScopes.RecordDisposition(principal, scope, CancellationDisposition.CancelledBeforeEffect);
                return KernelResult<OperationAdmissionSnapshot>.Fail(KernelError.DeadlineExpired, "Cancellation was requested before external-operation admission; no provider effect was admitted.");
            }
        }
        var budget = ReserveAttachedBudget(principal,
            [new(ServiceBudgetDimension.ExternalOperations, 1)],
            BudgetReservationLifetime.ExternalEffect,
            qosHint);
        if (!budget.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(budget.Error, budget.Message!);
        var admitted = ExternalOperations.Admit(operation, dependencies, serviceIdentity, cancellationSupport, cancellationScope);
        if (!admitted.IsSuccess)
        {
            _ = ReleaseAttachedBudget(principal, budget.Value);
            return admitted;
        }
        if (budget.Value is { } reservation)
            _externalOperationBudgetReservations.Add(operation, (principal, reservation));
        RecordTrace(principal, TraceEventKind.ExternalOperationAdmitted, traceContext, "external-operation",
            $"{operation.OperationId.Value}:{operation.Generation.Value}", "admitted", "admitted");
        return admitted;
    }

    internal KernelResult<OperationAdmissionSnapshot> AdmitExternalOperationForVirtualContext(
        ProcessHandle principal, ExternalOperationHandle operation, OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity = default,
        ExternalCancellationSupport cancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly,
        CancellationScopeHandle? cancellationScope = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
        if (!validation.IsSuccess)
            return KernelResult<OperationAdmissionSnapshot>.Fail(validation.Error, validation.Message!);
        if (cancellationScope is { } scope)
        {
            var binding = CancellationScopes.BindConsumer(principal, scope,
                $"external:{operation.OperationId.Value}:{operation.Generation.Value}");
            if (!binding.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(binding.Error, binding.Message!);
            var temporal = CancellationScopes.Observe(principal, scope);
            if (!temporal.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(temporal.Error, temporal.Message!);
            if (temporal.Value!.CancellationRequested)
            {
                _ = ExternalOperations.Cancel(operation, providerCancellationSupported: false);
                _ = CancellationScopes.RecordDisposition(principal, scope, CancellationDisposition.CancelledBeforeEffect);
                return KernelResult<OperationAdmissionSnapshot>.Fail(KernelError.DeadlineExpired, "Cancellation was requested before external-operation admission; no provider effect was admitted.");
            }
        }
        var budget = ReserveAttachedBudget(principal,
            [new(ServiceBudgetDimension.ExternalOperations, 1)],
            BudgetReservationLifetime.ExternalEffect,
            qosHint);
        if (!budget.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(budget.Error, budget.Message!);
        var admitted = ExternalOperations.AdmitForExactPlatformMappings(
            operation, dependencies, serviceIdentity, cancellationSupport, cancellationScope);
        if (!admitted.IsSuccess)
        {
            _ = ReleaseAttachedBudget(principal, budget.Value);
            return admitted;
        }
        if (budget.Value is { } reservation)
            _externalOperationBudgetReservations.Add(operation, (principal, reservation));
        return admitted;
    }

    public KernelResult<OperationBinding> RecordExternalOperationSubmission(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        TraceCausalContext? traceContext = null)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
        if (!validation.IsSuccess) return KernelResult<OperationBinding>.Fail(validation.Error, validation.Message!);
        var submitted = ExternalOperations.RecordSubmission(operation, currentDependencies);
        if (submitted.IsSuccess)
            RecordTrace(principal, TraceEventKind.ExternalOperationSubmitted, traceContext, "external-operation",
                $"{operation.OperationId.Value}:{operation.Generation.Value}", "submitted", "accepted");
        return submitted;
    }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationCompletion(
        ProcessHandle principal,
        OperationCompletion completion,
        TraceCausalContext? traceContext = null)
    {
        var validation = ValidateExternalOperationPrincipal(principal, completion.Binding.Operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        var completed = ExternalOperations.RecordCompletion(completion);
        if (completed.IsSuccess)
            RecordTrace(principal, TraceEventKind.ExternalOperationCompleted, traceContext, "external-operation",
                $"{completion.Binding.Operation.OperationId.Value}:{completion.Binding.Operation.Generation.Value}", "device-complete", "completed");
        return completed;
    }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationVisibility(
        ProcessHandle principal,
        OperationVisibilityEvidence evidence,
        TraceCausalContext? traceContext = null)
    {
        var validation = ValidateExternalOperationPrincipal(principal, evidence.Binding.Operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        var visible = ExternalOperations.RecordVisibility(evidence);
        if (visible.IsSuccess)
            RecordTrace(principal, TraceEventKind.ExternalOperationVisible, traceContext, "external-operation",
                $"{evidence.Binding.Operation.OperationId.Value}:{evidence.Binding.Operation.Generation.Value}", "visible", "observed");
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
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        var published = ExternalOperations.Publish(operation, currentDependencies, plan, publicationAction);
        if (published.IsSuccess)
            RecordTrace(principal, TraceEventKind.ExternalOperationPublished, traceContext, "external-operation",
                $"{operation.OperationId.Value}:{operation.Generation.Value}", "published", "published");
        return published;
    }

    public KernelResult<ExternalOperationSnapshot> CancelExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        bool providerCancellationSupported)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.Cancel(operation, providerCancellationSupported);
    }

    public KernelResult<CancellationObservation> RequestExternalOperationCancellation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        CancellationScopeHandle scope,
        bool providerCancellationSupported)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<CancellationObservation>.Fail(validation.Error, validation.Message!);
        var before = ExternalOperations.Query(operation);
        if (!before.IsSuccess) return KernelResult<CancellationObservation>.Fail(before.Error, before.Message!);
        if (before.Value!.Admission?.CancellationScope != scope)
            return KernelResult<CancellationObservation>.Fail(KernelError.StaleGeneration, "External operation is not bound to the exact cancellation scope generation.");

        var requested = CancellationScopes.Request(principal, scope);
        if (!requested.IsSuccess || requested.Value!.Disposition == CancellationDisposition.Stale)
            return requested;

        if (before.Value.State == ExternalOperationState.Published)
            return CancellationScopes.RecordDisposition(principal, scope, CancellationDisposition.TooLateEffectMayExist);

        var cancelled = ExternalOperations.Cancel(operation, providerCancellationSupported);
        if (!cancelled.IsSuccess)
            return KernelResult<CancellationObservation>.Fail(cancelled.Error, cancelled.Message!);

        var disposition = before.Value.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted
            ? CancellationDisposition.CancelledBeforeEffect
            : CancellationDisposition.ProviderClosurePending;
        return CancellationScopes.RecordDisposition(principal, scope, disposition);
    }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationProviderLoss(
        ProcessHandle principal,
        ExternalOperationHandle operation)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        var lost = ExternalOperations.RecordProviderLoss(operation);
        if (lost.IsSuccess && ExternalOperations.QueryResourceBinding(operation) is { IsSuccess: true, Value: { } resource })
        {
            _ = Budgets.QuarantineLease(resource.BudgetOwner, resource.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(operation);
        }
        if (lost.IsSuccess)
            RecordTrace(principal, TraceEventKind.ExternalOperationFaulted, null, "external-operation",
                operation.OperationId.Value.ToString(), "provider-lost", "effect-closure-ambiguous");
        return lost;
    }

    public KernelResult<ExternalOperationSnapshot> ReleaseExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        ReleasePlan plan,
        TraceCausalContext? traceContext = null)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        var before = ExternalOperations.Query(operation);
        if (!before.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(before.Error, before.Message!);
        var released = ExternalOperations.Release(operation, plan);
        if (released.IsSuccess && before.Value!.Admission?.CancellationScope is { } scope)
        {
            var temporal = CancellationScopes.Observe(principal, scope);
            if (temporal.IsSuccess && temporal.Value!.CancellationRequested)
            {
                var outcome = before.Value.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted
                    ? CancellationDisposition.CancelledBeforeEffect
                    : before.Value.State == ExternalOperationState.Published
                        ? CancellationDisposition.CompletedBeforeCancellation
                        : CancellationDisposition.ProviderEffectContained;
                _ = CancellationScopes.RecordDisposition(principal, scope, outcome);
            }
        }
        if (released.IsSuccess && _externalOperationBudgetReservations.Remove(operation, out var charge))
            _ = ReleaseAttachedBudget(principal, charge.Reservation);
        if (released.IsSuccess)
            RecordTrace(principal, TraceEventKind.ExternalOperationReleased, traceContext, "external-operation",
                $"{operation.OperationId.Value}:{operation.Generation.Value}", "released", "closed-or-contained");
        return released;
    }

    public KernelResult<ExternalOperationSnapshot> QueryExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        return validation.IsSuccess
            ? ExternalOperations.Query(operation)
            : KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
    }

    private KernelResult ValidateExternalOperationPrincipal(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        bool requireNewEffect)
    {
        var resolved = ResolveExternalOperationPrincipal(principal, requireNewEffect);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var snapshot = ExternalOperations.Query(operation);
        if (!snapshot.IsSuccess) return KernelResult.Fail(snapshot.Error, snapshot.Message!);
        return snapshot.Value!.Principal == resolved.Value!
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.WrongRegionOwner, "External operation belongs to a different principal.");
    }

    private KernelResult<RegionOwner> ResolveExternalOperationPrincipal(
        ProcessHandle principal,
        bool requireNewEffect)
    {
        var resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult<RegionOwner>.Fail(resolved.Error, resolved.Message!);
        if (requireNewEffect)
        {
            var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
            if (!effect.IsSuccess) return KernelResult<RegionOwner>.Fail(effect.Error, effect.Message!);
        }
        return KernelResult<RegionOwner>.Ok(new RegionOwner(resolved.Value!.DomainId, principal.Generation));
    }
}
