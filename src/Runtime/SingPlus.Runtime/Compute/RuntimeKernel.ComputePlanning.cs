using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<ComputeRegionCapabilityReceipt> QueryComputeRegionCapabilities(
        ProcessHandle principal,
        ComputeRegionCapabilityQuery query,
        ComputeProviderCandidate provider)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult<ComputeRegionCapabilityReceipt>.Fail(resolved.Error, resolved.Message!);
        return ComputePlanner.QueryCapabilities(new RegionOwner(resolved.Value!.DomainId, principal.Generation), query, provider);
    }

    public KernelResult<ComputePlan> PlanCompute(
        ProcessHandle principal,
        ComputeIntent intent,
        ComputeSelectionPolicy policy,
        IReadOnlyList<ComputeProviderCandidate> candidates)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult<ComputePlan>.Fail(resolved.Error, resolved.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
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
        KernelResult<SingProcess> resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return effect;
        return ComputePlanner.ValidateBeforeSubmit(
            plan,
            new RegionOwner(resolved.Value!.DomainId, principal.Generation),
            currentCandidates);
    }

    internal KernelResult<ComputePlan> PlanVirtualizedCompute(ProcessHandle principal, ComputeIntent intent,
        ComputeSelectionPolicy policy, IReadOnlyList<ComputeProviderCandidate> candidates,
        VirtualComputeContext context)
    {
        KernelResult exact = RevalidateVirtualComputeContext(principal, context, null);
        if (!exact.IsSuccess) return KernelResult<ComputePlan>.Fail(exact.Error, exact.Message!);
        KernelResult<SingProcess> resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult<ComputePlan>.Fail(resolved.Error, resolved.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<ComputePlan>.Fail(effect.Error, effect.Message!);
        return ComputePlanner.PlanForExactPlatformMappings(new(resolved.Value!.DomainId, principal.Generation),
            intent, policy, candidates);
    }

    internal KernelResult ValidateVirtualizedComputePlanBeforeSubmit(ProcessHandle principal, ComputePlan plan,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates, VirtualComputeContext context)
    {
        KernelResult exact = RevalidateVirtualComputeContext(principal, context, plan);
        if (!exact.IsSuccess) return exact;
        KernelResult<SingProcess> resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return effect;
        return ComputePlanner.ValidateBeforeSubmitForExactPlatformMappings(plan,
            new(resolved.Value!.DomainId, principal.Generation), currentCandidates);
    }
}
