using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformAuthorityBridgeTests
{
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
        var subject = new PlatformDomainIdentity(new DomainId(10), 1);

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
    public void ActiveMappingBlocksTransferLoanReleaseAndTerminationUntilRevoked()
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

        var transferWhileMapped = kernel.TransferRegion(owner, target, region);
        var loanWhileMapped = kernel.Regions.Loan(
            region.Handle,
            new RegionOwner(new DomainId(10), owner.Generation),
            new RegionOwner(new DomainId(20), target.Generation));
        var releaseWhileMapped = kernel.ReleaseRegion(owner, region);
        var terminateWhileBound = kernel.TerminateProcess(owner);

        Assert.False(transferWhileMapped.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transferWhileMapped.Error);
        Assert.False(loanWhileMapped.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, loanWhileMapped.Error);
        Assert.False(releaseWhileMapped.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, releaseWhileMapped.Error);
        Assert.False(terminateWhileBound.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, terminateWhileBound.Error);

        var revokeMapping = kernel.RevokePlatformRegionMapping(owner, mapping);
        var revokeDomain = kernel.RevokePlatformDomain(owner, binding);
        var transferAfterRevoke = kernel.TransferRegion(owner, target, region);

        Assert.True(revokeMapping.IsSuccess, revokeMapping.Message);
        Assert.True(revokeDomain.IsSuccess, revokeDomain.Message);
        Assert.True(transferAfterRevoke.IsSuccess, transferAfterRevoke.Message);
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
