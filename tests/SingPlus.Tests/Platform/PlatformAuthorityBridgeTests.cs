using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformAuthorityBridgeTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void OriginalRootBindOutcomeRetainsFactAndActualBacking(int outcome)
    {
        RootSourceProvider provider = new RootSourceProvider { Outcome = outcome };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1360, 1791);
        kernel.AllocateRegion(owner, 128);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, owner);
        object? original = null;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary pending = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_pendingDomainBinds", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary unresolved = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_unresolvedDomainBinds", flags)!.GetValue(kernel.PlatformAuthority)!;
        provider.DuringBind = () =>
        {
            original = pending[subject]!;
            Assert.True((bool)original.GetType().GetProperty("EffectMayHaveOccurred")!.GetValue(original)!);
            Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
            if (outcome == 3) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
        Assert.Equal(1, provider.BindCalls);
        Assert.False(pending.Contains(subject));
        if (outcome == 4)
        {
            Assert.False(unresolved.Contains(subject));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
            return;
        }
        Assert.Same(original, unresolved[subject]);
        object? observation = original!.GetType().GetProperty("Observation")!.GetValue(original);
        if (outcome == 1) Assert.Null(observation);
        else Assert.NotNull(observation);
        if (outcome == 2) Assert.NotNull(original.GetType().GetProperty("ReturnedCleanupObservation")!.GetValue(original));
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.BindCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }
    [Fact]
    public void ResetBeforeOriginalRootBindEffectDoesNotPinActualReclaim()
    {
        RootSourceProvider provider = new RootSourceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1359, 1790);
        kernel.AllocateRegion(owner, 128);
        provider.BeforeSource = () =>
        {
            provider.BeforeSource = null;
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
        Assert.Equal(0, provider.BindCalls);
        Assert.False(kernel.PlatformAuthority.HasUnresolvedDomainBindEffect(new(process.DomainId, owner)));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    private sealed class RootSourceProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider, IPlatformProviderIncarnationSource
    {
        private readonly HostPlatformAuthorityProvider host = new();
        public Action? BeforeSource { get; set; }
        public Action? DuringBind { get; set; }
        public int Outcome { get; init; } = -1;
        public int BindCalls { get; private set; }
        public PlatformProviderIncarnation CurrentIncarnation { get { BeforeSource?.Invoke(); return new(1); } }
        public PlatformProviderDescriptor Descriptor => host.Descriptor;
        public PlatformFeatureManifest QueryFeatures() => host.QueryFeatures();
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            BindCalls++;
            PlatformAuthorityResult<PlatformProviderDomainLease> result = Outcome == 4 ? PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "No invocation accepted.") : host.BindDomain(subject);
            DuringBind?.Invoke();
            return Outcome switch
            {
                0 => PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Denied, "Original outcome failed after effect."),
                1 => throw new IOException("Original response lost."),
                2 => PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(result.Value! with { LeaseId = new(0) }),
                _ => result
            };
        }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => host.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) => host.MapOwnedRegion(d, r, a);
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => host.RevokeRegionMapping(m, p);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public void MalformedRootCleanupCannotForgetOriginalSubject(int mutation, int cleanup)
    {
        MalformedRootProvider provider = new MalformedRootProvider(mutation, cleanup);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1348, 1780);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, owner);
        bool pinObserved = false;
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
            PlatformProviderDomainLease receipt = host.BindDomain(subject).Value!;
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
        MalformedMappingProvider provider = new MalformedMappingProvider(mutation);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1346, 1760);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1347, 1770);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding domain = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
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
            PlatformAuthorityResult<PlatformProviderRegionMappingLease> result = host.MapOwnedRegion(domain, region, access);
            if (!result.IsSuccess) return result;
            PlatformProviderRegionMappingLease receipt = result.Value!;
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
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);

        KernelResult<PlatformDomainBinding> binding = kernel.BindPlatformDomain(owner);

        Assert.False(binding.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, binding.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void HostProviderIsDeterministicForEquivalentFreshInstances()
    {
        HostPlatformAuthorityProvider left = new HostPlatformAuthorityProvider();
        HostPlatformAuthorityProvider right = new HostPlatformAuthorityProvider();
        PlatformDomainIdentity subject = new PlatformDomainIdentity(
            new DomainId(10),
            new ProcessHandle(new ProcessId(11), 1));

        PlatformAuthorityResult<PlatformProviderDomainLease> leftLease = left.BindDomain(subject);
        PlatformAuthorityResult<PlatformProviderDomainLease> rightLease = right.BindDomain(subject);

        Assert.Equal(left.Descriptor, right.Descriptor);
        Assert.True(leftLease.IsSuccess, leftLease.Message);
        Assert.True(rightLease.IsSuccess, rightLease.Message);
        Assert.Equal(leftLease.Value, rightLease.Value);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ProviderContractDoesNotAcceptLocalCapabilityIds()
    {
        Type[] parameterTypes = typeof(IPlatformAuthorityProvider)
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;

        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> first = kernel.AllocateRegion(owner, 123).Value!;
        OwnedRegion<int> second = kernel.AllocateRegion(owner, 456).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            first.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformDomainBinding stale = binding with
        {
            Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1)
        };

        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult revoke = kernel.RevokePlatformDomain(owner, binding);
        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle left) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle right) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(right, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(left).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            right,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            PlatformAuthorityFeatures.NeutralDomainBinding);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);

        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);

        KernelResult<OwnedRegion<int>> transfer = kernel.TransferRegion(owner, target, region);
        Assert.True(transfer.IsSuccess, transfer.Message);
        Assert.Equal(new RegionGeneration(2), transfer.Value!.Handle.Generation);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ActiveMappingBlocksOwnershipMutationButTerminationOrchestratesClosure()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? ownerProcess, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
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

        KernelResult<OwnedRegion<int>> transferWhileMapped = kernel.TransferRegion(owner, target, region);
        KernelResult<BorrowLeaseHandle> loanWhileMapped = kernel.Regions.Loan(
            region.Handle,
            new RegionOwner(new DomainId(10), owner.Generation),
            new RegionOwner(new DomainId(20), target.Generation));
        KernelResult releaseWhileMapped = kernel.ReleaseRegion(owner, region);
        KernelResult terminate = kernel.TerminateProcess(owner);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
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

        KernelResult first = kernel.RevokePlatformRegionMapping(owner, mapping);
        KernelResult second = kernel.RevokePlatformRegionMapping(owner, mapping);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
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

        KernelResult early = kernel.RevokePlatformDomain(owner, binding);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
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
        KernelResult staleMappingRevoke = kernel.RevokePlatformRegionMapping(owner, mapping);
        KernelResult<PlatformRegionMapping> remap = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);
        KernelResult<OwnedRegion<int>> transfer = kernel.TransferRegion(owner, target, region);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId mappingCapability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        CapabilityId unrelated = kernel.MintCapability(
            new DomainId(10),
            owner,
            ResourceKind.Device,
            "test-device",
            CapabilityRights.Read).Value!.CapabilityId;
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            mappingCapability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;

        KernelResult revoke = kernel.RevokeCapability(unrelated);
        KernelResult<OwnedRegion<int>> transfer = kernel.TransferRegion(owner, target, region);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            regionRevocationFailure: PlatformAuthorityStatus.Denied);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintRegionCapability(
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

        KernelResult revoke = kernel.RevokeCapability(capability);
        KernelResult<CapabilityDescriptorV1> localValidation = kernel.ValidateCapability(
            owner,
            capability,
            CapabilityRights.Map | CapabilityRights.Read);
        KernelResult<PlatformRegionMapping> remap = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);
        KernelResult<OwnedRegion<int>> transfer = kernel.TransferRegion(owner, target, region);

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
}
