using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu.Tests;
public sealed class DmaAdmissionContinuityTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void CopiedDmaOwnerCannotConsumeOriginalVisibility(bool acquire)
    {
        var (provider, proxy, parent, request) = Create(writer: true);
        var grant = provider.BindDmaGrant(request).Value!;
        if (acquire) Assert.True(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaGrants", flags)!.GetValue(provider)!;
        var original = records[grant.GrantId]!;
        records[grant.GrantId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.False(acquire ? provider.AcquireDmaGrantVisibility(grant).IsSuccess : provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        Assert.Equal(acquire ? 1 : 0, proxy.DmaPrepareCalls);
        Assert.Equal(0, proxy.DmaAcquireCalls);
        records[grant.GrantId] = original;
        Assert.True(acquire ? provider.AcquireDmaGrantVisibility(grant).IsSuccess : provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        Assert.True(provider.RevokeDmaGrant(grant).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveDmaGrantCount);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void LostDmaRouteCannotHideOriginalMappingAndDeviceDependencies(bool copiedClosed)
    {
        var (provider, proxy, parent, request) = Create();
        var grant = provider.BindDmaGrant(request).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaGrants", flags)!.GetValue(provider)!;
        var original = records[grant.GrantId]!;
        if (copiedClosed)
        {
            var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            copy.GetType().GetProperty("Revoked")!.SetValue(copy, true);
            records[grant.GrantId] = copy;
        }
        else records.Remove(grant.GrantId);
        Assert.False(provider.BindDmaGrant(request).IsSuccess);
        Assert.Equal(1, proxy.DmaCalls);
        Assert.False(provider.RevokeDevice(request.DeviceLease).IsSuccess);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        Assert.False(provider.BeginRegionMappingRevocation(request.MappingLease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, proxy.Runtime.ActiveDmaGrantCount);
        records[grant.GrantId] = original;
        Assert.True(provider.RevokeDmaGrant(grant).IsSuccess);
        Assert.True(provider.BeginRegionMappingRevocation(request.MappingLease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.RevokeDevice(request.DeviceLease).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Theory]
    [InlineData("device", false, false)] [InlineData("device", true, false)]
    [InlineData("mapping", false, false)] [InlineData("mapping", true, false)]
    [InlineData("parent", false, false)] [InlineData("parent", true, false)]
    [InlineData("device", false, true)] [InlineData("device", true, true)]
    [InlineData("mapping", false, true)] [InlineData("mapping", true, true)]
    [InlineData("parent", false, true)] [InlineData("parent", true, true)]
    public void PublishedGrantRefusesLostBindSourceBeforeCloseOrVisibility(string source, bool absent, bool prepared)
    {
        var (provider, proxy, parent, request) = Create(writer: true);
        var grant = provider.BindDmaGrant(request).Value!;
        if (prepared) Assert.True(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var field = source == "device" ? "_deviceLeases" : source == "mapping" ? "_providerMappings" : "_domains";
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField(field, flags)!.GetValue(provider)!;
        object key = source == "device" ? request.DeviceLease.LeaseId : source == "mapping" ? request.MappingLease.MappingId : parent.LeaseId;
        var original = owners[key]!;
        var cycleField = provider.GetType().GetField("_nextProviderDmaVisibilityCycle", flags)!;
        var cycle = cycleField.GetValue(provider);
        if (absent) owners.Remove(key);
        else owners[key] = Activator.CreateInstance(original.GetType(),
            original.GetType().GetProperty(source == "mapping" ? "Mapping" : "Lease")!.GetValue(original),
            original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        Assert.False(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        Assert.False(provider.AcquireDmaGrantVisibility(grant).IsSuccess);
        Assert.False(provider.RevokeDmaGrant(grant).IsSuccess);
        Assert.False(provider.RevokeDmaGrant(grant).IsSuccess);
        Assert.Equal(prepared ? 1 : 0, proxy.DmaPrepareCalls);
        Assert.Equal(0, proxy.DmaAcquireCalls);
        Assert.Equal(0, proxy.DmaCloseCalls);
        Assert.Equal(cycle, cycleField.GetValue(provider));
        Assert.Equal(1, proxy.Runtime.ActiveDmaGrantCount);
        // Original-source restoration arranges the positive control, not durable recovery.
        owners[key] = original;
        if (!prepared) Assert.True(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        Assert.True(provider.AcquireDmaGrantVisibility(grant).IsSuccess);
        Assert.True(provider.RevokeDmaGrant(grant).IsSuccess);
        Assert.Equal(1, proxy.DmaCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveDmaGrantCount);
        Assert.True(provider.BeginRegionMappingRevocation(request.MappingLease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.RevokeDevice(request.DeviceLease).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    private static (HybridCpuPlatformAuthorityProvider Provider, ChildClosureTupleTests.RootCloseRuntimeProxy Proxy,
        PlatformProviderDomainLease Parent, PlatformDmaGrantRequest Request) Create(bool writer = false)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        var device = provider.BindDevice(parent, new("dma-test"), PlatformDeviceRights.Read | PlatformDeviceRights.Configure | (writer ? PlatformDeviceRights.Write : PlatformDeviceRights.None)).Value!;
        var region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        var mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read | (writer ? PlatformMemoryAccess.Write : PlatformMemoryAccess.None))).Value!;
        return (provider, proxy, parent, new(device, mapping.Lease, mapping.Slice, new(0, 4096), writer ? PlatformDmaDirection.Bidirectional : PlatformDmaDirection.DeviceReadsMemory));
    }
    [Theory]
    [InlineData(false, "exact")][InlineData(true, "exact")]
    [InlineData(true, "state")]
    [InlineData(false, "provider-state")][InlineData(true, "provider-state")]
    [InlineData(false, "owner")][InlineData(true, "owner")]
    [InlineData(false, "unknown")][InlineData(true, "unknown")]
    [InlineData(false, "null")][InlineData(true, "null")]
    [InlineData(false, "throw")][InlineData(true, "throw")]
    [InlineData(false, "cycle")][InlineData(true, "cycle")]
    [InlineData(false, "false-success")][InlineData(true, "false-success")]
    public async Task VisibilityFirstRefusesCloseAndRetainsExactOriginalCycle(bool acquire, string outcome)
    {
        var (provider, proxy, parent, request) = Create(writer: true);
        var grant = provider.BindDmaGrant(request).Value!;
        if (acquire || outcome == "provider-state") Assert.True(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        var initialPrepare = proxy.DmaPrepareCalls;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var original = owners[parent.LeaseId]!;
        var cycles = (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaVisibilityStates", flags)!.GetValue(provider)!;
        var state = acquire || outcome == "provider-state" ? cycles[grant.GrantId] : null;
        var originalProviderCycle = state?.GetType().GetProperty("ProviderCycle")!.GetValue(state);
        var originalCycle = state?.GetType().GetProperty("NeutralCycle")!.GetValue(state);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.DmaVisibilityOutcome = outcome;
        proxy.BeforeDmaVisibility = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("DMA visibility timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
            if (outcome == "state") state!.GetType().GetProperty("NeutralCycle")!.SetValue(state, new NeutralDmaVisibilityCycle(999));
            if (outcome == "provider-state") state!.GetType().GetProperty("ProviderCycle")!.SetValue(state, new PlatformProviderDmaVisibilityCycle(999));
        };
        bool Run() => acquire ? provider.AcquireDmaGrantVisibility(grant).IsSuccess : provider.PrepareDmaGrantVisibility(grant).IsSuccess;
        var operation = Task.Run(Run);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDmaGrant(grant)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
            Assert.False(provider.AcquireDmaGrantVisibility(grant).IsSuccess);
            Assert.Equal(0, proxy.DmaCloseCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await operation; });
        else Assert.Equal(outcome == "exact", await operation);
        proxy.BeforeDmaVisibility = null;
        if (outcome is "owner" or "state" or "provider-state")
        {
            Assert.False(Run());
            owners[parent.LeaseId] = original;
            if (outcome == "state") state!.GetType().GetProperty("NeutralCycle")!.SetValue(state, originalCycle);
            if (outcome == "provider-state") state!.GetType().GetProperty("ProviderCycle")!.SetValue(state, originalProviderCycle);
            Assert.True(Run());
        }
        else if (outcome != "exact") Assert.False(Run());
        Assert.Equal(acquire ? initialPrepare : initialPrepare + 1, proxy.DmaPrepareCalls);
        Assert.Equal(acquire ? 1 : 0, proxy.DmaAcquireCalls);
        Assert.True(provider.RevokeDmaGrant(grant).IsSuccess);
        Assert.True(provider.BeginRegionMappingRevocation(request.MappingLease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.RevokeDevice(request.DeviceLease).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData(0UL)][InlineData(ulong.MaxValue)][InlineData(ulong.MaxValue - 1)]
    public void PrepareReservesCycleBeforeNeutralEffect(ulong capacity)
    {
        var (provider, proxy, _, request) = Create(writer: true);
        var grant = provider.BindDmaGrant(request).Value!;
        provider.GetType().GetField("_nextProviderDmaVisibilityCycle", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, capacity);
        var result = provider.PrepareDmaGrantVisibility(grant);
        Assert.Equal(capacity == ulong.MaxValue - 1, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.DmaPrepareCalls);
        if (result.IsSuccess) Assert.Equal(capacity, result.Value!.Cycle.Value);
        Assert.True(provider.RevokeDmaGrant(grant).IsSuccess);
    }
    [Theory]
    [InlineData("exact")]
    [InlineData("owner")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("revoked")]
    [InlineData("not-found")]
    [InlineData("absent-device")]
    [InlineData("absent-mapping")]
    [InlineData("absent-grant")]
    public async Task OriginalDmaCloseInterlocksSourcesAndRetainsOutcomeBeforePublication(string outcome)
    {
        var (provider, proxy, parent, request) = Create();
        var grant = provider.BindDmaGrant(request).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var original = owners[parent.LeaseId]!;
        var sourceOwners = outcome switch
        {
            "absent-device" => (global::System.Collections.IDictionary)provider.GetType().GetField("_deviceLeases", flags)!.GetValue(provider)!,
            "absent-mapping" => (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", flags)!.GetValue(provider)!,
            "absent-grant" => (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaGrants", flags)!.GetValue(provider)!,
            _ => owners
        };
        object sourceKey = outcome switch
        {
            "absent-device" => request.DeviceLease.LeaseId,
            "absent-mapping" => request.MappingLease.MappingId,
            "absent-grant" => grant.GrantId,
            _ => parent.LeaseId
        };
        var originalSource = sourceOwners[sourceKey]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.DmaCloseOutcome = outcome;
        proxy.BeforeDmaClose = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("DMA close timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
            if (outcome.StartsWith("absent-")) sourceOwners.Remove(sourceKey);
        };
        var closing = Task.Run(() => provider.RevokeDmaGrant(grant));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDmaGrant(grant)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
            Assert.Equal(0, proxy.DmaPrepareCalls);
            Assert.False(provider.RevokeDevice(request.DeviceLease).IsSuccess);
            Assert.False(provider.BeginRegionMappingRevocation(request.MappingLease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            Assert.Equal(1, proxy.DmaCloseCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await closing; });
        else Assert.Equal(outcome == "exact", (await closing).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveDmaGrantCount);
        proxy.BeforeDmaClose = null;
        if (outcome == "owner" || outcome.StartsWith("absent-"))
        {
            Assert.False(provider.RevokeDmaGrant(grant).IsSuccess);
            owners[parent.LeaseId] = original;
            sourceOwners[sourceKey] = originalSource;
        }
        Assert.Equal(outcome == "owner" || outcome.StartsWith("absent-"), provider.RevokeDmaGrant(grant).IsSuccess);
        Assert.Equal(1, proxy.DmaCloseCalls);
        Assert.False(provider.PrepareDmaGrantVisibility(grant).IsSuccess);
        Assert.Equal(0, proxy.DmaPrepareCalls);
        var closed = outcome is "exact" or "owner" || outcome.StartsWith("absent-");
        Assert.Equal(closed, provider.BeginRegionMappingRevocation(request.MappingLease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.Equal(closed, provider.RevokeDevice(request.DeviceLease).IsSuccess);
        Assert.Equal(closed, provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(ulong.MaxValue - 1)]
    public void DmaIdentityCapacityRefusesBeforeEffect(ulong capacity)
    {
        var (provider, proxy, parent, request) = Create();
        provider.GetType().GetField("_nextProviderDmaGrantId", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, capacity);
        var result = provider.BindDmaGrant(request);
        Assert.Equal(capacity == ulong.MaxValue - 1, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.DmaCalls);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.Runtime.ActiveDmaGrantCount);
        if (result.IsSuccess)
        {
            Assert.Equal(capacity, result.Value!.GrantId.Value);
            Assert.True(provider.RevokeDmaGrant(result.Value).IsSuccess);
        }
        Assert.True(provider.BeginRegionMappingRevocation(request.MappingLease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.RevokeDevice(request.DeviceLease).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData("exact")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("owner")]
    public async Task DeviceCloseFirstRefusesDmaBeforeCallbackAndRetainsOriginalClose(string outcome)
    {
        var (provider, proxy, parent, request) = Create();
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var original = owners[parent.LeaseId]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.DeviceCloseOutcome = outcome;
        proxy.BeforeDeviceClose = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Device callback timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        var closing = Task.Run(() => provider.RevokeDevice(request.DeviceLease));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.BindDmaGrant(request)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(0, proxy.DmaCalls);
            Assert.False(provider.MapMmio(request.DeviceLease, new("mmio-close-first", 4096), new(0, 64), PlatformMmioAccess.Read).IsSuccess);
            Assert.Equal(0, proxy.MmioMapCalls);
            Assert.False(provider.BindInterrupt(request.DeviceLease, new("irq-close-first", PlatformInterruptTrigger.Edge)).IsSuccess);
            Assert.Equal(0, proxy.IrqBindCalls);
            Assert.False(provider.RevokeDevice(request.DeviceLease).IsSuccess);
            Assert.Equal(1, proxy.DeviceCloseCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await closing; });
        else Assert.Equal(outcome == "exact", (await closing).IsSuccess);
        proxy.BeforeDeviceClose = null;
        if (outcome == "owner")
        {
            Assert.False(provider.RevokeDevice(request.DeviceLease).IsSuccess);
            owners[parent.LeaseId] = original;
        }
        Assert.Equal(outcome == "owner", provider.RevokeDevice(request.DeviceLease).IsSuccess);
        Assert.Equal(1, proxy.DeviceCloseCalls);
        Assert.False(provider.BindDmaGrant(request).IsSuccess);
        Assert.Equal(0, proxy.DmaCalls);
    }
    [Theory]
    [InlineData("exact")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("handle")]
    [InlineData("epoch")]
    [InlineData("false-denial")]
    [InlineData("owner")]
    public async Task OriginalDmaAdmissionPinsBothSourcesAndRefusesConcurrentClose(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        var device = provider.BindDevice(parent, new("dma-test"), PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        var mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        var request = new PlatformDmaGrantRequest(device, mapping.Lease, mapping.Slice, new(0, 4096), PlatformDmaDirection.DeviceReadsMemory);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var original = owners[parent.LeaseId]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.DmaOutcome = outcome;
        proxy.BeforeDma = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("DMA callback timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        var binding = Task.Run(() => provider.BindDmaGrant(request));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDevice(device)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.BeginRegionMappingRevocation(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            Assert.False(provider.BindDmaGrant(request).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(1, proxy.DmaCalls);
            Assert.Equal(0, proxy.DmaCloseCalls);
        }
        finally { release.Set(); }
        PlatformAuthorityResult<PlatformProviderDmaGrant> result = default!;
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await binding; });
        else { result = await binding; Assert.Equal(outcome == "exact", result.IsSuccess); }
        if (outcome == "owner") owners[parent.LeaseId] = original;
        proxy.BeforeDma = null;
        Assert.Equal(1, proxy.Runtime.ActiveDmaGrantCount);
        if (outcome == "exact") Assert.True(provider.RevokeDmaGrant(result.Value!).IsSuccess);
        else
        {
            Assert.False(provider.BindDmaGrant(request).IsSuccess);
            Assert.Equal(1, proxy.DmaCalls);
            Assert.Equal(0, proxy.DmaCloseCalls);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.BeginRegionMappingRevocation(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            return;
        }
        Assert.True(provider.BeginRegionMappingRevocation(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
}
