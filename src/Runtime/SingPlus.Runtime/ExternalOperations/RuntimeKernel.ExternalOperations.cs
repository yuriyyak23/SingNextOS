using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<OperationPreparation> PrepareExternalOperation(
        ProcessHandle principal,
        IReadOnlyList<OperationRegionUseRequest> regionUses,
        ExternalVisibilityRequirement visibilityRequirement,
        ExternalPublicationPolicy publicationPolicy,
        ExternalEffectPolicy? effectPolicy = null)
    {
        var resolved = ResolveExternalOperationPrincipal(principal, requireNewEffect: true);
        if (!resolved.IsSuccess) return KernelResult<OperationPreparation>.Fail(resolved.Error, resolved.Message!);
        return ExternalOperations.Prepare(resolved.Value!, regionUses, visibilityRequirement, publicationPolicy, effectPolicy);
    }

    public KernelResult<OperationAdmissionSnapshot> AdmitExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity = default,
        ExternalCancellationSupport cancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
        if (!validation.IsSuccess) return KernelResult<OperationAdmissionSnapshot>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.Admit(operation, dependencies, serviceIdentity, cancellationSupport);
    }

    public KernelResult<OperationBinding> RecordExternalOperationSubmission(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: true);
        if (!validation.IsSuccess) return KernelResult<OperationBinding>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.RecordSubmission(operation, currentDependencies);
    }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationCompletion(
        ProcessHandle principal,
        OperationCompletion completion)
    {
        var validation = ValidateExternalOperationPrincipal(principal, completion.Binding.Operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.RecordCompletion(completion);
    }

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationVisibility(
        ProcessHandle principal,
        OperationVisibilityEvidence evidence)
    {
        var validation = ValidateExternalOperationPrincipal(principal, evidence.Binding.Operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.RecordVisibility(evidence);
    }

    public KernelResult<ExternalOperationSnapshot> PublishExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        PublicationPlan plan,
        Action publicationAction)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.Publish(operation, currentDependencies, plan, publicationAction);
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

    public KernelResult<ExternalOperationSnapshot> RecordExternalOperationProviderLoss(
        ProcessHandle principal,
        ExternalOperationHandle operation)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.RecordProviderLoss(operation);
    }

    public KernelResult<ExternalOperationSnapshot> ReleaseExternalOperation(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        ReleasePlan plan)
    {
        var validation = ValidateExternalOperationPrincipal(principal, operation, requireNewEffect: false);
        if (!validation.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(validation.Error, validation.Message!);
        return ExternalOperations.Release(operation, plan);
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
