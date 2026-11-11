using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformRevocationLifecycleTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void LegacyOriginalRevokeCannotRepeatOrReleaseBacking(int outcome)
    {
        LegacySynchronousProvider provider = new LegacySynchronousProvider { Outcome = outcome };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 101, 1010);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle, PlatformMemoryAccess.Read).Value!;
        provider.DuringRevoke = () =>
        {
            Assert.False(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
            if (outcome == 4)
            {
                BindingFlags reflection = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
                object gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", reflection)!.GetValue(kernel.PlatformAuthority)!;
                lock (gate)
                    ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeSubjects", reflection)!
                        .GetValue(kernel.PlatformAuthority)!).Remove(binding.Subject);
            }
        };

        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.False(kernel.ObservePlatformRegionMappingRevocation(owner, mapping).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Region,
            new RegionOwner(new DomainId(1010), owner.Generation)));
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = routes[mapping.MappingId]!;
        Assert.False((bool)original.GetType().GetProperty("ClosureTransitionInFlight")!.GetValue(original)!);
        Assert.True((bool)original.GetType().GetProperty("LegacyRevokeMayHaveEffect")!.GetValue(original)!);
        PlatformAuthorityResult? observation = (PlatformAuthorityResult?)original.GetType().GetProperty("LegacyRevokeObservation")!.GetValue(original);
        if (outcome == 3) Assert.Null(observation);
        else Assert.Equal(outcome is 0 or 4, observation!.Value.IsSuccess);
    }
    [Fact]
    [Trait("Category", "Runtime")]
    public void CapabilityRevocationStaysPinnedUntilVerifiedClosedReceipt()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        KernelResult revoke = kernel.RevokeCapability(capability);
        KernelResult<CapabilityDescriptorV1> localValidation = kernel.ValidateCapability(
            owner,
            capability,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformRegionMappingLifecycle draining = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;
        KernelResult<OwnedRegion<int>> transferWhileDraining = kernel.TransferRegion(owner, target, region);

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
        PlatformAuthorityResult<PlatformCompletionReceipt> close = provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value);
        Assert.True(close.IsSuccess, close.Message);
        Assert.True(close.Value!.ProvesClosure);

        KernelResult observe = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        PlatformRegionMappingLifecycle closed = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;
        KernelResult<OwnedRegion<int>> transferAfterClose = kernel.TransferRegion(owner, target, region);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        KernelResult first = kernel.RevokePlatformRegionMapping(owner, mapping);
        KernelResult second = kernel.RevokePlatformRegionMapping(owner, mapping);

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
        ReceiptMutatingProvider provider = new ReceiptMutatingProvider(mutation);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Write);
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Write).Value!;

        _ = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.True(provider.Inner.LastRegionRevocationOperation.HasValue);
        PlatformAuthorityResult<PlatformCompletionReceipt> close = provider.Inner.CompleteRegionMappingRevocation(
            provider.Inner.LastRegionRevocationOperation.Value);
        Assert.True(close.IsSuccess, close.Message);

        KernelResult observed = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        KernelResult<OwnedRegion<int>> transfer = kernel.TransferRegion(owner, target, region);

        Assert.False(observed.IsSuccess);
        Assert.Equal(expectedError, observed.Error);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);

        KernelResult<PlatformRegionMappingLifecycle> lifecycle = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping);
        if (mutation == ReceiptMutation.MalformedState)
        {
            Assert.True(lifecycle.IsSuccess, lifecycle.Message);
            Assert.Equal(PlatformExternalClosureState.Faulted, lifecycle.Value!.PlatformClosure);
            Assert.False(lifecycle.Value.LocalReservationReleased);
            Assert.False(lifecycle.Value.LocalReclaimAllowed);
        }
        else
        {
            Assert.True(lifecycle.IsSuccess, lifecycle.Message);
            Assert.Equal(PlatformExternalClosureState.Draining, lifecycle.Value!.PlatformClosure);
            Assert.False(lifecycle.Value.LocalReservationReleased);

            provider.Mutation = ReceiptMutation.None;
            KernelResult recovered = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
            Assert.True(recovered.IsSuccess, recovered.Message);
        }
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ClosedReceiptStillRequiresExactLocalGenerationsBeforeReservationRelease()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        _ = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.True(provider.LastRegionRevocationOperation.HasValue);
        Assert.True(provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value).IsSuccess);

        PlatformRegionMapping staleMapping = mapping with
        {
            Generation = new PlatformRegionMappingGeneration(mapping.Generation.Value + 1)
        };
        PlatformRegionMapping staleBinding = mapping with
        {
            DomainBinding = mapping.DomainBinding with
            {
                Generation = new PlatformDomainBindingGeneration(
                    mapping.DomainBinding.Generation.Value + 1)
            }
        };
        PlatformRegionMapping staleRegion = mapping with
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

        KernelResult wrongOwner = kernel.ObservePlatformRegionMappingRevocation(target, mapping);
        Assert.False(wrongOwner.IsSuccess);
        Assert.Equal(KernelError.WrongPlatformDomain, wrongOwner.Error);

        KernelResult<OwnedRegion<int>> transferBeforeExactObservation = kernel.TransferRegion(owner, target, region);
        Assert.False(transferBeforeExactObservation.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transferBeforeExactObservation.Error);

        KernelResult exact = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        Assert.True(exact.IsSuccess, exact.Message);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void DuplicateClosedObservationIsIdempotent()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        KernelResult first = kernel.RevokePlatformRegionMapping(owner, mapping);
        KernelResult duplicate = kernel.ObservePlatformRegionMappingRevocation(owner, mapping);
        PlatformRegionMappingLifecycle lifecycle = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;

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
        LegacySynchronousProvider provider = new LegacySynchronousProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        KernelResult revoke = kernel.RevokePlatformRegionMapping(owner, mapping);
        KernelResult<OwnedRegion<int>> transfer = kernel.TransferRegion(owner, target, region);
        PlatformRegionMappingLifecycle lifecycle = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping).Value!;

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
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);

        KernelResult<CapabilityDescriptorV1> capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);

        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }

    public enum ReceiptMutation
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
            PlatformAuthorityResult<PlatformCompletionReceipt> observed = Inner.ObserveCompletion(operation);
            if (!observed.IsSuccess || Mutation == ReceiptMutation.None)
                return observed;

            PlatformCompletionReceipt receipt = observed.Value!;
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
        public int Outcome { get; init; }
        public int RevokeCalls { get; private set; }
        public Action? DuringRevoke { get; set; }

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
            PlatformRegionRevocationPolicy policy)
        {
            RevokeCalls++;
            PlatformAuthorityResult result = _inner.RevokeRegionMapping(mapping, policy);
            DuringRevoke?.Invoke();
            return Outcome switch
            {
                1 => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Original effect may already exist."),
                2 => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original effect may already exist."),
                3 => throw new InvalidOperationException("Original response lost after effect."),
                _ => result
            };
        }
    }
}
