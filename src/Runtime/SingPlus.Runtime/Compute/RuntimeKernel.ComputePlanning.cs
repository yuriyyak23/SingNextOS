using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<ComputeRegionCapabilityReceipt> QueryComputeRegionCapabilities(
        ProcessHandle principal,
        ComputeRegionCapabilityQuery query,
        ComputeProviderCandidate provider)
    {
        var resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult<ComputeRegionCapabilityReceipt>.Fail(resolved.Error, resolved.Message!);
        return ComputePlanner.QueryCapabilities(new RegionOwner(resolved.Value!.DomainId, principal.Generation), query, provider);
    }

    public KernelResult<ComputePlan> PlanCompute(
        ProcessHandle principal,
        ComputeIntent intent,
        ComputeSelectionPolicy policy,
        IReadOnlyList<ComputeProviderCandidate> candidates)
    {
        var resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult<ComputePlan>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<ComputePlan>.Fail(effect.Error, effect.Message!);
        return ComputePlanner.Plan(
            new RegionOwner(resolved.Value!.DomainId, principal.Generation),
            intent,
            policy,
            candidates);
    }

    public KernelResult ValidateComputePlanBeforeSubmit(
        ProcessHandle principal,
        ComputePlan plan,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates)
    {
        var resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return effect;
        return ComputePlanner.ValidateBeforeSubmit(
            plan,
            new RegionOwner(resolved.Value!.DomainId, principal.Generation),
            currentCandidates);
    }
}
