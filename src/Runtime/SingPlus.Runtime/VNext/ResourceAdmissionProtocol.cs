using SingPlus.Contracts;
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
        RuntimeKernel kernel, ProcessHandle principal, ProcessHandle budgetOwner, CapabilityId resourceGrant,
        ulong resourceGeneration, ResourceEnvelopeV1 envelope, ExternalOperationHandle operation,
        BudgetReservationHandle lease, ResourceUseAuthorityLease resourceAuthority,
        OperationAuthorityLease effectAuthority)
    {
        _kernel = kernel;
        Principal = principal;
        BudgetOwner = budgetOwner;
        ResourceGrant = resourceGrant;
        ResourceGeneration = resourceGeneration;
        Envelope = envelope;
        Operation = operation;
        Lease = lease;
        ResourceAuthority = resourceAuthority;
        EffectAuthority = effectAuthority;
    }

    internal ProcessHandle Principal { get; }
    internal ProcessHandle BudgetOwner { get; }
    internal CapabilityId ResourceGrant { get; }
    internal ulong ResourceGeneration { get; }
    internal ResourceEnvelopeV1 Envelope { get; }
    internal ExternalOperationHandle Operation { get; }
    internal BudgetReservationHandle Lease { get; }
    internal ResourceUseAuthorityLease ResourceAuthority { get; }
    internal OperationAuthorityLease EffectAuthority { get; }
    internal bool TryStartSubmit() => Interlocked.CompareExchange(ref _submitStarted, 1, 0) == 0;
    internal bool SubmitStarted => Volatile.Read(ref _submitStarted) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        EffectAuthority.Dispose();
        ResourceAuthority.Dispose();
        if (!SubmitStarted)
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

        var constraints = CapabilityAuthority.InspectConstraints(binding.ResourceGrant);
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

        var admitted = PrepareResourceExternalAdmission(principal,
            binding.EffectCapability, binding.EffectResourceKind, binding.EffectResourceId,
            binding.EffectResourceGeneration, binding.ResourceGrant, binding.ResourceGrantGeneration,
            envelope, binding.Operation, binding.Dependencies);
        return admitted.IsSuccess
            ? GeneratedSipResourceAdmission.Success(admitted.Value!, providerSubmit =>
            {
                var submitted = SubmitResourceExternalAdmission(admitted.Value!, binding.Dependencies, () =>
                {
                    var result = providerSubmit();
                    var providerError = (KernelError)result.ErrorCode;
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
        var exactBudgetOwner = budgetOwner ?? principal;
        BudgetReservationHandle? lease = null;
        ResourceUseAuthorityLease? resourceAuthority = null;
        OperationAuthorityLease? effectAuthority = null;
        var externalCommitted = false;
        try
        {
            var process = Processes.Resolve(principal);
            if (!process.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(process.Error, process.Message!);
            var processRecord = process.Value!;
            var accepts = EnsureProcessAcceptsNewEffects(processRecord);
            if (!accepts.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(accepts.Error, accepts.Message!);
            var requested = envelope.Canonicalize();
            if (requested.ResourceClass != ResourceClassV1.ComputeTime || requested.Unit != ResourceUnitV1.Nanoseconds)
                return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.InvalidMessage, "P04 supports only ComputeTime nanoseconds.");
            var resource = CapabilityAuthority.ValidateResourceUse(resourceGrant, processRecord.DomainId,
                principal.Generation, resourceGrantGeneration, requested);
            if (!resource.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(resource.Error, resource.Message!);
            var operationSnapshot = ExternalOperations.Query(operation);
            if (!operationSnapshot.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(operationSnapshot.Error, operationSnapshot.Message!);
            if (operationSnapshot.Value!.Principal != new RegionOwner(processRecord.DomainId, principal.Generation) ||
                operationSnapshot.Value.State != ExternalOperationState.Prepared)
                return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.StaleGeneration, "External operation preparation is stale or belongs to another principal.");
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterInitialValidation);

            if (existingLease is { } donatedLease)
            {
                var snapshot = Budgets.Query(donatedLease);
                if (!snapshot.IsSuccess || snapshot.Value!.Owner != exactBudgetOwner ||
                    snapshot.Value.State != BudgetReservationState.Reserved ||
                    snapshot.Value.Amounts.Count != 1 ||
                    snapshot.Value.Amounts[0] != new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, requested.Amount))
                    return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.StaleGeneration,
                        "Donated budget lease is stale, non-exact, or belongs to another charging lineage.");
                lease = donatedLease;
            }
            else
            {
                var reserved = Budgets.Reserve(principal,
                    [new(ServiceBudgetDimension.ComputeTimeNanoseconds, requested.Amount)],
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

            var acquiredResource = CapabilityAuthority.AcquireResourceUseAuthority(resourceGrant,
                process.Value.DomainId, principal.Generation, resourceGrantGeneration, requested);
            if (!acquiredResource.IsSuccess)
                return KernelResult<ResourceAdmissionCommit>.Fail(acquiredResource.Error, acquiredResource.Message!);
            resourceAuthority = acquiredResource.Value!;
            var acquired = CapabilityAuthority.AcquireOperationAuthority(effectCapability,
                process.Value.DomainId, principal.Generation, effectResourceKind, effectResourceId,
                effectResourceGeneration, CapabilityOperation.Execute);
            if (!acquired.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(acquired.Error, acquired.Message!);
            effectAuthority = acquired.Value!;
            var bound = Budgets.BindLease(exactBudgetOwner, lease.Value);
            if (!bound.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(bound.Error, bound.Message!);
            var admitted = ExternalOperations.Admit(operation, dependencies);
            if (!admitted.IsSuccess) return KernelResult<ResourceAdmissionCommit>.Fail(admitted.Error, admitted.Message!);
            externalCommitted = true;
            var resourceBinding = ExternalOperations.BindResourceLease(operation, exactBudgetOwner, lease.Value,
                requested, providerIdentity ?? requested.SemanticScope,
                providerGeneration ?? dependencies.PlatformGeneration);
            if (!resourceBinding.IsSuccess)
                return KernelResult<ResourceAdmissionCommit>.Fail(resourceBinding.Error, resourceBinding.Message!);
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterLocalCommit);

            var commit = new ResourceAdmissionCommit(this, principal, exactBudgetOwner, resourceGrant, resourceGrantGeneration, requested,
                operation, lease.Value, resourceAuthority, effectAuthority);
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
            if (externalCommitted)
            {
                _ = ExternalOperations.Cancel(operation, providerCancellationSupported: false);
                _ = ExternalOperations.MarkResourceCancelledPreSubmit(operation);
            }
            if (lease is { } reservation)
                _ = Budgets.CancelLeasePreSubmit(exactBudgetOwner, reservation);
        }
    }

    internal KernelResult<OperationBinding> SubmitResourceExternalAdmission(
        ResourceAdmissionCommit commit,
        OperationDependencySnapshot dependencies,
        Func<KernelResult> providerSubmit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(providerSubmit);
        if (!commit.TryStartSubmit())
            return KernelResult<OperationBinding>.Fail(KernelError.InvalidTransition, "Duplicate provider submission is denied.");

        var process = Processes.Resolve(commit.Principal);
        if (!process.IsSuccess) return FailBeforeSubmit(commit, process.Error, process.Message!);
        var submitted = ExternalOperations.RecordSubmission(commit.Operation, dependencies);
        if (!submitted.IsSuccess) return FailBeforeSubmit(commit, submitted.Error, submitted.Message!);
        var consuming = Budgets.BeginConsumption(commit.BudgetOwner, commit.Lease);
        if (!consuming.IsSuccess)
        {
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            return KernelResult<OperationBinding>.Fail(consuming.Error, consuming.Message!);
        }
        var resourceConsuming = ExternalOperations.MarkResourceConsumption(commit.Operation);
        if (!resourceConsuming.IsSuccess)
        {
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            return KernelResult<OperationBinding>.Fail(resourceConsuming.Error, resourceConsuming.Message!);
        }

        try
        {
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.BeforeProviderCallback);
            var provider = providerSubmit();
            ResourceAdmissionQualificationHook?.At(ResourceAdmissionQualificationPoint.AfterProviderCallback);
            if (provider.IsSuccess) return submitted;
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            return KernelResult<OperationBinding>.Fail(provider.Error, provider.Message!);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _ = ExternalOperations.RecordProviderLoss(commit.Operation);
            _ = Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease);
            _ = ExternalOperations.MarkResourceQuarantined(commit.Operation);
            return KernelResult<OperationBinding>.Fail(KernelError.PlatformFaulted, exception.Message);
        }
        finally
        {
            commit.EffectAuthority.Dispose();
            commit.ResourceAuthority.Dispose();
        }
    }

    internal void CompensateResourceAdmissionBeforeSubmit(ResourceAdmissionCommit commit)
    {
        _ = ExternalOperations.Cancel(commit.Operation, providerCancellationSupported: false);
        _ = Budgets.CancelLeasePreSubmit(commit.BudgetOwner, commit.Lease);
        _ = ExternalOperations.MarkResourceCancelledPreSubmit(commit.Operation);
    }

    private KernelResult<OperationBinding> FailBeforeSubmit(ResourceAdmissionCommit commit, KernelError error, string message)
    {
        CompensateResourceAdmissionBeforeSubmit(commit);
        commit.EffectAuthority.Dispose();
        commit.ResourceAuthority.Dispose();
        return KernelResult<OperationBinding>.Fail(error, message);
    }
}
