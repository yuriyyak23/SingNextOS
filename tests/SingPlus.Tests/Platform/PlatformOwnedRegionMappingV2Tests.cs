using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformOwnedRegionMappingV2Tests
{
    [Fact]
    public void InvalidRangeOwnerAndGenerationAreRejectedBeforeProviderCall()
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 701, 710);
        var (_, other) = TestFixtures.Create(kernel, 702, 720);
        var region = kernel.AllocateRegion(owner, 128).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        var outOfRange = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 100, 29, PlatformMemoryAccess.Read);
        Assert.Equal(KernelError.PlatformDenied, outOfRange.Error);
        Assert.Equal(0, provider.MapCalls);

        var staleHandle = region.Handle with
        {
            Generation = new RegionGeneration(region.Handle.Generation.Value + 1),
        };
        var stale = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, staleHandle, 0, 64, PlatformMemoryAccess.Read);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, provider.MapCalls);

        var otherBinding = kernel.BindPlatformDomain(other).Value!;
        var wrongOwner = kernel.MapPlatformOwnedRegionSlice(
            other, otherBinding, capability, region.Handle, 0, 64, PlatformMemoryAccess.Read);
        Assert.Equal(KernelError.WrongCapabilitySubject, wrongOwner.Error);
        Assert.Equal(0, provider.MapCalls);
    }

    [Fact]
    public void ExactSliceIsCommittedAndBlocksOwnershipMutationUntilClosed()
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 703, 730);
        var (_, target) = TestFixtures.Create(kernel, 704, 740);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);

        var mapped = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 64, 512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(64, mapped.Value!.Offset);
        Assert.Equal(512, mapped.Value.Length);
        Assert.Equal(64, provider.LastSlice!.Value.Offset);
        Assert.Equal(512, provider.LastSlice.Value.Length);
        Assert.Equal(new RegionOwner(new DomainId(730), owner.Generation),
            provider.LastSlice.Value.Region.Owner);

        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(owner, target, region).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.Regions.Loan(
                region.Handle,
                new RegionOwner(new DomainId(730), owner.Generation),
                new RegionOwner(new DomainId(740), target.Generation)).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.ReleaseRegion(owner, region).Error);

        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value).IsSuccess);
        var moved = kernel.TransferRegion(owner, target, region);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(new RegionGeneration(2), moved.Value!.Handle.Generation);
    }

    [Fact]
    public void NonCoherentMappingRequiresSatisfiedPublicationFence()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(705, 750);

        var coherent = kernel.PreparePlatformRegionMappingForConsumer(
            owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.CoherentAccess);
        var fence = kernel.PreparePlatformRegionMappingForConsumer(
            owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);

        Assert.Equal(KernelError.PlatformUnsupported, coherent.Error);
        Assert.True(fence.IsSuccess, fence.Message);
        Assert.True(fence.Value!.IsSatisfied);
        Assert.Equal(PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            fence.Value.Outcome);
        Assert.Equal(2, provider.VisibilityCalls);
    }

    [Fact]
    public void DrainingMappingRejectsNewVisibilityBeforeProviderCall()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(706, 760);
        provider.CompletionState = PlatformCompletionState.Draining;

        var revoke = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.Equal(KernelError.PlatformBindingDraining, revoke.Error);

        var calls = provider.VisibilityCalls;
        var visibility = kernel.PreparePlatformRegionMappingForConsumer(
            owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        Assert.Equal(KernelError.PlatformBindingDraining, visibility.Error);
        Assert.Equal(calls, provider.VisibilityCalls);

        provider.CompletionState = PlatformCompletionState.Closed;
        Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
    }

    [Fact]
    public void StaleCompletionCannotReleaseReservation()
    {
        var provider = new ExactMappingProvider
        {
            CompletionState = PlatformCompletionState.Closed,
            ReturnStaleCompletionGeneration = true,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 707, 770);
        var (_, target) = TestFixtures.Create(kernel, 708, 780);
        var region = kernel.AllocateRegion(owner, 512).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 0, 64, PlatformMemoryAccess.Read).Value!;

        Assert.Equal(KernelError.StaleGeneration,
            kernel.RevokePlatformRegionMapping(owner, mapping).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(owner, target, region).Error);

        provider.ReturnStaleCompletionGeneration = false;
        Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
        Assert.True(kernel.TransferRegion(owner, target, region).IsSuccess);
    }

    [Fact]
    public void ForgedExactRangeIsRejectedBeforeVisibilityProviderCall()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var forged = mapping with { Offset = mapping.Offset + 1 };

        var result = kernel.PreparePlatformRegionMappingForConsumer(
            owner, forged,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, provider.VisibilityCalls);
    }

    [Fact]
    public void ExactMappingContractsExposeNoHardwareAuthorityIdentifiers()
    {
        var forbidden = new[]
        {
            "Physical", "Pte", "PageTable", "CacheLine", "Dma", "Iommu", "Vmcs", "Lane", "Opcode",
        };
        var surfaceTypes = new[]
        {
            typeof(PlatformRegionSlice),
            typeof(PlatformProviderOwnedRegionMapping),
            typeof(PlatformRegionVisibilityRequest),
            typeof(PlatformRegionVisibilityResult),
            typeof(PlatformOwnedRegionSliceMapping),
            typeof(PlatformRegionVisibilityEvidence),
        };

        foreach (var type in surfaceTypes)
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var signature = property.PropertyType.FullName ?? property.PropertyType.Name;
            foreach (var name in forbidden)
                Assert.DoesNotContain(name, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (RuntimeKernel Kernel, ExactMappingProvider Provider, ProcessHandle Owner,
        PlatformOwnedRegionSliceMapping Mapping) CreateMappedRegion(ulong processId, ulong domainId)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, processId, domainId);
        var region = kernel.AllocateRegion(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 16, 128, PlatformMemoryAccess.Read).Value!;
        return (kernel, provider, owner, mapping);
    }

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var capability = kernel.MintCapability(
            process.Value!.DomainId, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId), rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }

    private sealed class ExactMappingProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionVisibilityProvider,
        IPlatformRegionRevocationProvider
    {
        private PlatformProviderDomainLease? _domain;
        private PlatformProviderRegionMappingLease? _mapping;
        private PlatformRegionSlice? _slice;
        private PlatformOperationIdentity? _operation;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public int MapCalls { get; private set; }
        public int VisibilityCalls { get; private set; }
        public PlatformRegionSlice? LastSlice { get; private set; }
        public PlatformCompletionState CompletionState { get; set; } = PlatformCompletionState.Closed;
        public bool ReturnStaleCompletionGeneration { get; set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("exact-mapping-test"), 4,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, 1,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ExplicitMemoryVisibility,
                PlatformRegionVisibilityContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(1), new PlatformProviderLeaseGeneration(1), subject);
            _domain = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            var exact = MapOwnedRegionSlice(domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return exact.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(exact.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(exact.Status, exact.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease, PlatformRegionSlice slice)
        {
            MapCalls++;
            LastSlice = slice;
            if (_domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain, "Wrong domain lease.");

            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1), domainLease, slice.Region, slice.Access);
            _mapping = lease;
            _slice = slice;
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(
                new PlatformProviderOwnedRegionMapping(lease, slice));
        }

        public PlatformAuthorityResult<PlatformRegionVisibilityResult> PrepareRegionMappingForConsumer(
            PlatformRegionVisibilityRequest request)
        {
            VisibilityCalls++;
            if (_mapping != request.Mapping || _slice != request.Slice)
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong mapping visibility request.");

            var outcome = request.Requirement == PlatformMemoryVisibilityRequirement.PublicationFence
                ? PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied
                : PlatformMemoryVisibilityOutcome.Unsupported;
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                new PlatformRegionVisibilityResult(
                    request.Mapping.MappingId, request.Mapping.Generation, request.Slice,
                    request.Consumer, request.Requirement, outcome));
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            if (_mapping != mapping)
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong mapping.");
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++), new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _operation = operation;
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(mapping.MappingId, mapping.Generation, operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            if (_operation != operation)
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong operation.");
            var generation = ReturnStaleCompletionGeneration
                ? new PlatformOperationGeneration(operation.Generation.Value + 1)
                : operation.Generation;
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(operation.OperationId, generation,
                    operation.DomainLease, CompletionState));
        }
    }
}
