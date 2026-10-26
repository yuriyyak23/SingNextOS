using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal interface IComputeSubmissionLegalityGate
{
    KernelResult Validate(ComputePlan plan, ComputeProviderCandidate provider);
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<ResourceAdmissionCommit> PrepareResourceAwareComputeSubmission(
        ProcessHandle principal,
        ComputePlan plan,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        CapabilityId effectCapability,
        ulong effectResourceGeneration,
        CapabilityId resourceGrant,
        ulong resourceGrantGeneration,
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        IComputeSubmissionLegalityGate cpuLegality)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentCandidates);
        ArgumentNullException.ThrowIfNull(cpuLegality);
        if (plan.Intent.ResourceRequirement is not { } envelope)
            return KernelResult<ResourceAdmissionCommit>.Fail(KernelError.InvalidMessage,
                "Resource-aware compute submission requires an explicit semantic resource envelope.");

        KernelResult livePlan = ValidateComputePlanBeforeSubmit(principal, plan, currentCandidates);
        if (!livePlan.IsSuccess)
            return KernelResult<ResourceAdmissionCommit>.Fail(livePlan.Error, livePlan.Message!);
        ComputeProviderCandidate provider = currentCandidates.Single(candidate => candidate.ProviderId == plan.ProviderId);
        KernelResult legality = cpuLegality.Validate(plan, provider);
        if (!legality.IsSuccess)
            return KernelResult<ResourceAdmissionCommit>.Fail(legality.Error, legality.Message!);

        KernelResult<SingProcess> process = Processes.Resolve(principal);
        if (!process.IsSuccess)
            return KernelResult<ResourceAdmissionCommit>.Fail(process.Error, process.Message!);
        KernelResult exactUses = ExternalOperations.ValidatePreparedUses(operation,
            new(process.Value!.DomainId, principal.Generation), plan.RequiredRegionUses);
        if (!exactUses.IsSuccess)
            return KernelResult<ResourceAdmissionCommit>.Fail(exactUses.Error, exactUses.Message!);

        return PrepareResourceExternalAdmission(principal, effectCapability, ResourceKind.Compute,
            "compute:effect", effectResourceGeneration, resourceGrant, resourceGrantGeneration,
            envelope, operation, dependencies, providerIdentity: provider.ProviderId.Value,
            providerGeneration: provider.Generation);
    }
}
