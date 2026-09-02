using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformRevocationLifecycleTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void CapabilityRevocationStaysPinnedUntilVerifiedClosedReceipt()
    {
        var provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
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
        var draining = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;
        var transferWhileDraining = kernel.TransferRegion(owner, target, region);

        Assert.False(revoke.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, revoke.Error);
        Assert.False(localValidation.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, localValidation.Error);
        Assert.True(draining.LocalAuthorizationRevoked);
        Assert.Equal(PlatformExternalClosureState.Draining, draining.PlatformClosure);
        Assert.False(draining.LocalReservationReleased);
        Assert.False(draining.LocalReclaimAllowed);
        Assert.False(transferWhileDraining.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transferWhileDraining.Error);

        Assert.True(provider.LastRegionRevocationOperation.HasValue);
        var close = provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value);
        Assert.True(close.IsSuccess, close.Message);
        Assert.True(close.Value!.ProvesClosure);

        var observe = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        var closed = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;
        var transferAfterClose = kernel.TransferRegion(owner, target, region);

        Assert.True(observe.IsSuccess, observe.Message);
        Assert.True(closed.LocalAuthorizationRevoked);
        Assert.Equal(PlatformExternalClosureState.Closed, closed.PlatformClosure);
        Assert.True(closed.LocalReservationReleased);
        Assert.True(closed.LocalReclaimAllowed);
        Assert.True(transferAfterClose.IsSuccess, transferAfterClose.Message);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void DrainingMappingDoesNotRestartProviderRevocation()
    {
        var provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
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

        var first = kernel.RevokePlatformRegionMapping(owner, mapping);
        var second = kernel.RevokePlatformRegionMapping(owner, mapping);

        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, first.Error);
        Assert.False(second.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, second.Error);
        Assert.Equal(1, provider.RevokeRegionMappingCallCount);
        Assert.Equal(
            PlatformExternalClosureState.Draining,
            kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!.PlatformClosure);
    }

    [Theory]
    [Trait("Category", "Runtime")]
    [InlineData(ReceiptMutation.StaleGeneration, KernelError.StaleGeneration)]
    [InlineData(ReceiptMutation.WrongDomain, KernelError.WrongPlatformDomain)]
    [InlineData(ReceiptMutation.MalformedState, KernelError.PlatformFaulted)]
    public void InvalidClosedReceiptNeverReleasesRegionReservation(
        ReceiptMutation mutation,
        KernelError expectedError)
    {
        var provider = new ReceiptMutatingProvider(mutation);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
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

        _ = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.True(provider.Inner.LastRegionRevocationOperation.HasValue);
        var close = provider.Inner.CompleteRegionMappingRevocation(
            provider.Inner.LastRegionRevocationOperation.Value);
        Assert.True(close.IsSuccess, close.Message);

        var observed = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        var transfer = kernel.TransferRegion(owner, target, region);

        Assert.False(observed.IsSuccess);
        Assert.Equal(expectedError, observed.Error);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);

        var lifecycle = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping);
        if (mutation == ReceiptMutation.MalformedState)
        {
            Assert.False(lifecycle.IsSuccess);
            Assert.Equal(KernelError.PlatformFaulted, lifecycle.Error);
        }
        else
        {
            Assert.True(lifecycle.IsSuccess, lifecycle.Message);
            Assert.Equal(PlatformExternalClosureState.Draining, lifecycle.Value!.PlatformClosure);
            Assert.False(lifecycle.Value.LocalReservationReleased);

            provider.Mutation = ReceiptMutation.None;
            var recovered = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
            Assert.True(recovered.IsSuccess, recovered.Message);
        }
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ClosedReceiptStillRequiresExactLocalGenerationsBeforeReservationRelease()
    {
        var provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
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

        _ = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.True(provider.LastRegionRevocationOperation.HasValue);
        Assert.True(provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value).IsSuccess);

        var staleMapping = mapping with
        {
            Generation = new PlatformRegionMappingGeneration(mapping.Generation.Value + 1)
        };
        var staleBinding = mapping with
        {
            DomainBinding = mapping.DomainBinding with
            {
                Generation = new PlatformDomainBindingGeneration(
                    mapping.DomainBinding.Generation.Value + 1)
            }
        };
        var staleRegion = mapping with
        {
            Region = mapping.Region with
            {
                Generation = new RegionGeneration(mapping.Region.Generation.Value + 1)
            }
        };

        Assert.Equal(
            KernelError.StaleGeneration,
            kernel.ObservePlatformRegionMappingRevocation(owner, staleMapping).Error);
        Assert.Equal(
            KernelError.StaleGeneration,
            kernel.ObservePlatformRegionMappingRevocation(owner, staleBinding).Error);
        Assert.Equal(
            KernelError.StaleGeneration,
            kernel.ObservePlatformRegionMappingRevocation(owner, staleRegion).Error);

        var wrongOwner = kernel.ObservePlatformRegionMappingRevocation(target, mapping);
        Assert.False(wrongOwner.IsSuccess);
        Assert.Equal(KernelError.WrongPlatformDomain, wrongOwner.Error);

        var transferBeforeExactObservation = kernel.TransferRegion(owner, target, region);
        Assert.False(transferBeforeExactObservation.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transferBeforeExactObservation.Error);

        var exact = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        Assert.True(exact.IsSuccess, exact.Message);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void DuplicateClosedObservationIsIdempotent()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
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

        var first = kernel.RevokePlatformRegionMapping(owner, mapping);
        var duplicate = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        var lifecycle = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;

        Assert.True(first.IsSuccess, first.Message);
        Assert.True(duplicate.IsSuccess, duplicate.Message);
        Assert.Equal(1, provider.RevokeRegionMappingCallCount);
        Assert.Equal(PlatformExternalClosureState.Closed, lifecycle.PlatformClosure);
        Assert.True(lifecycle.LocalReservationReleased);
        Assert.True(lifecycle.LocalReclaimAllowed);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void LegacySynchronousRevokeWithoutReceiptCannotAuthorizeLocalReclaim()
    {
        var provider = new LegacySynchronousProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
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

        var revoke = kernel.RevokePlatformRegionMapping(owner, mapping);
        var transfer = kernel.TransferRegion(owner, target, region);
        var lifecycle = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;

        Assert.False(revoke.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, revoke.Error);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);
        Assert.Equal(PlatformExternalClosureState.Draining, lifecycle.PlatformClosure);
        Assert.False(lifecycle.LocalReservationReleased);
        Assert.False(lifecycle.LocalReclaimAllowed);
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

    private enum ReceiptMutation
    {
        None = 0,
        StaleGeneration,
        WrongDomain,
        MalformedState
    }

    private sealed class ReceiptMutatingProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformRegionRevocationProvider
    {
        public ReceiptMutatingProvider(ReceiptMutation mutation)
        {
            Mutation = mutation;
            Inner = new HostPlatformAuthorityProvider(
                deferRegionRevocationCompletion: true);
        }

        public HostPlatformAuthorityProvider Inner { get; }
        public ReceiptMutation Mutation { get; set; }
        public PlatformProviderDescriptor Descriptor => Inner.Descriptor;

        public PlatformFeatureManifest QueryFeatures() => Inner.QueryFeatures();

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject) => Inner.BindDomain(subject);

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            Inner.RevokeDomain(lease);

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            Inner.MapOwnedRegion(domainLease, region, access);

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            Inner.RevokeRegionMapping(mapping, policy);

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            Inner.BeginRegionMappingRevocation(mapping, policy);

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            var observed = Inner.ObserveCompletion(operation);
            if (!observed.IsSuccess || Mutation == ReceiptMutation.None)
                return observed;

            var receipt = observed.Value!;
            receipt = Mutation switch
            {
                ReceiptMutation.StaleGeneration => receipt with
                {
                    Generation = new PlatformOperationGeneration(receipt.Generation.Value + 1)
                },
                ReceiptMutation.WrongDomain => receipt with
                {
                    DomainLease = receipt.DomainLease with
                    {
                        LeaseId = new PlatformProviderDomainLeaseId(
                            receipt.DomainLease.LeaseId.Value + 1000)
                    }
                },
                ReceiptMutation.MalformedState => receipt with
                {
                    State = (PlatformCompletionState)999
                },
                _ => receipt
            };

            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(receipt);
        }
    }

    private sealed class LegacySynchronousProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider
    {
        private readonly HostPlatformAuthorityProvider _inner = new();

        public PlatformProviderDescriptor Descriptor => _inner.Descriptor;
        public PlatformFeatureManifest QueryFeatures() => _inner.QueryFeatures();

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject) => _inner.BindDomain(subject);

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            _inner.RevokeDomain(lease);

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            _inner.MapOwnedRegion(domainLease, region, access);

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            _inner.RevokeRegionMapping(mapping, policy);
    }
}
