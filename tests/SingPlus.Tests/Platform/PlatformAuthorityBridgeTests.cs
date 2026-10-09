using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformAuthorityBridgeTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public void MalformedRootCleanupCannotForgetOriginalSubject(int mutation, int cleanup)
    {
        var provider = new MalformedRootProvider(mutation, cleanup);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 1348, 1780);
        var subject = new PlatformDomainIdentity(process.DomainId, owner);
        var pinObserved = false;
        provider.BeforeCleanup = () => pinObserved = kernel.PlatformAuthority.HasUnresolvedDomainBindEffect(subject);
        Assert.Equal(KernelError.PlatformFaulted, kernel.BindPlatformDomain(owner).Error);
        Assert.True(pinObserved);
        Assert.True(kernel.PlatformAuthority.HasUnresolvedDomainBindEffect(subject));
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
        Assert.Equal(1, provider.BindCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, provider.CleanupCalls);
    }

    private sealed class MalformedRootProvider(int mutation, int cleanup) : IPlatformAuthorityProvider, IPlatformFeatureProvider
    {
        private readonly HostPlatformAuthorityProvider host = new();
        public int BindCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public Action? BeforeCleanup { get; set; }
        public PlatformProviderDescriptor Descriptor => host.Descriptor;
        public PlatformFeatureManifest QueryFeatures() => host.QueryFeatures();
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            BindCalls++;
            var receipt = host.BindDomain(subject).Value!;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(mutation switch
            {
                0 => receipt with { Subject = subject with { Process = new(subject.ProcessId, subject.ProcessGeneration + 1) } },
                1 => receipt with { LeaseId = new(0) },
                _ => receipt with { Generation = new(0) }
            });
        }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            CleanupCalls++;
            BeforeCleanup?.Invoke();
            if (cleanup == 2) throw new IOException("malformed root cleanup reply lost");
            return cleanup == 0 ? PlatformAuthorityResult.Ok() : PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "cleanup refused");
        }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) => host.MapOwnedRegion(d, r, a);
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => host.RevokeRegionMapping(m, p);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MalformedMappingCleanupSuccessDoesNotReleaseRequestedReservation(int mutation)
    {
        var provider = new MalformedMappingProvider(mutation);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1346, 1760);
        var (_, target) = TestFixtures.Create(kernel, 1347, 1770);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var domain = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        Assert.Equal(KernelError.PlatformFaulted, kernel.MapPlatformOwnedRegion(owner, domain,
            capability, region.Handle, PlatformMemoryAccess.Read).Error);
        Assert.Equal(1, provider.CleanupCalls);
        Assert.False(kernel.TransferRegion(owner, target, region).IsSuccess);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, provider.CleanupCalls);
    }

    private sealed class MalformedMappingProvider(int mutation) : IPlatformAuthorityProvider, IPlatformFeatureProvider
    {
        private readonly HostPlatformAuthorityProvider host = new();
        public int CleanupCalls { get; private set; }
        public PlatformProviderDescriptor Descriptor => host.Descriptor;
        public PlatformFeatureManifest QueryFeatures() => host.QueryFeatures();
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) => host.BindDomain(subject);
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => host.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domain, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            var result = host.MapOwnedRegion(domain, region, access);
            if (!result.IsSuccess) return result;
            var receipt = result.Value!;
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mutation switch
            {
                0 => receipt with { DomainLease = domain with { Generation = new(99) } },
                1 => receipt with { Region = region with { ByteLength = region.ByteLength + 1 } },
                _ => receipt with { Access = PlatformMemoryAccess.Write }
            });
        }
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            CleanupCalls++;
            return PlatformAuthorityResult.Ok(); // Returned object closure is not original-effect evidence.
        }
    }
    [Fact]
    [Trait("Category", "Runtime")]
    public void DefaultKernelFailsClosedWhenPlatformProviderIsUnavailable()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);

        var binding = kernel.BindPlatformDomain(owner);

        Assert.False(binding.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, binding.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void HostProviderIsDeterministicForEquivalentFreshInstances()
    {
        var left = new HostPlatformAuthorityProvider();
        var right = new HostPlatformAuthorityProvider();
        var subject = new PlatformDomainIdentity(
            new DomainId(10),
            new ProcessHandle(new ProcessId(11), 1));

        var leftLease = left.BindDomain(subject);
        var rightLease = right.BindDomain(subject);

        Assert.Equal(left.Descriptor, right.Descriptor);
        Assert.True(leftLease.IsSuccess, leftLease.Message);
        Assert.True(rightLease.IsSuccess, rightLease.Message);
        Assert.Equal(leftLease.Value, rightLease.Value);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ProviderContractDoesNotAcceptLocalCapabilityIds()
    {
        var parameterTypes = typeof(IPlatformAuthorityProvider)
            .GetMethods()
            .SelectMany(static method => method.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(CapabilityId), parameterTypes);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void MapOwnedRegionRequiresLocalCapabilityBeforeProviderCall()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;

        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            new CapabilityId(999),
            region.Handle,
            PlatformMemoryAccess.Read);

        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.CapabilityNotFound, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single().State);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CapabilityForAnotherRegionCannotAuthorizePlatformMapping()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var first = kernel.AllocateRegion(owner, 123).Value!;
        var second = kernel.AllocateRegion(owner, 456).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            first.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            second.Handle,
            PlatformMemoryAccess.Read);

        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.WrongCapabilityResource, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void StalePlatformBindingGenerationIsRejectedBeforeProviderCall()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var stale = binding with
        {
            Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1)
        };

        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            stale,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);

        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void RevokedPlatformBindingCannotBeReused()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        var revoke = kernel.RevokePlatformDomain(owner, binding);
        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void PlatformBindingCannotCrossDomainBoundary()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, left) = TestFixtures.Create(kernel, 1, 10);
        var (_, right) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(right, 123).Value!;
        var binding = kernel.BindPlatformDomain(left).Value!;
        var capability = MintRegionCapability(
            kernel,
            right,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        var mapping = kernel.MapPlatformOwnedRegion(
            right,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);

        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.WrongPlatformDomain, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void UnsupportedExternalMappingDoesNotChangeLocalOwnership()
    {
        var provider = new HostPlatformAuthorityProvider(
            PlatformAuthorityFeatures.NeutralDomainBinding);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);

        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);

        var transfer = kernel.TransferRegion(owner, target, region);
        Assert.True(transfer.IsSuccess, transfer.Message);
        Assert.Equal(new RegionGeneration(2), transfer.Value!.Handle.Generation);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ActiveMappingBlocksOwnershipMutationButTerminationOrchestratesClosure()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (ownerProcess, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        _ = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        var transferWhileMapped = kernel.TransferRegion(owner, target, region);
        var loanWhileMapped = kernel.Regions.Loan(
            region.Handle,
            new RegionOwner(new DomainId(10), owner.Generation),
            new RegionOwner(new DomainId(20), target.Generation));
        var releaseWhileMapped = kernel.ReleaseRegion(owner, region);
        var terminate = kernel.TerminateProcess(owner);

        Assert.False(transferWhileMapped.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transferWhileMapped.Error);
        Assert.False(loanWhileMapped.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, loanWhileMapped.Error);
        Assert.False(releaseWhileMapped.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, releaseWhileMapped.Error);
        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(ProcessState.Exited, ownerProcess.State);
        Assert.Equal(1, provider.RevokeRegionMappingCallCount);
        Assert.Equal(1, provider.RevokeDomainCallCount);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(owner).Error);
        Assert.Equal(
            RegionState.Released,
            kernel.Regions.Snapshot().Single(r => r.Handle.RegionId == region.Handle.RegionId).State);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void RevokedMappingHandleIsRejected()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Write);
        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Write).Value!;

        var first = kernel.RevokePlatformRegionMapping(owner, mapping);
        var second = kernel.RevokePlatformRegionMapping(owner, mapping);

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, second.Error);
        Assert.Equal(1, provider.RevokeRegionMappingCallCount);
        Assert.Equal(
            PlatformRegionRevocationPolicy.DrainBeforeRevoke,
            provider.LastRegionRevocationPolicy);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void DomainBindingCannotBeRevokedWhileMappingIsActive()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        var early = kernel.RevokePlatformDomain(owner, binding);

        Assert.False(early.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, early.Error);
        Assert.Equal(0, provider.RevokeDomainCallCount);

        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.True(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CapabilityRevocationDrainsAndRevokesItsPlatformMapping()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        var revoke = kernel.RevokeCapability(capability);
        var localValidation = kernel.ValidateCapability(
            owner,
            capability,
            CapabilityRights.Map | CapabilityRights.Read);
        var staleMappingRevoke = kernel.RevokePlatformRegionMapping(owner, mapping);
        var remap = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);
        var transfer = kernel.TransferRegion(owner, target, region);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.False(localValidation.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, localValidation.Error);
        Assert.Equal(1, provider.RevokeRegionMappingCallCount);
        Assert.Equal(
            PlatformRegionRevocationPolicy.DrainBeforeRevoke,
            provider.LastRegionRevocationPolicy);
        Assert.False(staleMappingRevoke.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, staleMappingRevoke.Error);
        Assert.False(remap.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, remap.Error);
        Assert.Equal(1, provider.MapOwnedRegionCallCount);
        Assert.True(transfer.IsSuccess, transfer.Message);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void RevokingUnrelatedCapabilityDoesNotClosePlatformMapping()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var mappingCapability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var unrelated = kernel.MintCapability(
            new DomainId(10),
            owner,
            ResourceKind.Device,
            "test-device",
            CapabilityRights.Read).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            mappingCapability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        var revoke = kernel.RevokeCapability(unrelated);
        var transfer = kernel.TransferRegion(owner, target, region);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(0, provider.RevokeRegionMappingCallCount);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void FailedDrainLeavesCapabilityRevokedAndMappingFailClosed()
    {
        var provider = new HostPlatformAuthorityProvider(
            regionRevocationFailure: PlatformAuthorityStatus.Denied);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        _ = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        var revoke = kernel.RevokeCapability(capability);
        var localValidation = kernel.ValidateCapability(
            owner,
            capability,
            CapabilityRights.Map | CapabilityRights.Read);
        var remap = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);
        var transfer = kernel.TransferRegion(owner, target, region);

        Assert.False(revoke.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, revoke.Error);
        Assert.False(localValidation.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, localValidation.Error);
        Assert.Equal(1, provider.RevokeRegionMappingCallCount);
        Assert.Equal(
            PlatformRegionRevocationPolicy.DrainBeforeRevoke,
            provider.LastRegionRevocationPolicy);
        Assert.False(remap.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, remap.Error);
        Assert.Equal(1, provider.MapOwnedRegionCallCount);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);
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
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);

        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }
}
