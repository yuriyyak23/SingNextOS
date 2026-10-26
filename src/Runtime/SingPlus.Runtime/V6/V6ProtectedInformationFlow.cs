using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult<ProtectedRegionLabelDescriptorV1> AttachV6ProtectedRegionLabel(
        ProcessHandle principal, RegionHandle region, DataLabelV1 label)
    {
        KernelResult<SingProcess> process = Processes.Resolve(principal);
        return process.IsSuccess
            ? Regions.AttachProtectedLabel(region, new(process.Value!.DomainId, principal.Generation), label)
            : KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(process.Error, process.Message!);
    }

    internal KernelResult<ProtectedRegionLabelDescriptorV1> PropagateV6ProtectedRegionLabel(
        ProcessHandle principal, IReadOnlyList<RegionHandle> inputs, RegionHandle output)
    {
        KernelResult<SingProcess> process = Processes.Resolve(principal);
        return process.IsSuccess
            ? Regions.PropagateProtectedLabel(inputs, output, new(process.Value!.DomainId, principal.Generation))
            : KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(process.Error, process.Message!);
    }

    internal KernelResult<ProtectedRegionLabelDescriptorV1> TransitionV6ProtectedRegionLabel(
        ProcessHandle principal, ProtectedRegionLabelDescriptorV1 expected,
        InformationFlowTransitionV1 transition, CapabilityId capability)
    {
        KernelResult<SingProcess> process = Processes.Resolve(principal);
        if (!process.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(process.Error, process.Message!);
        string resource = CapabilityResourceIds.InformationFlowTransition(expected.Region.RegionId);
        KernelResult<OperationAuthorityLease> authority = CapabilityAuthority.AcquireOperationAuthority(capability,
            process.Value!.DomainId, principal.Generation, ResourceKind.MemoryRegion, resource,
            expected.Region.Generation.Value, CapabilityOperation.Configure);
        if (!authority.IsSuccess)
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(authority.Error, authority.Message!);
        using OperationAuthorityLease lease = authority.Value!;
        return Regions.ApplyProtectedLabelTransition(expected,
            new(process.Value.DomainId, principal.Generation), transition);
    }
}
