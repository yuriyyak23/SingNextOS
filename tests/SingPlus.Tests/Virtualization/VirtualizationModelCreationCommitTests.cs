using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using Xunit;

namespace SingPlus.Tests.Virtualization;

public sealed class VirtualizationModelCreationCommitTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void CreationFaultsKeepKnownModelCleanupOrUnknownTuplePin(int fault)
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 960, 9600);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        if (fault == 0) provider.AfterCreate = () => Assert.True(kernel.RevokeCapability(source).IsSuccess);
        if (fault == 1) provider.AfterCreate = () =>
        {
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        };
        if (fault == 2) provider.AfterCreate = () => throw new InvalidOperationException("controlled lost model receipt");
        if (fault == 3) provider.CreateFailure = PlatformAuthorityStatus.Denied;
        if (fault == 4) provider.AfterCreate = () => Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        if (fault == 5) provider.MalformedLease = true;
        if (fault == 6) SetNonce(kernel, () => { Assert.True(kernel.RevokeCapability(source).IsSuccess); return Guid.NewGuid(); });
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        Assert.Equal(1, provider.CreateCalls);
        if (fault is 7 or 8)
        {
            Assert.True(created.IsSuccess, created.Message);
            if (fault == 7) provider.RevokeFailure = PlatformAuthorityStatus.Faulted;
            else provider.BeforeRevoke = () => throw new InvalidOperationException("controlled lost revoke receipt");
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, provider.RevokeCalls);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        }
        else Assert.False(created.IsSuccess);
        provider.AfterCreate = null;
        provider.CreateFailure = null;
        provider.RevokeFailure = null;
        provider.BeforeRevoke = null;
        if (fault is 0 or 1 or 6 or 7)
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(ProcessState.Exited, process.State);
            Assert.Equal(fault == 7 ? 2 : 1, provider.RevokeCalls);
            Assert.Equal(KernelError.StaleHandle, kernel.QueryProcessTeardown(owner).Error);
        }
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(fault == 8 ? 1 : 0, provider.RevokeCalls);
            Assert.Equal(1, provider.CreateCalls);
        }
        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
    }

    [Fact]
    public void PartialCapabilitiesCannotAdmitEffectBeforeActualPublication()
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 961, 9610);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var nonceCalls = 0;
        SetNonce(kernel, () =>
        {
            if (++nonceCalls == 2)
            {
                var record = Assert.Single(Records(kernel).ForOwner(owner));
                Assert.False(record.AuthorityPublished);
                Assert.Single(record.Capabilities);
                Assert.Equal(KernelError.PlatformBindingActive,
                    kernel.ConfigureVirtualDomain(owner, record.Handle, record.Capabilities[0]).Error);
                Assert.Equal(0, provider.TransitionCalls);
            }
            return Guid.NewGuid();
        });
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        Assert.True(created.IsSuccess, created.Message);
        Assert.True(Assert.Single(Records(kernel).ForOwner(owner)).AuthorityPublished);
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Value!.Domain, created.Value.ConfigureCapability).IsSuccess);
        Assert.Equal(1, provider.TransitionCalls);
        Assert.True(kernel.DestroyVirtualDomain(owner, created.Value.Domain, created.Value.ConfigureCapability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls); // a closed model record is not reopened by teardown
    }

    [Fact]
    public async Task ConcurrentStopDuringModelCallbackCannotReclaimOrPublishLateAuthority()
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 962, 9620);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.AfterCreate = () =>
        {
            foreach (var pair in new (object Owner, string Field)[]
            {
                (kernel, "_platformMemoryUseGate"), (kernel.PlatformAuthority, "_secureDomainLifecycleGate"),
                (kernel.CapabilityAuthority, "_gate")
            })
                Assert.False(Monitor.IsEntered(Field(pair.Owner, pair.Field)));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var work = Task.Run(() => kernel.CreateVirtualDomain(owner, source, new(1, 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.Equal(KernelError.PlatformBindingActive, kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Error);
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
            Assert.Equal(0, provider.RevokeCalls);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.False(Assert.Single(Records(kernel).ForOwner(owner)).AuthorityPublished);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.CreateCalls);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void ExactSourceAndProfileRefusalDoNotStartModelEffect()
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 963, 9630);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(0, 4096)).IsSuccess);
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(257, 4096)).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        var (_, replacement) = TestFixtures.Create(kernel, 963, 9630, generation: 2);
        Assert.False(kernel.CreateVirtualDomain(replacement, source, new(1, 4096)).IsSuccess);
        Assert.Equal(0, provider.CreateCalls);
    }

    [Fact]
    public void ResetDuringModelRevokeBalancesReplacementInterlockWithoutClosureClaim()
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 964, 9640);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        provider.BeforeRevoke = () => Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        var records = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        Assert.Single(records.Values.Cast<object>());
        foreach (var value in records.Values)
        {
            Assert.False((bool)value!.GetType().GetProperty("CloseInFlight")!.GetValue(value)!);
            Assert.True((bool)value.GetType().GetProperty("Quarantined")!.GetValue(value)!);
        }
        provider.BeforeRevoke = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void HostModelStaleRevokeCannotRemoveCurrentLease()
    {
        var provider = new HostPlatformAuthorityProvider();
        var owner = new PlatformDomainIdentity(new DomainId(9650), new ProcessHandle(new ProcessId(965), 1));
        var lease = provider.CreateVirtualDomain(owner, new(1, 4096)).Value!;
        Assert.Equal(PlatformAuthorityStatus.Stale,
            provider.RevokeVirtualDomain(lease with { Generation = new(lease.Generation.Value + 1) }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            provider.RevokeVirtualDomain(lease with { Owner = owner with { DomainId = new(9651) } }).Status);
        Assert.True(provider.TransitionVirtualDomain(lease, PlatformVirtualDomainTransition.Configure).IsSuccess);
        Assert.True(provider.RevokeVirtualDomain(lease).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentHostModelCreatesKeepUniqueLeaseOwnerTuples()
    {
        var provider = new HostPlatformAuthorityProvider();
        var leases = await Task.WhenAll(Enumerable.Range(1, 32).Select(i => Task.Run(() =>
        {
            var owner = new PlatformDomainIdentity(new DomainId((ulong)(9660 + i)),
                new ProcessHandle(new ProcessId((ulong)(966 + i)), 1));
            var created = provider.CreateVirtualDomain(owner, new(1, 4096));
            Assert.True(created.IsSuccess);
            Assert.Equal(owner, created.Value!.Owner);
            return created.Value;
        })));
        Assert.Equal(32, leases.Select(x => x.LeaseId).Distinct().Count());
        foreach (var lease in leases) Assert.True(provider.RevokeVirtualDomain(lease).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task PendingModelTransitionPreventsClosureAndLateStatePublication(int step)
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 970, 9700);
        var source = kernel.MintCapability(new DomainId(9700), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        if (step > 0) Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        if (step > 1) Assert.True(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        if (step > 2) Assert.True(kernel.ParkVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        var local = Assert.Single(Records(kernel).ForOwner(owner));
        var before = provider.TransitionCalls;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.AfterTransition = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        KernelResult Transition() => step == 0 ? kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability) :
            step == 1 ? kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability) :
            step == 2 ? kernel.ParkVirtualDomain(owner, created.Domain, created.ExecuteCapability) :
            kernel.ResumeVirtualDomain(owner, created.Domain, created.ExecuteCapability);
        var work = Task.Run(Transition);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.Equal(KernelError.PlatformBindingActive, Transition().Error);
            Assert.Equal(KernelError.PlatformBindingActive, kernel.PlatformAuthority.RevokeVirtualDomain(local.ModelBinding!.Value).Error);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        provider.AfterTransition = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(VirtualDomainState.Closed, local.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(before + 2, provider.TransitionCalls); // pending effect then actual cleanup drain
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ModelUnknownTransitionRequiresContainmentConsumerOrKeepsPin(int fault)
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 971, 9710);
        var source = kernel.MintCapability(new DomainId(9710), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        if (fault == 0) provider.AfterTransition = () => throw new InvalidOperationException("controlled lost transition receipt");
        if (fault == 1) provider.TransitionFailure = PlatformAuthorityStatus.Denied;
        if (fault == 2) provider.AfterTransition = () => Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        if (fault == 3) provider.TransitionFailure = PlatformAuthorityStatus.NotAccepted;
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        var local = Assert.Single(Records(kernel).ForOwner(owner));
        Assert.Equal(fault == 2 ? VirtualDomainState.Quarantined : VirtualDomainState.Configured, local.State);
        provider.AfterTransition = null;
        provider.TransitionFailure = null;
        var bridgeRecords = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        foreach (var value in bridgeRecords.Values)
            Assert.False((bool)value!.GetType().GetProperty("TransitionInFlight")!.GetValue(value)!);
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        if (fault == 3)
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess); // explicit pre-admission refusal, real model cleanup
            Assert.Equal(1, provider.RevokeCalls);
        }
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
        }
    }

    [Fact]
    public void ModelSourceRevokeDuringCallbackCannotCommitStateOrReplayEffect()
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 972, 9720);
        var source = kernel.MintCapability(new DomainId(9720), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        provider.AfterTransition = () => Assert.True(kernel.RevokeCapability(created.ExecuteCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).Error);
        Assert.Equal(VirtualDomainState.Configured, Assert.Single(Records(kernel).ForOwner(owner)).State);
        var effects = provider.TransitionCalls;
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        Assert.Equal(effects, provider.TransitionCalls);
        provider.AfterTransition = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void UnknownModelTransitionAndRevokedSourceRefuseBeforeCallback()
    {
        var provider = new ObservedModelProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 973, 9730);
        var source = kernel.MintCapability(new DomainId(9730), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        var local = Assert.Single(Records(kernel).ForOwner(owner));
        Assert.Equal(KernelError.PlatformDenied,
            kernel.PlatformAuthority.TransitionVirtualDomain(local.ModelBinding!.Value, (PlatformVirtualDomainTransition)999).Error);
        Assert.True(kernel.RevokeCapability(created.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).Error);
        Assert.Equal(0, provider.TransitionCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
    private static VirtualDomainAuthority Records(RuntimeKernel kernel) => (VirtualDomainAuthority)Field(kernel, "_virtualDomains");
    private static void SetNonce(RuntimeKernel kernel, Func<Guid> factory) =>
        typeof(CapabilityAuthority).GetField("_nonceFactory", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(kernel.CapabilityAuthority, factory);

    private sealed class ObservedModelProvider : IPlatformAuthorityProvider, IPlatformVirtualizationProvider, IPlatformFeatureProvider
    {
        private readonly HostPlatformAuthorityProvider _inner = new();
        public Action? AfterCreate { get; set; }
        public Action? BeforeRevoke { get; set; }
        public Action? AfterTransition { get; set; }
        public PlatformAuthorityStatus? TransitionFailure { get; set; }
        public PlatformAuthorityStatus? CreateFailure { get; set; }
        public PlatformAuthorityStatus? RevokeFailure { get; set; }
        public bool MalformedLease { get; set; }
        public int CreateCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public int TransitionCalls { get; private set; }
        public PlatformProviderDescriptor Descriptor => _inner.Descriptor;
        public PlatformFeatureManifest QueryFeatures() => _inner.QueryFeatures();
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) => _inner.BindDomain(subject);
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => _inner.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease lease,
            PlatformRegionIdentity region, PlatformMemoryAccess access) => _inner.MapOwnedRegion(lease, region, access);
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease lease,
            PlatformRegionRevocationPolicy policy) => _inner.RevokeRegionMapping(lease, policy);
        public PlatformAuthorityResult<PlatformProviderVirtualDomainLease> CreateVirtualDomain(
            PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile)
        {
            CreateCalls++;
            var result = _inner.CreateVirtualDomain(owner, profile);
            AfterCreate?.Invoke();
            if (CreateFailure is { } status) return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(status, "controlled unknown creation");
            return MalformedLease ? PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Ok(
                result.Value! with { LeaseId = new(0) }) : result;
        }
        public PlatformAuthorityResult TransitionVirtualDomain(PlatformProviderVirtualDomainLease lease, PlatformVirtualDomainTransition transition)
        {
            TransitionCalls++;
            if (TransitionFailure == PlatformAuthorityStatus.NotAccepted)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "controlled refusal before model mutation");
            var result = _inner.TransitionVirtualDomain(lease, transition);
            AfterTransition?.Invoke();
            return TransitionFailure is { } status ? PlatformAuthorityResult.Fail(status, "controlled unknown model transition") : result;
        }
        public PlatformAuthorityResult RevokeVirtualDomain(PlatformProviderVirtualDomainLease lease)
        {
            RevokeCalls++;
            BeforeRevoke?.Invoke();
            return RevokeFailure is { } status ? PlatformAuthorityResult.Fail(status, "controlled model closure refusal") : _inner.RevokeVirtualDomain(lease);
        }
    }
}
