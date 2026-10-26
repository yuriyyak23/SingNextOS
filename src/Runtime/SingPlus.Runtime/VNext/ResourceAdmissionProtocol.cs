using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;

namespace SingPlus.Runtime;

internal enum ResourceAdmissionQualificationPoint
{
    AfterInitialValidation = 0,
    AfterBudgetReservation,
    AfterFinalRevalidation,
    AfterLocalCommit,
    BeforeProviderCallback,
    AfterProviderCallback,
}

internal interface IResourceAdmissionQualificationHook
{
    void At(ResourceAdmissionQualificationPoint point);
}

internal sealed record SipResourceAdmissionBinding(
    CapabilityId EffectCapability,
    ResourceKind EffectResourceKind,
    string EffectResourceId,
    ulong EffectResourceGeneration,
    CapabilityId ResourceGrant,
    ulong ResourceGrantGeneration,
    string SemanticScope,
    ExternalOperationHandle Operation,
    OperationDependencySnapshot Dependencies);

internal sealed class ResourceAdmissionCommit : IDisposable
{
    private readonly RuntimeKernel _kernel;
    private int _submitStarted;
    private int _disposed;

    internal ResourceAdmissionCommit(
        RuntimeKernel kernel, ProcessHandle principal, ProcessHandle budgetOwner,
        CapabilityId effectCapability, ResourceKind effectResourceKind, string effectResourceId,
        ulong effectResourceGeneration, CapabilityId resourceGrant,
        ulong resourceGeneration, ResourceEnvelopeV1 envelope, ExternalOperationHandle operation,
        BudgetReservationHandle lease, ResourceUseAuthorityLease resourceAuthority,
        OperationAuthorityLease effectAuthority,
        PlatformResourceCorrelation providerCorrelation)
    {
        _kernel = kernel;
        Principal = principal;
        BudgetOwner = budgetOwner;
        EffectCapability = effectCapability;
        EffectResourceKind = effectResourceKind;
        EffectResourceId = effectResourceId;
        EffectResourceGeneration = effectResourceGeneration;
        ResourceGrant = resourceGrant;
        ResourceGeneration = resourceGeneration;
        Envelope = envelope;
        Operation = operation;
        Lease = lease;
        ResourceAuthority = resourceAuthority;
        EffectAuthority = effectAuthority;
        ProviderCorrelation = providerCorrelation;
    }

    internal ProcessHandle Principal { get; }
    internal ProcessHandle BudgetOwner { get; }
    internal CapabilityId EffectCapability { get; }
    internal ResourceKind EffectResourceKind { get; }
    internal string EffectResourceId { get; }
    internal ulong EffectResourceGeneration { get; }
    internal CapabilityId ResourceGrant { get; }
    internal ulong ResourceGeneration { get; }
    internal ResourceEnvelopeV1 Envelope { get; }
    internal ExternalOperationHandle Operation { get; }
    internal BudgetReservationHandle Lease { get; }
    internal ResourceUseAuthorityLease ResourceAuthority { get; }
    internal OperationAuthorityLease EffectAuthority { get; }
    internal PlatformResourceCorrelation ProviderCorrelation { get; }
    internal bool TryStartSubmit() => Interlocked.CompareExchange(ref _submitStarted, 1, 0) == 0;
    internal bool TryCloseBeforeSubmit() => Interlocked.CompareExchange(ref _submitStarted, -1, 0) == 0;
    internal bool SubmitStarted => Volatile.Read(ref _submitStarted) > 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Disposal and submit contend on the same admission state: -1 closes
        // an unused commit without inventing a submitted external effect.
        bool unused = TryCloseBeforeSubmit();
        EffectAuthority.Dispose();
        ResourceAuthority.Dispose();
        if (unused)
            _kernel.CompensateResourceAdmissionBeforeSubmit(this);
    }
}

public sealed partial class RuntimeKernel
{
    internal IResourceAdmissionQualificationHook? ResourceAdmissionQualificationHook { private get; set; }

    internal GeneratedSipResourceAdmission EnterSipResourceAdmission(
        ProcessHandle principal,
        SipResourceAdmissionBinding binding,
        SipResourceRequirementV1 requirement)
    {
        if (requirement.Version != SipResourceRequirementV1.CurrentVersion ||
            requirement.ResourceClass != ResourceClassV1.ComputeTime ||
            requirement.Unit != ResourceUnitV1.Nanoseconds ||
            !string.Equals(requirement.SemanticScope, binding.SemanticScope, StringComparison.Ordinal))
            return GeneratedSipResourceAdmission.Failure((int)KernelError.InvalidMessage,
                "Generated SIP resource requirement does not match the bound semantic contour.");

        (EffectiveCapabilityConstraints Constraints, ulong SharedRemaining, ulong HandleConsumed)? constraints = CapabilityAuthority.InspectConstraints(binding.ResourceGrant);
        if (constraints?.Constraints.ResourceUse is not { } resourceUse)
            return GeneratedSipResourceAdmission.Failure((int)KernelError.CapabilityNotFound,
                "The bound live resource-use grant is unavailable.");
        if (resourceUse.AssuranceCeiling > requirement.AssuranceCeiling)
            return GeneratedSipResourceAdmission.Failure((int)KernelError.InsufficientRights,
                "The live resource-use assurance exceeds the generated contract ceiling.");

        ResourceEnvelopeV1 envelope;
        try
        {
            envelope = new ResourceEnvelopeV1(ResourceEnvelopeV1.CurrentVersion, ResourceDimensionFamilyV1.Time,
                ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
                requirement.MaximumAmount, 0, binding.SemanticScope).Canonicalize();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return GeneratedSipResourceAdmission.Failure((int)KernelError.InvalidMessage, exception.Message);
        }

        KernelResult<ResourceAdmissionCommit> admitted = PrepareResourceExternalAdmission(principal,
            binding.EffectCapability, binding.EffectResourceKind, binding.EffectResourceId,
            binding.EffectResourceGeneration, binding.ResourceGrant, binding.ResourceGrantGeneration,
            envelope, binding.Operation, binding.Dependencies);
        return admitted.IsSuccess
            ? GeneratedSipResourceAdmission.Success(admitted.Value!, providerSubmit =>
            {
                KernelResult<OperationBinding> submitted = SubmitResourceExternalAdmission(admitted.Value!, binding.Dependencies, () =>
                {
                    GeneratedSipSubmitResult result = providerSubmit();
                    KernelError providerError = (KernelError)result.ErrorCode;
                    return result.IsSuccess
                        ? KernelResult.Ok()
                        : KernelResult.Fail(Enum.IsDefined(providerError) && providerError != KernelError.None
                                ? providerError
                                : KernelError.PlatformFaulted,
                            result.Message ?? "Provider submission failed.");
                });
                return submitted.IsSuccess
                    ? GeneratedSipSubmitResult.Ok()
                    : GeneratedSipSubmitResult.Failure((int)submitted.Error, submitted.Message!);
            })
            : GeneratedSipResourceAdmission.Failure((int)admitted.Error, admitted.Message!);
    }

    internal KernelResult<ResourceAdmissionCommit> PrepareResourceExternalAdmission(
        ProcessHandle principal,
        CapabilityId effectCapability,
        ResourceKind effectResourceKind,
        string effectResourceId,
        ulong effectResourceGeneration,
        CapabilityId resourceGrant,
        ulong resourceGrantGeneration,
        ResourceEnvelopeV1 envelope,
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        ProcessHandle? budgetOwner = null,
        BudgetReservationHandle? existingLease = null,
        string? providerIdentity = null,
        ulong? providerGeneration = null)
    {
        ProcessHandle exactBudgetOwner = budgetOwner ?? principal;
        BudgetReservationHandle? lease = null;
        ResourceUseAuthorityLease? resourceAuthority = null;
        OperationAuthorityLease? effectAuthority = null;
        bool externalCommitted = false;
        try
        {
            if (_resourceBudgetRecoveryJournal is not null && !Budgets.RecoveryImportComplete)
                return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.Quarantined,
                    "Durable resource recovery state requires accounting import or provider reconciliation before fresh admission.");
            KernelResult<SingProcess> process = Processes.Resolve(principal);
            if (!process.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(process.Error, process.Message!);
            SingProcess processRecord = process.Value!;
            KernelResult accepts = EnsureProcessAcceptsNewEffects(processRecord);
            if (!accepts.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(accepts.Error, accepts.Message!);
            ResourceEnvelopeV1 requested = envelope.Canonicalize();
            KernelResult<IReadOnlyList<BudgetAmount>> budgetAmounts = ResourceEnvelopeBudgetMapping.Map([requested]);
            if (!budgetAmounts.IsSuccess)
                return KernelResult<ResourceAdmissionCommit>.Fail(budgetAmounts.Error, budgetAmounts.Message!);
            KernelResult<ResourceUseConstraintV1> resource = CapabilityAuthority.ValidateResourceUse(resourceGrant, processRecord.DomainId,
                principal.Generation, resourceGrantGeneration, requested);
            if (!resource.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(resource.Error, resource.Message!);
            KernelResult<ExternalOperationSnapshot> operationSnapshot = ExternalOperations.Query(operation);
            if (!operationSnapshot.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(operationSnapshot.Error, operationSnapshot.Message!);
            if (operationSnapshot.Value!.Principal != new RegionOwner(processRecord.DomainId, principal.Generation) ||
                operationSnapshot.Value.State != ExternalOperationState.Prepared)
                return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.StaleGeneration, "External operation preparation is stale or belongs to another principal.");
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterInitialValidation);

            if (existingLease is { } donatedLease)
            {
                KernelResult<BudgetReservationSnapshot> snapshot = Budgets.Query(donatedLease);
                if (!snapshot.IsSuccess || snapshot.Value!.Owner != exactBudgetOwner ||
                    snapshot.Value.State != BudgetReservationState.Reserved ||
                    !snapshot.Value.Amounts.SequenceEqual(budgetAmounts.Value!))
                    return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.StaleGeneration,
                        "Donated budget lease is stale, non-exact, or belongs to another charging lineage.");
                lease = donatedLease;
            }
            else
            {
                KernelResult<BudgetReservationSnapshot> reserved = Budgets.Reserve(principal, budgetAmounts.Value!,
                    BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None);
                if (!reserved.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(reserved.Error, reserved.Message!);
                lease = reserved.Value!.Reservation;
            }
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterBudgetReservation);

            process = Processes.Resolve(principal);
            if (!process.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(process.Error, process.Message!);
            resource = CapabilityAuthority.ValidateResourceUse(resourceGrant, process.Value!.DomainId,
                principal.Generation, resourceGrantGeneration, requested);
            if (!resource.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(resource.Error, resource.Message!);
            operationSnapshot = ExternalOperations.Query(operation);
            if (!operationSnapshot.IsSuccess || operationSnapshot.Value!.State != ExternalOperationState.Prepared)
                return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.StaleGeneration, "External operation changed before commit.");
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterFinalRevalidation);

            KernelResult<ResourceUseAuthorityLease> acquiredResource = CapabilityAuthority.AcquireResourceUseAuthority(resourceGrant,
                process.Value.DomainId, principal.Generation, resourceGrantGeneration, requested,
                revalidateConsumer: () =>
                {
                    KernelResult<SingProcess> current = Processes.Resolve(principal);
                    if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                    KernelResult accepts = EnsureProcessAcceptsNewEffects(current.Value!);
                    if (!accepts.IsSuccess) return accepts;
                    KernelResult<ExternalOperationSnapshot> prepared = ExternalOperations.Query(operation);
                    return prepared.IsSuccess && prepared.Value!.State == ExternalOperationState.Prepared
                        ? KernelResult.Ok()
                        : KernelResult.Fail(KernelError.StaleGeneration, "External operation changed during resource clock collection.");
                });
            if (!acquiredResource.IsSuccess)
                return KernelResult<ResourceAdmissionCommit>.Fail(acquiredResource.Error, acquiredResource.Message!);
            resourceAuthority = acquiredResource.Value!;
            KernelResult<OperationAuthorityLease> acquired = CapabilityAuthority.AcquireOperationAuthority(effectCapability,
                process.Value.DomainId, principal.Generation, effectResourceKind, effectResourceId,
                effectResourceGeneration, CapabilityOperation.Execute);
            if (!acquired.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(acquired.Error, acquired.Message!);
            effectAuthority = acquired.Value!;
            KernelResult liveRecords = RevalidateAdmissionCapabilityRecords(principal, effectCapability,
                effectResourceGeneration, resourceGrant, resourceGrantGeneration);
            if (!liveRecords.IsSuccess)
                return KernelResult<ResourceAdmissionCommit>.Fail(liveRecords.Error, liveRecords.Message!);
            KernelResult<BudgetReservationSnapshot> bound = Budgets.BindLease(exactBudgetOwner, lease.Value);
            if (!bound.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(bound.Error, bound.Message!);
            KernelResult<OperationAdmissionSnapshot> admitted = ExternalOperations.Admit(operation, dependencies);
            if (!admitted.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(admitted.Error, admitted.Message!);
            externalCommitted = true;
            KernelResult<ExternalOperationResourceBinding> resourceBinding = ExternalOperations.BindResourceLease(operation, exactBudgetOwner, lease.Value,
                requested, providerIdentity ?? requested.SemanticScope,
                providerGeneration ?? dependencies.PlatformGeneration);
            if (!resourceBinding.IsSuccess)
                return KernelResult<ResourceAdmissionCommit>.Fail(resourceBinding.Error, resourceBinding.Message!);
            PlatformResourceCorrelation correlation = new PlatformResourceCorrelation(
                new PlatformResourceCorrelationId(operation.OperationId.Value),
                new PlatformResourceCorrelationGeneration(operation.Generation.Value));
            KernelResult journalPrepared = AppendResourceRecovery(lease.Value, exactBudgetOwner, requested, correlation,
                ResourceBudgetRecoveryTransition.Prepared, []);
            if (!journalPrepared.IsSuccess)
                return KernelResult<ResourceAdmissionCommit>.Fail(journalPrepared.Error, journalPrepared.Message!);
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterLocalCommit);

            ResourceAdmissionCommit commit = new ResourceAdmissionCommit(this, principal, exactBudgetOwner,
                effectCapability, effectResourceKind, effectResourceId, effectResourceGeneration,
                resourceGrant, resourceGrantGeneration, requested,
                operation, lease.Value, resourceAuthority, effectAuthority, correlation);
            resourceAuthority = null;
            effectAuthority = null;
            lease = null;
            externalCommitted = false;
            return KernelResult<ResourceAdmissionCommit>.Ok(commit);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.PlatformFaulted, exception.Message);
        }
        finally
        {
            effectAuthority?.Dispose();
            resourceAuthority?.Dispose();
            if (externalCommitted && lease is { } cancelled)
            {
                _ = CompensateResourceAdmissionBeforeSubmit(operation, exactBudgetOwner, cancelled, envelope,
                    new PlatformResourceCorrelation(new PlatformResourceCorrelationId(operation.OperationId.Value),
                        new PlatformResourceCorrelationGeneration(operation.Generation.Value)));
            }
            else if (lease is { } reservation)
                _ = Budgets.CancelLeasePreSubmit(exactBudgetOwner, reservation);
        }
    }

    internal KernelResult<OperationBinding> SubmitResourceExternalAdmission(
        ResourceAdmissionCommit commit,
        OperationDependencySnapshot dependencies,
        Func<KernelResult> providerSubmit,
        Func<KernelResult>? finalSentry = null)
    {
        ArgumentNullException.ThrowIfNull(providerSubmit);
        return SubmitResourceExternalAdmissionWithBinding(commit, dependencies,
            _ => providerSubmit(), finalSentry);
    }

    /// <summary>
    /// Supplies the exact SingNext-owned operation binding after its submit commit.
    /// The binding is correlation evidence, not provider admission or runtime legality.
    /// </summary>
    internal KernelResult<OperationBinding> SubmitResourceExternalAdmissionWithBinding(
        ResourceAdmissionCommit commit,
        OperationDependencySnapshot dependencies,
        Func<OperationBinding, KernelResult> providerSubmit,
        Func<KernelResult>? finalSentry = null,
        Action? submissionObservation = null,
        Action? preSubmitFailureObservation = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(providerSubmit);
        if (commit.SubmitStarted)
            return KernelResult<OperationBinding>.Fail(KernelError.InvalidTransition, "Duplicate provider submission is denied.");
        if (finalSentry is not null)
        {
            KernelResult final;
            try
            {
                final = finalSentry();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                final = KernelResult.Fail(KernelError.PlatformFaulted,
                    $"Final semantic sentry failed closed: {exception.GetType().Name}.");
            }
            if (!final.IsSuccess)
            {
                // A concurrent caller may have won the irreversible submit transition while this
                // caller was revalidating.  Never compensate or refund after any submit winner.
                if (commit.SubmitStarted)
                    return KernelResult<OperationBinding>.Fail(KernelError.InvalidTransition,
                        "Provider submission was already won; no post-submit compensation is permitted.");
                return FailBeforeSubmit(commit, final.Error, final.Message!, observation: preSubmitFailureObservation);
            }
        }
        if (!commit.TryStartSubmit())
            return KernelResult<OperationBinding>.Fail(KernelError.InvalidTransition, "Duplicate provider submission is denied.");

        KernelResult<SingProcess> process = Processes.Resolve(commit.Principal);
        if (!process.IsSuccess) return FailBeforeSubmit(commit, process.Error, process.Message!, ownsSubmit: true, observation: preSubmitFailureObservation);
        KernelResult durableSubmit = AppendResourceRecovery(commit.Lease, commit.BudgetOwner, commit.Envelope,
            commit.ProviderCorrelation, ResourceBudgetRecoveryTransition.PossibleSubmit, []);
        if (!durableSubmit.IsSuccess)
            return FailBeforeSubmit(commit, durableSubmit.Error, durableSubmit.Message!, ownsSubmit: true, observation: preSubmitFailureObservation);
        // Journal store callbacks can change admission owners. Re-read before
        // recording a possible effect or consuming capacity; dispatch still
        // independently revalidates after the owner submit commit.
        KernelResult finalAdmission = RevalidateResourceAdmissionCommit(commit);
        if (!finalAdmission.IsSuccess)
            return FailBeforeSubmit(commit, finalAdmission.Error, finalAdmission.Message!, ownsSubmit: true, observation: preSubmitFailureObservation);
        KernelResult<OperationBinding> submitted = ExternalOperations.RecordSubmission(commit.Operation, dependencies);
        if (!submitted.IsSuccess) return FailBeforeSubmit(commit, submitted.Error, submitted.Message!, ownsSubmit: true, observation: preSubmitFailureObservation);
        try { submissionObservation?.Invoke(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Observation cannot veto or compensate an authoritative submission.
        }
        KernelResult<BudgetReservationSnapshot> consuming = Budgets.BeginConsumption(commit.BudgetOwner, commit.Lease);
        if (!consuming.IsSuccess)
        {
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            _ = AppendResourceRecovery(commit.Lease, commit.BudgetOwner, commit.Envelope,
                commit.ProviderCorrelation, ResourceBudgetRecoveryTransition.Quarantined, []);
            return KernelResult<OperationBinding>.Fail(consuming.Error, consuming.Message!);
        }
        KernelResult<ExternalOperationResourceBinding> resourceConsuming = ExternalOperations.MarkResourceConsumption(commit.Operation);
        if (!resourceConsuming.IsSuccess)
        {
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            _ = AppendResourceRecovery(commit.Lease, commit.BudgetOwner, commit.Envelope,
                commit.ProviderCorrelation, ResourceBudgetRecoveryTransition.Quarantined, []);
            return KernelResult<OperationBinding>.Fail(resourceConsuming.Error, resourceConsuming.Message!);
        }

        try
        {
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.BeforeProviderCallback);
            KernelResult authorization = RevalidateResourceDispatch(commit, submitted.Value!);
            KernelResult provider = !authorization.IsSuccess ? authorization : providerSubmit(submitted.Value!);
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterProviderCallback);
            if (provider.IsSuccess) return submitted;
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            _ = AppendResourceRecovery(commit.Lease, commit.BudgetOwner, commit.Envelope,
                commit.ProviderCorrelation, ResourceBudgetRecoveryTransition.Quarantined, []);
            return KernelResult<OperationBinding>.Fail(provider.Error, provider.Message!);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            _ = AppendResourceRecovery(commit.Lease, commit.BudgetOwner, commit.Envelope,
                commit.ProviderCorrelation, ResourceBudgetRecoveryTransition.Quarantined, []);
            return KernelResult<OperationBinding>.Fail(KernelError.PlatformFaulted, exception.Message);
        }
        finally
        {
            commit.EffectAuthority.Dispose();
            commit.ResourceAuthority.Dispose();
        }
    }

    internal KernelResult CompensateResourceAdmissionBeforeSubmit(ResourceAdmissionCommit commit)
        => CompensateResourceAdmissionBeforeSubmit(commit.Operation, commit.BudgetOwner, commit.Lease,
            commit.Envelope, commit.ProviderCorrelation);

    private KernelResult RevalidateResourceDispatch(ResourceAdmissionCommit commit, OperationBinding binding)
    {
        KernelResult authorization = RevalidateResourceAdmissionCommit(commit, afterSubmit: true);
        if (!authorization.IsSuccess) return authorization;
        KernelResult<ExternalOperationSnapshot> liveOwner = ExternalOperations.Query(commit.Operation);
        KernelResult<BudgetReservationSnapshot> liveBudget = Budgets.Query(commit.Lease);
        KernelResult<ExternalOperationResourceBinding> liveResource = ExternalOperations.QueryResourceBinding(commit.Operation);
        return liveOwner.IsSuccess && liveOwner.Value!.State == ExternalOperationState.Submitted &&
            liveOwner.Value.Disposition == ExternalOperationDisposition.Active && liveOwner.Value.Binding == binding &&
            liveBudget.IsSuccess && liveBudget.Value!.Owner == commit.BudgetOwner &&
            liveBudget.Value.State == BudgetReservationState.Consuming &&
            liveResource.IsSuccess && liveResource.Value!.Lease == commit.Lease &&
            liveResource.Value.State == ExternalResourceBindingState.Consuming
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.StaleGeneration,
                    "Exact resource owners changed before provider callback; reservations remain pinned.");
    }

    private KernelResult CompensateResourceAdmissionBeforeSubmit(ExternalOperationHandle operation,
        ProcessHandle budgetOwner, BudgetReservationHandle lease, ResourceEnvelopeV1 envelope,
        PlatformResourceCorrelation correlation)
    {
        KernelResult<ExternalOperationSnapshot> cancelled = ExternalOperations.Cancel(operation, providerCancellationSupported: false);
        if (!cancelled.IsSuccess ||
            (cancelled.Value!.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) &&
             !(cancelled.Value.State == ExternalOperationState.Released && cancelled.Value.Binding is null)) ||
            cancelled.Value.Disposition != ExternalOperationDisposition.Cancelled)
            return QuarantineCompensation();
        KernelResult<BudgetReservationSnapshot> budget = Budgets.CancelLeasePreSubmit(budgetOwner, lease);
        if (!budget.IsSuccess) return QuarantineCompensation();
        KernelResult<ExternalOperationResourceBinding> binding = ExternalOperations.MarkResourceCancelledPreSubmit(operation);
        if (!binding.IsSuccess) return QuarantineCompensation();
        return AppendResourceRecovery(lease, budgetOwner, envelope,
            correlation, ResourceBudgetRecoveryTransition.CancelledPreSubmit, []);

        KernelResult QuarantineCompensation()
        {
            _ = Budgets.QuarantineLease(budgetOwner, lease);
            _ = ExternalOperations.MarkResourceQuarantined(operation);
            _ = AppendResourceRecovery(lease, budgetOwner, envelope,
                correlation, ResourceBudgetRecoveryTransition.Quarantined, []);
            return KernelResult.Fail(KernelError.Quarantined,
                "Pre-submit compensation was not confirmed by the authoritative owners.");
        }
    }

    private KernelResult<OperationBinding> FailBeforeSubmit(ResourceAdmissionCommit commit, KernelError error, string message,
        bool ownsSubmit = false, Action? observation = null)
    {
        if (!ownsSubmit && !commit.TryCloseBeforeSubmit())
            return KernelResult<OperationBinding>.Fail(KernelError.InvalidTransition,
                "Admission was already closed or submit was won; no losing compensation is permitted.");
        KernelResult compensation = CompensateResourceAdmissionBeforeSubmit(commit);
        commit.EffectAuthority.Dispose();
        commit.ResourceAuthority.Dispose();
        try { observation?.Invoke(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Only the submit/close winner observes committed compensation; observation
            // cannot change its result or steal another caller's submission trace.
        }
        return compensation.IsSuccess
            ? KernelResult<OperationBinding>.Fail(error, message)
            : KernelResult<OperationBinding>.Fail(compensation.Error, compensation.Message!);
    }

    private KernelResult AppendResourceRecovery(
        BudgetReservationHandle lease,
        ProcessHandle owner,
        ResourceEnvelopeV1 envelope,
        PlatformResourceCorrelation correlation,
        ResourceBudgetRecoveryTransition transition,
        IReadOnlyList<BudgetAmount> charged)
    {
        if (_resourceBudgetRecoveryJournal is null) return KernelResult.Ok();
        try
        {
            if (transition is ResourceBudgetRecoveryTransition.SettledExact or ResourceBudgetRecoveryTransition.CancelledPreSubmit)
            {
                ResourceBudgetRecoveryItem? prior = _resourceBudgetRecoveryJournal.Replay().Items
                    .SingleOrDefault(item => item.LastPayload.Lease == lease);
                if (transition == ResourceBudgetRecoveryTransition.CancelledPreSubmit &&
                    prior?.LastPayload.Transition is ResourceBudgetRecoveryTransition.PossibleSubmit or
                        ResourceBudgetRecoveryTransition.SettledContained)
                {
                    // ADR-011: a journal intent is not an owner submit. Project
                    // zero-charge recovery only from exact committed no-effect
                    // owner facts; raw journal cancellation stays prohibited.
                    ExternalOperationHandle operation = new ExternalOperationHandle(new(correlation.CorrelationId.Value),
                        new(correlation.Generation.Value));
                    KernelResult<ExternalOperationSnapshot> effect = ExternalOperations.Query(operation);
                    KernelResult<ExternalOperationResourceBinding> resource = ExternalOperations.QueryResourceBinding(operation);
                    KernelResult<BudgetReservationSnapshot> budget = Budgets.Query(lease);
                    if (charged.Count != 0 || !effect.IsSuccess || !resource.IsSuccess || !budget.IsSuccess ||
                        effect.Value!.Binding is not null || effect.Value.Disposition != ExternalOperationDisposition.Cancelled ||
                        effect.Value.EffectBoundary != ExternalEffectBoundaryState.NotCrossed ||
                        effect.Value.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted or ExternalOperationState.Released) ||
                        resource.Value!.Operation != operation || resource.Value.Lease != lease ||
                        resource.Value.BudgetOwner != owner || resource.Value.Envelope != envelope ||
                        resource.Value.State != ExternalResourceBindingState.CancelledPreSubmit ||
                        budget.Value!.Owner != owner || budget.Value.Lifetime != BudgetReservationLifetime.ExternalEffect ||
                        budget.Value.State != BudgetReservationState.CancelledPreSubmit ||
                        !budget.Value.Amounts.SequenceEqual([new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, envelope.Amount)]) ||
                        budget.Value.ChargedAmounts?.Count != 0)
                        return KernelResult.Fail(KernelError.Quarantined,
                            "Exact original no-effect owners did not confirm zero-charge recovery.");
                    transition = ResourceBudgetRecoveryTransition.SettledContained;
                }
                if (prior?.LastPayload.Transition == transition)
                {
                    ResourceBudgetRecoveryPayload payload = prior.LastPayload;
                    return payload.Owner == owner &&
                           payload.ProviderCorrelation == correlation &&
                           payload.ReservedAmounts.SequenceEqual(
                               [new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, envelope.Amount)]) &&
                           payload.ChargedAmounts.SequenceEqual(charged)
                        ? KernelResult.Ok()
                        : KernelResult.Fail(KernelError.Quarantined,
                            "The durable terminal resource receipt conflicts with the exact owner transition.");
                }
            }
            _resourceBudgetRecoveryJournal.Append(new ResourceBudgetRecoveryPayload(
                lease, owner,
                [new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, envelope.Amount)],
                correlation, transition, charged));
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // A store callback can fail before or after a frame is committed.
            // Report failure; existing consumers must confirm their own exact
            // cleanup/charge facts rather than infer no effect from this exception.
            return KernelResult.Fail(KernelError.Quarantined,
                $"Durable resource recovery journal failed closed: {exception.Message}");
        }
    }
}
