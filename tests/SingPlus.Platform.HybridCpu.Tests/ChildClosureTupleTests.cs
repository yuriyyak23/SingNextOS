using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed partial class ChildClosureTupleTests
{
    internal static object ReplacementSourceOwner(object original)
    {
        Type type = original.GetType();
        object? lease = (type.GetProperty("Lease") ?? type.GetProperty("Mapping"))!.GetValue(original);
        object? native = type.GetProperty("HybridCpuLease")!.GetValue(original);
        object?[] arguments = type.GetProperty("OriginalAdmissionRequired") is { } admission
            ? [lease, native, admission.GetValue(original)] : [lease, native];
        return Activator.CreateInstance(type, arguments)!;
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void OriginalChildOrGuestClosureCannotTransferToSameTupleOwner(bool guest)
    {
        ClosureProvider runtime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformProviderGuestRegionMappingLease mapping = default!;
        if (guest)
        {
            PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
                new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
            PlatformProviderOwnedRegionMapping backing = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
            mapping = provider.MapGuestRegion(new(child, backing, new(0, 512), PlatformGuestMemoryAccess.Read)).Value!;
        }
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)provider.GetType().GetField(guest ? "_providerGuestMappings" : "_providerChildren", flags)!.GetValue(provider)!;
        object key = guest ? mapping.LeaseId : child.LeaseId;
        object original = routes[key]!;
        object foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Action replace = () => routes[key] = foreign;
        if (guest) runtime.BeforeUnmap = replace; else runtime.BeforeClose = replace;
        bool Close() => guest ? provider.UnmapGuestRegion(mapping).IsSuccess : provider.CloseChildDomain(child).IsSuccess;
        Assert.False(Close());
        Assert.NotNull(original.GetType().GetProperty("ClosureReceipt")!.GetValue(original));
        Assert.Null(foreign.GetType().GetProperty("ClosureReceipt")!.GetValue(foreign));
        Assert.False(Close());
        Assert.Equal(1, guest ? runtime.UnmapCalls : runtime.CloseCalls);
        runtime.BeforeClose = null; runtime.BeforeUnmap = null;
        routes[key] = original;
        Assert.True(Close());
        Assert.Equal(1, guest ? runtime.UnmapCalls : runtime.CloseCalls);
    }

    [Fact]
    public void MissingChildRouteRetainsOriginalHistoryEvenAfterChildClosure()
    {
        INeutralDomainRuntime neutral = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, RootCloseRuntimeProxy>();
        RootCloseRuntimeProxy proxy = (RootCloseRuntimeProxy)(object)neutral;
        ClosureProvider runtime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(neutral, runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerChildren", flags)!.GetValue(provider)!;
        object original = routes[child.LeaseId]!;
        routes.Remove(child.LeaseId);
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.CloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
        routes[child.LeaseId] = original;
        Assert.True(provider.CloseChildDomain(child).IsSuccess);
        Assert.Equal(1, runtime.CloseCalls);
        routes[child.LeaseId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
        routes.Remove(child.LeaseId);
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.CloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
        // A consumed Close does not replace the original Create-history owner.
        // Restoring that exact route is a warm positive control, not cold recovery.
        routes[child.LeaseId] = original;
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(1, runtime.CloseCalls);
        Assert.Equal(1, proxy.CloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Theory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(true, true)]
    public void ForeignArtifactRouteCannotRepeatOriginalStart(bool afterEffect, bool lostReply)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ClosureProvider? runtime, PlatformProviderDomainLease parent, PlatformProviderChildDomainLease child, PlatformProviderGuestRegionMappingLease mapping, PlatformExecutableArtifactReceipt artifact) = CreateOriginalArtifact();
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerArtifacts", flags)!.GetValue(provider)!;
        object original = routes[artifact.ArtifactId]!;
        PlatformChildExecutionStartRequest request = new PlatformChildExecutionStartRequest(child, artifact, new(1), new(1));
        if (afterEffect)
        {
            runtime.BeforeStart = () => routes.Remove(artifact.ArtifactId);
            runtime.StartFault = lostReply ? "throw" : "exact";
            if (lostReply) Assert.Throws<IOException>(() => provider.StartExecutableArtifact(request));
            else Assert.False(provider.StartExecutableArtifact(request).IsSuccess);
            Assert.NotNull(original.GetType().GetProperty("OriginalExecution")!.GetValue(original));
            Assert.False((bool)original.GetType().GetProperty("ExecutionInFlight")!.GetValue(original)!);
        }
        runtime.BeforeStart = null;
        runtime.StartFault = "exact";
        object foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        foreign.GetType().GetProperty("OriginalExecution")!.SetValue(foreign, null);
        routes[artifact.ArtifactId] = foreign;
        Assert.False(provider.StartExecutableArtifact(request).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, runtime.StartCalls);
        Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
        Assert.Equal(0, runtime.UnmapCalls);
        routes[artifact.ArtifactId] = original;
        Assert.Equal(!afterEffect, provider.StartExecutableArtifact(request).IsSuccess);
        Assert.Equal(1, runtime.StartCalls);
        Assert.False(provider.CloseChildDomain(child).IsSuccess);
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
    }

    [Fact]
    public void LostArtifactRouteCannotAuthorizeOriginalGuestUnmap()
    {
        (HybridCpuPlatformAuthorityProvider? provider, ClosureProvider? runtime, PlatformProviderDomainLease parent, PlatformProviderChildDomainLease child, PlatformProviderGuestRegionMappingLease mapping, PlatformExecutableArtifactReceipt artifact) = CreateOriginalArtifact();
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerArtifacts", flags)!.GetValue(provider)!;
        object original = routes[artifact.ArtifactId]!;
        routes.Remove(artifact.ArtifactId);
        Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
        Assert.Equal(0, runtime.UnmapCalls);
        Assert.False(provider.CloseChildDomain(child).IsSuccess);
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
        routes[artifact.ArtifactId] = original;
        Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
        Assert.Equal(0, runtime.UnmapCalls);
    }

    private static (HybridCpuPlatformAuthorityProvider Provider, ClosureProvider Runtime,
        PlatformProviderDomainLease Parent, PlatformProviderChildDomainLease Child,
        PlatformProviderGuestRegionMappingLease Mapping, PlatformExecutableArtifactReceipt Artifact) CreateOriginalArtifact()
    {
        ClosureProvider runtime = new ClosureProvider { StartFault = "exact" };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child, source, new(0, 4096), PlatformGuestMemoryAccess.Read)).Value!;
        PlatformExecutableArtifactReceipt artifact = provider.BindExecutableArtifact(new(child, mapping, new byte[] { 1 }, 10)).Value!;
        return (provider, runtime, parent, child, mapping, artifact);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("owner")]
    [InlineData("revoked")]
    [InlineData("unknown")]
    [InlineData("lease")]
    [InlineData("outcome")]
    [InlineData("wrong-semantics")]
    [InlineData("null")]
    [InlineData("throw")]
    public async Task VisibilityInterlocksCloseAndRequiresOriginalSourcePublication(string outcome)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, RootCloseRuntimeProxy>();
        RootCloseRuntimeProxy proxy = (RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping mapped = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformRegionVisibilityRequest request = new PlatformRegionVisibilityRequest(mapped.Lease, mapped.Slice,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object original = owners[parent.LeaseId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.VisibilityOutcome = outcome;
        proxy.BeforeVisibility = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Visibility timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        Task<PlatformAuthorityResult<PlatformRegionVisibilityResult>> preparing = Task.Run(() => provider.PrepareRegionMappingForConsumer(request));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.BeginRegionMappingRevocation(mapped.Lease,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.False(provider.PrepareRegionMappingForConsumer(request).IsSuccess);
            Assert.Equal(1, proxy.VisibilityCalls);
            Assert.Equal(0, proxy.MappingCloseCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await preparing; });
        else Assert.Equal(outcome == "exact", (await preparing).IsSuccess);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
        proxy.BeforeVisibility = null;
        if (outcome == "owner")
        {
            Assert.False(provider.PrepareRegionMappingForConsumer(request).IsSuccess);
            owners[parent.LeaseId] = original;
            Assert.True(provider.PrepareRegionMappingForConsumer(request).IsSuccess);
            Assert.Equal(1, proxy.VisibilityCalls);
        }
        else if (outcome != "exact")
        {
            Assert.False(provider.PrepareRegionMappingForConsumer(request).IsSuccess);
            Assert.Equal(1, proxy.VisibilityCalls);
        }
        Assert.True(provider.BeginRegionMappingRevocation(mapped.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.Equal(1, proxy.MappingCloseCalls);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(ulong.MaxValue - 1)]
    public void MappingCloseReservesOperationCapacityBeforeEffect(ulong capacity)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, RootCloseRuntimeProxy>();
        RootCloseRuntimeProxy proxy = (RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderRegionMappingLease mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!.Lease;
        provider.GetType().GetField("_nextProviderOperationId", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, capacity);
        PlatformAuthorityResult<PlatformRegionRevocationTicket> result = provider.BeginRegionMappingRevocation(mapping, PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        Assert.Equal(capacity == ulong.MaxValue - 1, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.MappingCloseCalls);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 0 : 1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(capacity == ulong.MaxValue - 1, provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("null")]
    [InlineData("owner")]
    public async Task MappingCloseRetainsOriginalOutcomeAndInterlocksRootPublication(string outcome)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, RootCloseRuntimeProxy>();
        RootCloseRuntimeProxy proxy = (RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderRegionMappingLease mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!.Lease;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object original = owners[parent.LeaseId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.MappingCloseOutcome = outcome;
        proxy.BeforeMappingClose = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Mapping close timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        Task<PlatformAuthorityResult<PlatformRegionRevocationTicket>> closing = Task.Run(() => provider.BeginRegionMappingRevocation(mapping, PlatformRegionRevocationPolicy.DrainBeforeRevoke));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDomain(parent)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.BeginRegionMappingRevocation(mapping, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            Assert.Equal(1, proxy.MappingCloseCalls);
            Assert.Equal(0, proxy.CloseCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await closing; });
        else Assert.Equal(outcome == "exact", (await closing).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveOwnedRegionMappingCount);
        proxy.BeforeMappingClose = null;
        if (outcome == "owner")
        {
            Assert.False(provider.BeginRegionMappingRevocation(mapping, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            owners[parent.LeaseId] = original;
        }
        PlatformAuthorityResult<PlatformRegionRevocationTicket> retry = provider.BeginRegionMappingRevocation(mapping, PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        Assert.Equal(outcome == "owner", retry.IsSuccess);
        Assert.Equal(1, proxy.MappingCloseCalls);
        Assert.Equal(outcome is "owner" or "exact", provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("handle")]
    [InlineData("epoch")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("null")]
    [InlineData("owner")]
    [InlineData("false-range")]
    public async Task OwnedMapInterlocksRootCloseAndPinsOriginalOutcomeBeforePublication(string outcome)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, RootCloseRuntimeProxy>();
        RootCloseRuntimeProxy proxy = (RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformRegionSlice slice = new PlatformRegionSlice(region, 0, 4096, PlatformMemoryAccess.Read);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object original = owners[parent.LeaseId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.MapOutcome = outcome;
        proxy.BeforeMap = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Map test timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        Task<PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>> mapping = Task.Run(() => provider.MapOwnedRegionSlice(parent, slice));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDomain(parent)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(0, proxy.CloseCalls);
            Assert.False(provider.MapOwnedRegionSlice(parent, slice).IsSuccess);
            Assert.Equal(1, proxy.MapCalls);
        }
        finally { release.Set(); }
        PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> result = default!;
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await mapping; });
        else { result = await mapping; Assert.Equal(outcome == "exact", result.IsSuccess); }
        if (outcome == "owner") owners[parent.LeaseId] = original;
        Assert.False((bool)original.GetType().GetProperty("OwnedMapInFlight")!.GetValue(original)!);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        if (outcome == "exact")
        {
            Assert.True(provider.RevokeRegionMapping(result.Value!.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            Assert.True(provider.RevokeDomain(parent).IsSuccess);
        }
        else
        {
            object failure = original.GetType().GetProperty("FailedOwnedMap")!.GetValue(original)!;
            Assert.NotNull(failure);
            Assert.Equal(parent, failure.GetType().GetProperty("Domain")!.GetValue(failure));
            Assert.Equal(slice, failure.GetType().GetProperty("Slice")!.GetValue(failure));
            NeutralOwnedRegionMapResult? observation = (NeutralOwnedRegionMapResult?)failure.GetType().GetProperty("Observation")!.GetValue(failure);
            Assert.Equal(outcome is "throw" or "null", observation is null);
            if (outcome == "owner") Assert.NotEqual(0UL, observation!.Lease.Handle.Value);
            Assert.False(provider.MapOwnedRegionSlice(parent, slice).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(1, proxy.MapCalls);
            Assert.Equal(0, proxy.CloseCalls);
            Assert.Equal(0, proxy.MappingCloseCalls);
        }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(ulong.MaxValue - 1)]
    public void OwnedMapReservesCapacityBeforeNeutralEffect(ulong capacity)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, RootCloseRuntimeProxy>();
        RootCloseRuntimeProxy proxy = (RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        provider.GetType().GetField("_nextProviderMappingId", flags)!.SetValue(provider, capacity);
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> result = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read));
        Assert.Equal(capacity == ulong.MaxValue - 1, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.MapCalls);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.Runtime.ActiveOwnedRegionMappingCount);
        if (result.IsSuccess) Assert.Equal(capacity, result.Value!.Lease.MappingId.Value);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("owner")]
    [InlineData("null")]
    public async Task RootCloseInterlocksConcurrentCreateAndRetainsOriginalOutcome(string outcome)
    {
        INeutralDomainRuntime rootRuntime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, RootCloseRuntimeProxy>();
        RootCloseRuntimeProxy proxy = (RootCloseRuntimeProxy)(object)rootRuntime;
        ClosureProvider children = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(rootRuntime, children);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle;
        PlatformChildDomainIntent intent = new PlatformChildDomainIntent(new(1, 4096), new(rights, rights));
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object original = owners[parent.LeaseId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.Outcome = outcome;
        proxy.BeforeClose = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Root test callback timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        Task<PlatformAuthorityResult> closing = Task.Run(() => provider.RevokeDomain(parent));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            PlatformAuthorityResult<PlatformProviderChildDomainLease> admission = await Task.Run(() => provider.CreateChildDomain(parent, intent)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(admission.IsSuccess);
            Assert.Equal(0, children.CreateCalls);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(1, proxy.CloseCalls);
            PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
                new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
            Assert.False(provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).IsSuccess);
            Assert.Equal(0, proxy.MapCalls);
            Assert.False(provider.BindDevice(parent, new("device-root-first"), PlatformDeviceRights.Read).IsSuccess);
            Assert.Equal(0, proxy.DeviceBindCalls);
            Assert.False(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Start).IsSuccess);
            Assert.Equal(0, proxy.RootTransitionCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await closing; });
        else Assert.Equal(outcome == "exact", (await closing).IsSuccess);
        if (outcome == "owner") owners[parent.LeaseId] = original;
        Assert.False((bool)original.GetType().GetProperty("RootCloseInFlight")!.GetValue(original)!);
        proxy.BeforeClose = null;
        PlatformAuthorityResult retry = provider.RevokeDomain(parent);
        Assert.Equal(outcome == "owner", retry.IsSuccess);
        Assert.Equal(1, proxy.CloseCalls);
        Assert.False(provider.CreateChildDomain(parent, intent).IsSuccess);
        Assert.Equal(0, children.CreateCalls);
    }

    public class RootCloseRuntimeProxy : global::System.Reflection.DispatchProxy
    {
        public NeutralDomainRuntimeFacade Runtime { get; } = new();
        public Action? BeforeClose { get; set; }
        public string? Outcome { get; set; }
        public int CloseCalls { get; private set; }
        public int MapCalls { get; private set; }
        public int MappingCloseCalls { get; private set; }
        public Action? BeforeMappingClose { get; set; }
        public string? MappingCloseOutcome { get; set; }
        public Action? BeforeIrqClose { get; set; }
        public string? IrqCloseOutcome { get; set; }
        public int IrqPollCalls { get; private set; }
        public Action? BeforeRootTransition { get; set; }
        public string? RootTransitionOutcome { get; set; }
        public int RootTransitionCalls { get; private set; }
        public Action? BeforeRootBind { get; set; }
        public string? RootBindOutcome { get; set; }
        public int RootBindCalls { get; private set; }
        public Action? BeforeAcquire { get; set; }
        public int AcquireCalls { get; private set; }
        public Action? BeforeIrqPoll { get; set; }
        public string? IrqPollOutcome { get; set; }
        public Action? BeforeIrqComplete { get; set; }
        public string? IrqCompleteOutcome { get; set; }
        public int IrqCompleteCalls { get; private set; }
        public Action? BeforeIrqBind { get; set; }
        public string? IrqBindOutcome { get; set; }
        public int IrqBindCalls { get; private set; }
        public int IrqCloseCalls { get; private set; }
        public Action? BeforeMmioClose { get; set; }
        public string? MmioCloseOutcome { get; set; }
        public Action? BeforeMmioMap { get; set; }
        public string? MmioMapOutcome { get; set; }
        public int MmioMapCalls { get; private set; }
        public int MmioCloseCalls { get; private set; }
        public Action? BeforeDeviceBind { get; set; }
        public string? DeviceBindOutcome { get; set; }
        public int DeviceBindCalls { get; private set; }
        public Action? BeforeDeviceClose { get; set; }
        public string? DeviceCloseOutcome { get; set; }
        public int DeviceCloseCalls { get; private set; }
        public Action? BeforeDmaClose { get; set; }
        public string? DmaCloseOutcome { get; set; }
        public int DmaPrepareCalls { get; private set; }
        public int DmaAcquireCalls { get; private set; }
        public Action? BeforeDmaVisibility { get; set; }
        public string? DmaVisibilityOutcome { get; set; }
        public Action? BeforeDma { get; set; }
        public string? DmaOutcome { get; set; }
        public int DmaCalls { get; private set; }
        public int DmaCloseCalls { get; private set; }
        public Action? BeforeVisibility { get; set; }
        public string? VisibilityOutcome { get; set; }
        public int VisibilityCalls { get; private set; }
        public Action? BeforeMap { get; set; }
        public string? MapOutcome { get; set; }
        protected override object? Invoke(global::System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(INeutralDomainRuntime.CloseOwnedRegionMapping))
            {
                MappingCloseCalls++;
                BeforeMappingClose?.Invoke();
                NeutralOwnedRegionCloseResult result = (NeutralOwnedRegionCloseResult)method.Invoke(Runtime, args)!;
                if (MappingCloseOutcome == "throw") throw new IOException("Original close outcome lost after effect.");
                if (MappingCloseOutcome == "null") return null;
                if (MappingCloseOutcome == "unknown") return new NeutralOwnedRegionCloseResult
                    { Decision = (NeutralOwnedRegionCloseDecision)777, Reason = "Unknown close after effect" };
                return result;
            }
            if (method.Name == nameof(INeutralDomainRuntime.AcquireOwnedRegionVisibility))
            {
                AcquireCalls++;
                BeforeAcquire?.Invoke();
                return method.Invoke(Runtime, args);
            }
            if (method.Name == nameof(INeutralDomainRuntime.Bind))
            {
                RootBindCalls++;
                BeforeRootBind?.Invoke();
                NeutralDomainBindResult result = (NeutralDomainBindResult)method.Invoke(Runtime, args)!;
                if (RootBindOutcome == "throw") throw new IOException("Root bind response lost after effect.");
                return result with
                {
                    IsBound = RootBindOutcome != "false-success" && result.IsBound,
                    Decision = RootBindOutcome == "unknown" ? (NeutralDomainBindDecision)777 : result.Decision,
                    Lease = RootBindOutcome == "epoch" ? result.Lease with { Epoch = new(0) } : result.Lease
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.TransitionExecution))
            {
                RootTransitionCalls++;
                BeforeRootTransition?.Invoke();
                NeutralExecutionTransitionResult result = (NeutralExecutionTransitionResult)method.Invoke(Runtime, args)!;
                if (RootTransitionOutcome == "throw") throw new IOException("Root transition response lost after effect.");
                if (RootTransitionOutcome == "null") return null;
                return result with
                {
                    IsTransitioned = RootTransitionOutcome != "false-success" && result.IsTransitioned,
                    Decision = RootTransitionOutcome == "unknown" ? (NeutralExecutionTransitionDecision)777 :
                        RootTransitionOutcome == "false-decision" ? NeutralExecutionTransitionDecision.InvalidTransition :
                        RootTransitionOutcome == "revoked" ? NeutralExecutionTransitionDecision.Revoked : result.Decision,
                    Lease = RootTransitionOutcome == "lease" ? result.Lease with { Epoch = new(0) } : result.Lease,
                    Transition = RootTransitionOutcome == "transition" ? NeutralExecutionTransition.Park : result.Transition,
                    State = RootTransitionOutcome == "state" ? NeutralExecutionState.Parked : result.State
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.PollInterrupt))
            {
                IrqPollCalls++;
                BeforeIrqPoll?.Invoke();
                NeutralInterruptPollResult result = (NeutralInterruptPollResult)method.Invoke(Runtime, args)!;
                if (IrqPollOutcome == "throw") throw new IOException("IRQ poll response lost.");
                if (IrqPollOutcome == "null") return null;
                return new NeutralInterruptPollResult
                {
                    IsObserved = result.IsObserved,
                    Decision = IrqPollOutcome == "unknown" ? (NeutralInterruptPollDecision)777 :
                        IrqPollOutcome == "false-decision" ? NeutralInterruptPollDecision.NoDelivery : result.Decision,
                    Lease = IrqPollOutcome == "lease" ? result.Lease with { Epoch = new(0) } : result.Lease,
                    DeliveryAvailable = IrqPollOutcome != "availability" && result.DeliveryAvailable,
                    Sequence = IrqPollOutcome == "sequence" ? new(0) : result.Sequence
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.CompleteInterruptDelivery))
            {
                IrqCompleteCalls++;
                BeforeIrqComplete?.Invoke();
                NeutralInterruptCompleteResult result = (NeutralInterruptCompleteResult)method.Invoke(Runtime, args)!;
                if (IrqCompleteOutcome == "throw") throw new IOException("IRQ completion response lost after effect.");
                if (IrqCompleteOutcome == "null") return null;
                if (IrqCompleteOutcome == "unknown") return new NeutralInterruptCompleteResult { Decision = (NeutralInterruptCompleteDecision)777 };
                if (IrqCompleteOutcome == "revoked") return new NeutralInterruptCompleteResult { Decision = NeutralInterruptCompleteDecision.Revoked };
                return result;
            }
            if (method.Name == nameof(INeutralDomainRuntime.CloseInterrupt))
            {
                IrqCloseCalls++;
                BeforeIrqClose?.Invoke();
                NeutralInterruptCloseResult result = (NeutralInterruptCloseResult)method.Invoke(Runtime, args)!;
                if (IrqCloseOutcome == "throw") throw new IOException("IRQ close outcome lost after effect.");
                if (IrqCloseOutcome == "null") return null;
                if (IrqCloseOutcome == "unknown") return new NeutralInterruptCloseResult { Decision = (NeutralInterruptCloseDecision)777 };
                if (IrqCloseOutcome == "revoked") return new NeutralInterruptCloseResult { Decision = NeutralInterruptCloseDecision.Revoked };
                if (IrqCloseOutcome == "not-found") return new NeutralInterruptCloseResult { Decision = NeutralInterruptCloseDecision.NotFound };
                return result;
            }
            if (method.Name == nameof(INeutralDomainRuntime.BindInterrupt))
            {
                IrqBindCalls++;
                BeforeIrqBind?.Invoke();
                NeutralInterruptBindResult result = (NeutralInterruptBindResult)method.Invoke(Runtime, args)!;
                if (IrqBindOutcome == "throw") throw new IOException("IRQ outcome lost after effect.");
                if (IrqBindOutcome == "null") return null;
                return new NeutralInterruptBindResult
                {
                    IsBound = IrqBindOutcome != "false-denial" && result.IsBound,
                    Decision = IrqBindOutcome == "unknown" ? (NeutralInterruptBindDecision)777 :
                        IrqBindOutcome == "false-denial" ? NeutralInterruptBindDecision.InvalidSource : result.Decision,
                    Lease = IrqBindOutcome == "handle" ? result.Lease with { Handle = new(0) } :
                        IrqBindOutcome == "epoch" ? result.Lease with { Epoch = new(0) } : result.Lease
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.CloseMmio))
            {
                MmioCloseCalls++;
                BeforeMmioClose?.Invoke();
                NeutralMmioCloseResult result = (NeutralMmioCloseResult)method.Invoke(Runtime, args)!;
                if (MmioCloseOutcome == "throw") throw new IOException("MMIO close outcome lost after effect.");
                if (MmioCloseOutcome == "null") return null;
                if (MmioCloseOutcome == "unknown") return new NeutralMmioCloseResult { Decision = (NeutralMmioCloseDecision)777 };
                if (MmioCloseOutcome == "revoked") return new NeutralMmioCloseResult { Decision = NeutralMmioCloseDecision.Revoked };
                if (MmioCloseOutcome == "not-found") return new NeutralMmioCloseResult { Decision = NeutralMmioCloseDecision.NotFound };
                return result;
            }
            if (method.Name == nameof(INeutralDomainRuntime.MapMmio))
            {
                MmioMapCalls++;
                BeforeMmioMap?.Invoke();
                NeutralMmioMapResult result = (NeutralMmioMapResult)method.Invoke(Runtime, args)!;
                if (MmioMapOutcome == "throw") throw new IOException("MMIO outcome lost after effect.");
                if (MmioMapOutcome == "null") return null;
                return new NeutralMmioMapResult
                {
                    IsMapped = MmioMapOutcome != "false-denial" && result.IsMapped,
                    Decision = MmioMapOutcome == "unknown" ? (NeutralMmioMapDecision)777 :
                        MmioMapOutcome == "false-denial" ? NeutralMmioMapDecision.InvalidRange : result.Decision,
                    Lease = MmioMapOutcome == "handle" ? result.Lease with { Handle = new(0) } :
                        MmioMapOutcome == "epoch" ? result.Lease with { Epoch = new(0) } : result.Lease
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.BindDevice))
            {
                DeviceBindCalls++;
                BeforeDeviceBind?.Invoke();
                NeutralDeviceBindResult result = (NeutralDeviceBindResult)method.Invoke(Runtime, args)!;
                if (DeviceBindOutcome == "throw") throw new IOException("Device bind outcome lost after effect.");
                if (DeviceBindOutcome == "null") return null;
                return new NeutralDeviceBindResult
                {
                    IsBound = DeviceBindOutcome != "false-denial" && result.IsBound,
                    Decision = DeviceBindOutcome == "unknown" ? (NeutralDeviceBindDecision)777 :
                        DeviceBindOutcome == "false-denial" ? NeutralDeviceBindDecision.InvalidRights : result.Decision,
                    Lease = DeviceBindOutcome == "handle" ? result.Lease with { Handle = new(0) } :
                        DeviceBindOutcome == "epoch" ? result.Lease with { Epoch = new(0) } : result.Lease
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.CloseDevice))
            {
                DeviceCloseCalls++;
                BeforeDeviceClose?.Invoke();
                NeutralDeviceCloseResult result = (NeutralDeviceCloseResult)method.Invoke(Runtime, args)!;
                if (DeviceCloseOutcome == "throw") throw new IOException("Device close outcome lost.");
                if (DeviceCloseOutcome == "null") return null;
                if (DeviceCloseOutcome == "unknown") return new NeutralDeviceCloseResult
                    { Decision = (NeutralDeviceCloseDecision)777, Reason = "Unknown device close" };
                return result;
            }
            if (method.Name is nameof(INeutralDomainRuntime.PrepareDmaVisibility) or nameof(INeutralDomainRuntime.AcquireDmaVisibility))
            {
                bool acquire = method.Name == nameof(INeutralDomainRuntime.AcquireDmaVisibility);
                if (acquire) DmaAcquireCalls++; else DmaPrepareCalls++;
                BeforeDmaVisibility?.Invoke();
                object result = method.Invoke(Runtime, args)!;
                if (DmaVisibilityOutcome == "throw") throw new IOException("Original visibility outcome lost after effect.");
                if (DmaVisibilityOutcome == "null") return null;
                NeutralDmaVisibilityEvidence evidence = acquire ? ((NeutralDmaAcquireResult)result).Evidence : ((NeutralDmaPrepareResult)result).Evidence;
                NeutralDmaVisibilityEvidence altered = new NeutralDmaVisibilityEvidence { GrantHandle = evidence.GrantHandle, GrantEpoch = evidence.GrantEpoch,
                    Direction = evidence.Direction, Cycle = DmaVisibilityOutcome == "cycle" ? new(0) : evidence.Cycle };
                if (acquire) return new NeutralDmaAcquireResult { IsAcquired = DmaVisibilityOutcome != "false-success",
                    Decision = DmaVisibilityOutcome == "unknown" ? (NeutralDmaAcquireDecision)777 : NeutralDmaAcquireDecision.Acquired,
                    Evidence = altered };
                return new NeutralDmaPrepareResult { IsPrepared = DmaVisibilityOutcome != "false-success",
                    Decision = DmaVisibilityOutcome == "unknown" ? (NeutralDmaPrepareDecision)777 : NeutralDmaPrepareDecision.Prepared,
                    Evidence = altered };
            }
            if (method.Name == nameof(INeutralDomainRuntime.CloseDmaGrant))
            {
                DmaCloseCalls++;
                BeforeDmaClose?.Invoke();
                NeutralDmaGrantCloseResult result = (NeutralDmaGrantCloseResult)method.Invoke(Runtime, args)!;
                if (DmaCloseOutcome == "throw") throw new IOException("Original DMA close outcome lost after effect.");
                if (DmaCloseOutcome == "null") return null;
                if (DmaCloseOutcome == "unknown") return new NeutralDmaGrantCloseResult { Decision = (NeutralDmaGrantCloseDecision)777 };
                if (DmaCloseOutcome == "revoked") return new NeutralDmaGrantCloseResult { Decision = NeutralDmaGrantCloseDecision.Revoked };
                if (DmaCloseOutcome == "not-found") return new NeutralDmaGrantCloseResult { Decision = NeutralDmaGrantCloseDecision.NotFound };
                return result;
            }
            if (method.Name == nameof(INeutralDomainRuntime.BindDmaGrant))
            {
                DmaCalls++;
                BeforeDma?.Invoke();
                NeutralDmaGrantResult result = (NeutralDmaGrantResult)method.Invoke(Runtime, args)!;
                if (DmaOutcome == "throw") throw new IOException("Original DMA outcome lost after effect.");
                if (DmaOutcome == "null") return null;
                return new NeutralDmaGrantResult
                {
                    IsGranted = DmaOutcome != "false-denial" && result.IsGranted,
                    Decision = DmaOutcome == "unknown" ? (NeutralDmaGrantDecision)777 :
                        DmaOutcome == "false-denial" ? NeutralDmaGrantDecision.InvalidRange : result.Decision,
                    Grant = DmaOutcome == "handle" ? result.Grant with { Handle = new(0) } :
                        DmaOutcome == "epoch" ? result.Grant with { Epoch = new(0) } : result.Grant,
                    Reason = result.Reason
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.PrepareOwnedRegionVisibility))
            {
                VisibilityCalls++;
                BeforeVisibility?.Invoke();
                NeutralOwnedRegionVisibilityResult result = (NeutralOwnedRegionVisibilityResult)method.Invoke(Runtime, args)!;
                if (VisibilityOutcome == "throw") throw new IOException("Visibility outcome lost.");
                if (VisibilityOutcome == "null") return null;
                return new NeutralOwnedRegionVisibilityResult
                {
                    Decision = VisibilityOutcome == "revoked" ? NeutralOwnedRegionVisibilityDecision.Revoked :
                        VisibilityOutcome == "unknown" ? (NeutralOwnedRegionVisibilityDecision)777 : result.Decision,
                    Lease = VisibilityOutcome == "lease" ? result.Lease with { Epoch = new(999) } : result.Lease,
                    Requirement = result.Requirement,
                    Outcome = VisibilityOutcome == "outcome" ? (NeutralMemoryVisibilityOutcome)777 :
                        VisibilityOutcome == "wrong-semantics" ? NeutralMemoryVisibilityOutcome.Coherent : result.Outcome,
                    Reason = result.Reason
                };
            }
            if (method.Name == nameof(INeutralDomainRuntime.MapOwnedRegion))
            {
                MapCalls++;
                BeforeMap?.Invoke();
                NeutralOwnedRegionMapResult result = (NeutralOwnedRegionMapResult)method.Invoke(Runtime, args)!;
                if (MapOutcome == "throw") throw new IOException("Original mapping outcome lost after effect.");
                if (MapOutcome == "null") return null;
                return new NeutralOwnedRegionMapResult { IsMapped = MapOutcome != "false-range" && result.IsMapped,
                    Decision = MapOutcome == "unknown" ? (NeutralOwnedRegionMapDecision)777 :
                        MapOutcome == "false-range" ? NeutralOwnedRegionMapDecision.InvalidRange : result.Decision,
                    Lease = MapOutcome == "handle" ? result.Lease with { Handle = new(0) } :
                        MapOutcome == "epoch" ? result.Lease with { Epoch = new(0) } : result.Lease, Reason = result.Reason };
            }
            if (method!.Name == nameof(INeutralDomainRuntime.Close))
            {
                CloseCalls++;
                BeforeClose?.Invoke();
                if (Outcome == "throw") throw new IOException("Root closure outcome lost.");
                if (Outcome == "unknown") return new NeutralDomainCloseResult((NeutralDomainCloseDecision)777, "unknown outcome");
                if (Outcome == "null") return null;
            }
            return method.Invoke(Runtime, args);
        }
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("not-invoked")]
    public async Task ConcurrentChildCreateRefusesBeforeCallbackAndRetainsOriginalDisposition(string outcome)
    {
        ClosureProvider runtime = new ClosureProvider { CreateFault = outcome };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle;
        PlatformChildDomainIntent intent = new PlatformChildDomainIntent(new(1, 4096), new(rights, rights));
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        runtime.BeforeCreate = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test callback release timed out.");
        };
        Task<PlatformAuthorityResult<PlatformProviderChildDomainLease>> first = Task.Run(() => provider.CreateChildDomain(parent, intent));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            // A different thread must be able to enter local admission while the callback waits.
            Task<PlatformAuthorityResult<PlatformProviderChildDomainLease>> competing = Task.Run(() => provider.CreateChildDomain(parent, intent));
            PlatformAuthorityResult<PlatformProviderChildDomainLease> refused = await competing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, refused.Status);
            Assert.Equal(1, runtime.CreateCalls);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await first; });
        else Assert.Equal(outcome == "exact", (await first).IsSuccess);
        runtime.BeforeCreate = null;
        runtime.CreateFault = "exact";
        PlatformAuthorityResult<PlatformProviderChildDomainLease> retry = provider.CreateChildDomain(parent, intent);
        Assert.Equal(outcome is "exact" or "not-invoked", retry.IsSuccess);
        Assert.Equal(outcome is "exact" or "not-invoked" ? 2 : 1, runtime.CreateCalls);
    }

    [Theory]
    [InlineData("child")]
    [InlineData("child-epoch")]
    [InlineData("parent")]
    [InlineData("parent-epoch")]
    [InlineData("state")]
    [InlineData("terminal")]
    [InlineData("unknown-status")]
    [InlineData("throw")]
    [InlineData("denied")]
    [InlineData("not-invoked")]
    public void InvalidOriginalClosurePinsWithoutRepeatAndNotInvokedAllowsExactRetry(string fault)
    {
        ClosureProvider childRuntime = new ClosureProvider { Fault = fault };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), childRuntime);
        PlatformAuthorityResult<PlatformProviderDomainLease> parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7)));
        Assert.True(parent.IsSuccess);
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle;
        PlatformAuthorityResult<PlatformProviderChildDomainLease> created = provider.CreateChildDomain(parent.Value!, new(new(1, 4096), new(authority, authority)));
        Assert.True(created.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Denied, provider.RevokeDomain(parent.Value!).Status);
        if (fault == "throw") Assert.Throws<IOException>(() => provider.CloseChildDomain(created.Value!));
        else Assert.False(provider.CloseChildDomain(created.Value!).IsSuccess);
        Assert.Equal(1, childRuntime.CloseCalls);
        PlatformAuthorityResult transition = provider.TransitionChildDomain(created.Value!, PlatformChildDomainTransition.Start);
        Assert.Equal(fault == "not-invoked", transition.IsSuccess);
        Assert.Equal(fault == "not-invoked" ? 1 : 0, childRuntime.TransitionCalls);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary children = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerChildren", flags)!.GetValue(provider)!;
        object owner = Assert.Single(children.Values.Cast<object>());
        object? originalFailure = owner.GetType().GetProperty("FailedClose")!.GetValue(owner);
        Assert.Equal(fault != "not-invoked", originalFailure is not null);
        Assert.False((bool)owner.GetType().GetProperty("CloseInFlight")!.GetValue(owner)!);
        childRuntime.Fault = null;
        PlatformAuthorityResult<PlatformChildDomainClosureReceipt> closed = provider.CloseChildDomain(created.Value!);
        if (fault != "not-invoked")
        {
            Assert.Equal(PlatformAuthorityStatus.Faulted, closed.Status);
            Assert.Equal(1, childRuntime.CloseCalls);
            Assert.Same(originalFailure, owner.GetType().GetProperty("FailedClose")!.GetValue(owner));
            Assert.Null(owner.GetType().GetProperty("ClosureReceipt")!.GetValue(owner));
            Assert.False(provider.CloseChildDomain(created.Value!).IsSuccess);
            Assert.Equal(1, childRuntime.CloseCalls);
            Assert.False(provider.RevokeDomain(parent.Value!).IsSuccess);
            return;
        }
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.True(PlatformChildDomainContract.ValidateClosureReceipt(created.Value!, closed.Value!).IsSuccess);
        Assert.Equal(2, childRuntime.CloseCalls);
        Assert.Same(originalFailure, owner.GetType().GetProperty("FailedClose")!.GetValue(owner));
        Assert.Equal(closed, provider.CloseChildDomain(created.Value!));
        Assert.Equal(2, childRuntime.CloseCalls);
        Assert.False(provider.TransitionChildDomain(created.Value!, PlatformChildDomainTransition.Start).IsSuccess);
        Assert.True(provider.RevokeDomain(parent.Value!).IsSuccess);
        Assert.False(provider.CreateChildDomain(parent.Value!, created.Value!.Intent).IsSuccess);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("mapping-epoch")]
    [InlineData("child")]
    [InlineData("child-epoch")]
    [InlineData("parent")]
    [InlineData("parent-epoch")]
    [InlineData("terminal")]
    [InlineData("unknown-status")]
    [InlineData("throw")]
    [InlineData("denied")]
    [InlineData("not-invoked")]
    public void InvalidUnmapReceiptPinsWithoutRepeatAndNotInvokedAllowsExactCleanup(string fault)
    {
        ClosureProvider childRuntime = new ClosureProvider { Fault = fault };
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime, childRuntime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> parentMapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read));
        Assert.True(parentMapping.IsSuccess);
        PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> mapped = provider.MapGuestRegion(new(child, parentMapping.Value!, new(0, 4096), PlatformGuestMemoryAccess.Read));
        Assert.True(mapped.IsSuccess);
        childRuntime.BeforeUnmap = () =>
        {
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.UnmapGuestRegion(mapped.Value!).Status);
            Assert.Equal(1, childRuntime.UnmapCalls);
        };
        if (fault == "throw") Assert.Throws<IOException>(() => provider.UnmapGuestRegion(mapped.Value!));
        else Assert.False(provider.UnmapGuestRegion(mapped.Value!).IsSuccess);
        Assert.Equal(1, childRuntime.UnmapCalls);
        childRuntime.BeforeUnmap = null;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary facts = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerGuestMappings", flags)!.GetValue(provider)!;
        object fact = Assert.Single(facts.Values.Cast<object>());
        Assert.False((bool)fact.GetType().GetProperty("UnmapInFlight")!.GetValue(fact)!);
        Assert.Equal(fault != "not-invoked", fact.GetType().GetProperty("FailedUnmap")!.GetValue(fact) is not null);
        if (fault != "not-invoked")
        {
            Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
            Assert.Equal(0, childRuntime.TransitionCalls);
        }
        else Assert.True(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
        childRuntime.Fault = null;
        object? originalFailure = fact.GetType().GetProperty("FailedUnmap")!.GetValue(fact);
        PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> exact = provider.UnmapGuestRegion(mapped.Value!);
        if (fault != "not-invoked")
        {
            Assert.Equal(PlatformAuthorityStatus.Faulted, exact.Status);
            Assert.Equal(1, childRuntime.UnmapCalls);
            Assert.Same(fact, Assert.Single(facts.Values.Cast<object>()));
            Assert.Same(originalFailure, fact.GetType().GetProperty("FailedUnmap")!.GetValue(fact));
            Assert.False(provider.UnmapGuestRegion(mapped.Value!).IsSuccess);
            Assert.Equal(1, childRuntime.UnmapCalls);
            Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            return;
        }
        Assert.True(exact.IsSuccess, exact.Message);
        Assert.True(PlatformGuestMemoryContract.ValidateClosureReceipt(mapped.Value!, exact.Value!).IsSuccess);
        Assert.Equal(2, childRuntime.UnmapCalls);
        Assert.Equal(PlatformAuthorityStatus.Stale, provider.UnmapGuestRegion(mapped.Value!).Status);
        Assert.Equal(2, childRuntime.UnmapCalls);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
    }

    [Fact]
    public void AdmittedArtifactPinsMappingBeforeNeutralUnmapUntilProviderReleaseContractExists()
    {
        ClosureProvider childRuntime = new ClosureProvider();
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime, childRuntime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping parentMapping = provider.MapOwnedRegionSlice(parent,
            new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child, parentMapping, new(0, 4096), PlatformGuestMemoryAccess.Read)).Value!;
        PlatformExecutableArtifactRequest request = new PlatformExecutableArtifactRequest(child, mapping, new byte[] { 1 }, 1);
        PlatformAuthorityResult<PlatformExecutableArtifactReceipt> artifact = provider.BindExecutableArtifact(request);
        Assert.True(artifact.IsSuccess, artifact.Message);

        Assert.Equal(PlatformAuthorityStatus.Faulted, provider.UnmapGuestRegion(mapping).Status);
        Assert.Equal(0, childRuntime.UnmapCalls);
        Assert.Equal(1, childRuntime.BindCalls);
        Assert.Equal(0, childRuntime.StartCalls);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);

        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary mappings = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerGuestMappings", flags)!.GetValue(provider)!;
        IDictionary artifacts = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerArtifacts", flags)!.GetValue(provider)!;
        Assert.Single(mappings.Values.Cast<object>());
        object artifactFact = Assert.Single(artifacts.Values.Cast<object>());
        Assert.True((bool)artifactFact.GetType().GetProperty("AdmissionPublished")!.GetValue(artifactFact)!);

        // No release contract exists, so unmap remains refused after an exact Start no-effect result too.
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            provider.StartExecutableArtifact(new(child, artifact.Value!, new(1), new(1))).Status);
        Assert.Equal(1, childRuntime.StartCalls);
        Assert.Equal(PlatformAuthorityStatus.Faulted, provider.UnmapGuestRegion(mapping).Status);
        Assert.Equal(0, childRuntime.UnmapCalls);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.Single(mappings.Values.Cast<object>());
        Assert.Single(artifacts.Values.Cast<object>());
    }

    [Theory]
    [InlineData("child-epoch")]
    [InlineData("mapping-epoch")]
    [InlineData("slice")]
    public void GuestMapRejectsCallerDriftBeforeProviderEffect(string fault)
    {
        ClosureProvider childRuntime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), childRuntime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 2048, PlatformMemoryAccess.Read)).Value!;
        PlatformGuestRegionMappingRequest original = new PlatformGuestRegionMappingRequest(child, mapping, new(0, 512), PlatformGuestMemoryAccess.Read);
        PlatformGuestRegionMappingRequest changed = fault switch
        {
            "child-epoch" => original with { ChildLease = child with { Generation = new(99) } },
            "mapping-epoch" => original with { ParentMapping = mapping with { Lease = mapping.Lease with { Generation = new(99) } } },
            _ => original with { ParentMapping = mapping with { Slice = mapping.Slice with { Offset = 256 } } },
        };
        Assert.True(PlatformGuestMemoryContract.ValidateRequest(changed).IsSuccess);
        Assert.False(provider.MapGuestRegion(changed).IsSuccess);
        Assert.Equal(0, childRuntime.MapCalls);
        PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> accepted = provider.MapGuestRegion(original);
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.Equal(1, childRuntime.MapCalls);
        Assert.True(provider.UnmapGuestRegion(accepted.Value!).IsSuccess);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(ulong.MaxValue - 1)]
    public void GuestMapCapacityRefusalPrecedesEffectAndLastIdentityIsReservedBeforeReentry(ulong capacity)
    {
        ClosureProvider childRuntime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), childRuntime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformGuestRegionMappingRequest request = new PlatformGuestRegionMappingRequest(child, mapping, new(0, 512), PlatformGuestMemoryAccess.Read);
        // Keep a pre-existing exact cleanup fact while testing admission capacity.
        PlatformProviderGuestRegionMappingLease prior = provider.MapGuestRegion(request).Value!;
        FieldInfo counter = typeof(HybridCpuPlatformAuthorityProvider).GetField("_nextProviderGuestId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        counter.SetValue(provider, capacity);
        PlatformAuthorityStatus? nested = null;
        childRuntime.BeforeMap = () =>
        {
            Assert.Equal(ulong.MaxValue, counter.GetValue(provider));
            nested = provider.MapGuestRegion(request).Status;
        };
        PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> result = provider.MapGuestRegion(request);
        childRuntime.BeforeMap = null;
        if (capacity == ulong.MaxValue - 1)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(capacity, result.Value!.LeaseId.Value);
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, nested);
            Assert.Equal(2, childRuntime.MapCalls);
            Assert.True(provider.UnmapGuestRegion(result.Value!).IsSuccess);
        }
        else
        {
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, result.Status);
            Assert.Null(nested);
            Assert.Equal(capacity, counter.GetValue(provider));
            Assert.Equal(1, childRuntime.MapCalls);
        }
        Assert.True(provider.UnmapGuestRegion(prior).IsSuccess);
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("epoch")]
    [InlineData("child")]
    [InlineData("range")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("not-invoked")]
    [InlineData("late-exact")]
    [InlineData("reentry")]
    public void GuestMapOriginalOutcomePinsOrRetainsExactCleanupWithoutPublication(string fault)
    {
        ClosureProvider childRuntime = new ClosureProvider();
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime, childRuntime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformGuestRegionMappingRequest request = new PlatformGuestRegionMappingRequest(child, mapping, new(0, 512), PlatformGuestMemoryAccess.Read);
        childRuntime.MapFault = fault;
        if (fault == "reentry") childRuntime.BeforeMap = () =>
        {
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.MapGuestRegion(request).Status);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
        };
        if (fault == "late-exact") childRuntime.BeforeMap = () =>
        {
            Assert.False(provider.RevokeRegionMapping(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            // Inject external source loss independently of the provider admission interlock.
            BindingFlags ownerFlags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
            IDictionary sources = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", ownerFlags)!.GetValue(provider)!;
            object original = sources[mapping.Lease.MappingId]!;
            NeutralOwnedRegionMappingLease neutral = (NeutralOwnedRegionMappingLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
            Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(neutral).Decision);
            original.GetType().GetProperty("Revoked")!.SetValue(original, true);
            Assert.Equal(PlatformAuthorityStatus.Denied, provider.RevokeDomain(parent).Status);
            Assert.Equal(1, runtime.ActiveBindingCount);
        };
        if (fault == "reentry") Assert.True(provider.MapGuestRegion(request).IsSuccess);
        else if (fault == "throw") Assert.Throws<IOException>(() => provider.MapGuestRegion(request));
        else Assert.False(provider.MapGuestRegion(request).IsSuccess);
        childRuntime.BeforeMap = null;
        childRuntime.MapFault = null;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary facts = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerGuestMappings", flags)!.GetValue(provider)!;
        if (fault == "reentry")
        {
            object fact = Assert.Single(facts.Values.Cast<object>());
            PlatformProviderGuestRegionMappingLease lease = (PlatformProviderGuestRegionMappingLease)fact.GetType().GetProperty("Lease")!.GetValue(fact)!;
            Assert.True(provider.UnmapGuestRegion(lease).IsSuccess);
            Assert.Equal(1, childRuntime.MapCalls);
            Assert.Equal(0, childRuntime.CloseCalls);
        }
        else if (fault == "not-invoked")
        {
            Assert.Empty(facts.Keys.Cast<object>());
            Assert.True(provider.MapGuestRegion(request).IsSuccess);
            Assert.Equal(2, childRuntime.MapCalls);
        }
        else if (fault == "late-exact")
        {
            object fact = Assert.Single(facts.Values.Cast<object>());
            Assert.False((bool)fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact)!);
            PlatformProviderGuestRegionMappingLease lease = (PlatformProviderGuestRegionMappingLease)fact.GetType().GetProperty("Lease")!.GetValue(fact)!;
            Assert.Equal(PlatformAuthorityStatus.Denied, provider.RevokeDomain(parent).Status);
            Assert.Equal(1, runtime.ActiveBindingCount);
            Assert.True(provider.UnmapGuestRegion(lease).IsSuccess);
            Assert.Equal(1, childRuntime.UnmapCalls);
            Assert.False(provider.MapGuestRegion(request).IsSuccess);
            Assert.Equal(1, childRuntime.MapCalls);
            Assert.True(provider.CloseChildDomain(child).IsSuccess);
            Assert.True(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(0, runtime.ActiveBindingCount);
        }
        else
        {
            Assert.Empty(facts.Keys.Cast<object>());
            Assert.False(provider.MapGuestRegion(request).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
            Assert.Equal(1, childRuntime.MapCalls);
            Assert.Equal(0, childRuntime.CloseCalls);
            IDictionary children = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
                .GetField("_providerChildren", flags)!.GetValue(provider)!;
            object owner = Assert.Single(children.Values.Cast<object>());
            Assert.NotNull(owner.GetType().GetProperty("UnclosedGuestMap")!.GetValue(owner));
            Assert.False(provider.RevokeRegionMapping(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
            Assert.Equal(PlatformAuthorityStatus.Denied, provider.RevokeDomain(parent).Status);
            Assert.Equal(1, runtime.ActiveBindingCount);
            PlatformProviderDomainLease independent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(20), new ProcessHandle(new ProcessId(22), 1))).Value!;
            Assert.True(provider.RevokeDomain(independent).IsSuccess);
            Assert.Equal(1, runtime.ActiveBindingCount);
        }
    }

    [Fact]
    public void ChildCloseCallbackReentryCannotRepeatEffectOrCloseRootBeforeExactCommit()
    {
        ClosureProvider childRuntime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), childRuntime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        childRuntime.BeforeClose = () =>
        {
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.CloseChildDomain(child).Status);
            Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
            Assert.Equal(PlatformAuthorityStatus.Denied, provider.RevokeDomain(parent).Status);
            Assert.Equal(1, childRuntime.CloseCalls);
        };
        PlatformAuthorityResult<PlatformChildDomainClosureReceipt> result = provider.CloseChildDomain(child);
        Assert.True(result.IsSuccess);
        Assert.Equal(result, provider.CloseChildDomain(child));
        Assert.Equal(1, childRuntime.CloseCalls);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData("child", 2UL)]
    [InlineData("mapping-epoch", 2UL)]
    [InlineData("mapping-range", 2UL)]
    [InlineData("source-closed", 2UL)]
    [InlineData("capacity", 0UL)]
    [InlineData("capacity", ulong.MaxValue)]
    [InlineData("capacity", ulong.MaxValue - 1)]
    public void ArtifactBindAdmissionRefusesBeforeEffectAndReservesLastUsableIdentity(string fault, ulong capacity)
    {
        ClosureProvider childRuntime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), childRuntime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child, source, new(0, 4096), PlatformGuestMemoryAccess.Read)).Value!;
        PlatformExecutableArtifactRequest original = new PlatformExecutableArtifactRequest(child, mapping, new byte[] { 1 }, 1);
        PlatformAuthorityResult<PlatformExecutableArtifactReceipt> prior = provider.BindExecutableArtifact(original);
        Assert.True(prior.IsSuccess);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        FieldInfo counter = typeof(HybridCpuPlatformAuthorityProvider).GetField("_nextProviderArtifactId", flags)!;
        IDictionary facts = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerArtifacts", flags)!.GetValue(provider)!;
        object? priorFact = facts[prior.Value!.ArtifactId];
        counter.SetValue(provider, capacity);
        PlatformProviderChildDomainLease changedChild = child with { Generation = new(99) };
        PlatformExecutableArtifactRequest request = fault switch
        {
            "child" => original with { ChildLease = changedChild, GuestMapping = mapping with { ChildLease = changedChild } },
            "mapping-epoch" => original with { GuestMapping = mapping with { Generation = new(99) } },
            "mapping-range" => original with { GuestMapping = mapping with { GuestRange = new(0, 2048) } },
            _ => original,
        };
        Assert.True(PlatformChildExecutionContract.ValidateRequest(request).IsSuccess);
        if (fault == "source-closed")
            InjectRevokedOwnedMappingSource(provider, source.Lease);
        PlatformAuthorityStatus? nested = null;
        childRuntime.BeforeBind = () =>
        {
            Assert.Equal(ulong.MaxValue, counter.GetValue(provider));
            nested = provider.BindExecutableArtifact(original).Status;
        };
        PlatformAuthorityResult<PlatformExecutableArtifactReceipt> result = provider.BindExecutableArtifact(request);
        childRuntime.BeforeBind = null;
        bool accepted = fault == "capacity" && capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, result.IsSuccess);
        Assert.Same(priorFact, facts[prior.Value!.ArtifactId]);
        Assert.Equal(accepted ? 2 : 1, childRuntime.BindCalls);
        Assert.Equal(accepted ? ulong.MaxValue : capacity, counter.GetValue(provider));
        if (accepted)
        {
            Assert.Equal(capacity, result.Value!.ArtifactId.Value);
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, nested);
        }
        else
        {
            Assert.Null(nested);
            Assert.Single(facts.Values.Cast<object>());
            if (fault == "capacity") Assert.Equal(PlatformAuthorityStatus.NotAccepted, result.Status);
        }
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("epoch")]
    [InlineData("child")]
    [InlineData("mapping")]
    [InlineData("maximum")]
    [InlineData("digest")]
    [InlineData("null-digest")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("not-invoked")]
    [InlineData("late-exact")]
    [InlineData("caller-mutation")]
    [InlineData("reentry")]
    public void ArtifactOriginalOutcomeIsValidatedPinnedOrRetainedBeforePublication(string fault)
    {
        ClosureProvider childRuntime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), childRuntime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(authority, authority))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child, source, new(0, 4096), PlatformGuestMemoryAccess.Read)).Value!;
        byte[] package = [1];
        PlatformExecutableArtifactRequest request = new PlatformExecutableArtifactRequest(child, mapping, package, 1);
        childRuntime.BindFault = fault;
        childRuntime.BeforeBind = () =>
        {
            if (fault == "late-exact") InjectRevokedOwnedMappingSource(provider, source.Lease);
            if (fault == "caller-mutation") package[0] = 2;
            if (fault == "reentry")
            {
                Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.BindExecutableArtifact(request).Status);
                Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.UnmapGuestRegion(mapping).Status);
                Assert.False(provider.CloseChildDomain(child).IsSuccess);
                Assert.Equal(1, childRuntime.BindCalls);
            }
        };
        PlatformAuthorityResult<PlatformExecutableArtifactReceipt> result = PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "No returned outcome.");
        if (fault == "throw") Assert.Throws<IOException>(() => provider.BindExecutableArtifact(request));
        else result = provider.BindExecutableArtifact(request);
        childRuntime.BeforeBind = null; childRuntime.BindFault = null;
        bool published = fault is "caller-mutation" or "reentry";
        Assert.Equal(published, result.IsSuccess);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary guests = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerGuestMappings", flags)!.GetValue(provider)!;
        object owner = guests[mapping.LeaseId]!;
        Assert.False((bool)owner.GetType().GetProperty("ArtifactBindInFlight")!.GetValue(owner)!);
        IDictionary artifacts = (global::System.Collections.IDictionary)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_providerArtifacts", flags)!.GetValue(provider)!;
        Assert.Equal(published || fault == "late-exact" ? 1 : 0, artifacts.Count);
        if (published)
            Assert.Equal(Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(new byte[] { 1 })), result.Value!.ContentDigest);
        else if (fault == "not-invoked")
        {
            Assert.Null(owner.GetType().GetProperty("FailedArtifactBind")!.GetValue(owner));
            Assert.True(provider.BindExecutableArtifact(request).IsSuccess);
            Assert.Equal(2, childRuntime.BindCalls);
        }
        else
        {
            if (fault == "late-exact")
            {
                object fact = Assert.Single(artifacts.Values.Cast<object>());
                Assert.False((bool)fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact)!);
                PlatformExecutableArtifactReceipt receipt = (PlatformExecutableArtifactReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
                Assert.False(provider.StartExecutableArtifact(new(child, receipt, new(1), new(1))).IsSuccess);
            }
            else Assert.NotNull(owner.GetType().GetProperty("FailedArtifactBind")!.GetValue(owner));
            Assert.False(provider.BindExecutableArtifact(request).IsSuccess);
            Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(1, childRuntime.BindCalls);
            Assert.Equal(0, childRuntime.StartCalls);
            Assert.Equal(0, childRuntime.UnmapCalls);
            Assert.Equal(0, childRuntime.CloseCalls);
        }
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("artifact")]
    [InlineData("child")]
    [InlineData("mapping")]
    [InlineData("parent")]
    [InlineData("digest")]
    [InlineData("operation")]
    [InlineData("generation")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("not-invoked")]
    [InlineData("late-exact")]
    [InlineData("reentry")]
    public void StartValidatesOriginalReceiptBeforePlatformTupleSynthesis(string fault)
    {
        ClosureProvider runtime = new ClosureProvider { StartFault = fault };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child, source, new(0, 4096), PlatformGuestMemoryAccess.Read)).Value!;
        PlatformExecutableArtifactReceipt artifact = provider.BindExecutableArtifact(new(child, mapping, new byte[] { 1 }, 10)).Value!;
        PlatformChildExecutionStartRequest request = new PlatformChildExecutionStartRequest(child, artifact, new(1), new(1));
        runtime.BeforeStart = () =>
        {
            if (fault == "late-exact") InjectRevokedOwnedMappingSource(provider, source.Lease);
            if (fault == "reentry")
            {
                Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.StartExecutableArtifact(request).Status);
                Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.UnmapGuestRegion(mapping).Status);
                Assert.False(provider.CloseChildDomain(child).IsSuccess);
            }
        };
        PlatformAuthorityResult<PlatformChildExecutionReceipt> result = PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted, "not returned");
        if (fault == "throw") Assert.Throws<IOException>(() => provider.StartExecutableArtifact(request));
        else result = provider.StartExecutableArtifact(request);
        Assert.Equal(fault is "exact" or "reentry", result.IsSuccess);
        Assert.Equal(1, runtime.StartCalls);
        if (result.IsSuccess) Assert.True(PlatformChildExecutionContract.ValidateExecution(request, result.Value!).IsSuccess);
        else Assert.Equal(default, result.Value);
        runtime.BeforeStart = null;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary artifacts = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerArtifacts", flags)!.GetValue(provider)!;
        object fact = artifacts[artifact.ArtifactId]!;
        Assert.False((bool)fact.GetType().GetProperty("ExecutionInFlight")!.GetValue(fact)!);
        object retainedStart = fact.GetType().GetProperty("OriginalExecution")!.GetValue(fact)!;
        Assert.NotNull(retainedStart);
        Assert.Equal(fault == "not-invoked", (bool)retainedStart.GetType().GetProperty("NoEffectConsumed")!.GetValue(retainedStart)!);
        Assert.False(provider.StartExecutableArtifact(request).IsSuccess);
        Assert.Equal(fault == "not-invoked" ? 2 : 1, runtime.StartCalls);
        if (fault is not ("not-invoked" or "exact" or "reentry"))
        {
            Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(0, runtime.UnmapCalls);
            Assert.Equal(0, runtime.CloseCalls);
        }
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(ulong.MaxValue, false)]
    [InlineData(ulong.MaxValue - 1, false)]
    [InlineData(ulong.MaxValue - 1, true)]
    public void ChildCapacityRefusesBeforeCallbackAndReservesLastIdentity(ulong capacity, bool refuse)
    {
        ClosureProvider runtime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle;
        PlatformChildDomainIntent intent = new PlatformChildDomainIntent(new(1, 4096), new(rights, rights));
        PlatformProviderChildDomainLease prior = provider.CreateChildDomain(parent, intent).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance;
        FieldInfo counter = provider.GetType().GetField("_nextProviderChildId", flags)!;
        IDictionary facts = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerChildren", flags)!.GetValue(provider)!;
        object? priorFact = facts[prior.LeaseId];
        counter.SetValue(provider, capacity);
        runtime.RefuseCreate = refuse;
        runtime.BeforeCreate = () => Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.CreateChildDomain(parent, intent).Status);
        PlatformAuthorityResult<PlatformProviderChildDomainLease> result = provider.CreateChildDomain(parent, intent);
        Assert.Equal(capacity == ulong.MaxValue - 1 && !refuse, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 2 : 1, runtime.CreateCalls);
        Assert.Same(priorFact, facts[prior.LeaseId]);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? ulong.MaxValue : capacity, counter.GetValue(provider));
        if (result.IsSuccess) Assert.Equal(capacity, result.Value.LeaseId.Value);
        runtime.BeforeCreate = null;
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.CreateChildDomain(parent, intent).Status);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 2 : 1, runtime.CreateCalls);
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("epoch")]
    [InlineData("parent")]
    [InlineData("parent-epoch")]
    [InlineData("intent")]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("not-invoked")]
    [InlineData("exact")]
    [InlineData("late-parent")]
    public void ChildOriginalOutcomeCannotPublishForgedLeaseOrLoseRootPin(string fault)
    {
        ClosureProvider runtime = new ClosureProvider { CreateFault = fault };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle;
        PlatformChildDomainIntent intent = new PlatformChildDomainIntent(new(1, 4096), new(rights, rights));
        runtime.BeforeCreate = () =>
        {
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.CreateChildDomain(parent, intent).Status);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            if (fault == "late-parent")
            {
                BindingFlags ownerFlags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
                IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", ownerFlags)!.GetValue(provider)!;
                object owner = owners[parent.LeaseId]!;
                owner.GetType().GetProperty("ExternalRevocationObserved")!.SetValue(owner, true);
            }
        };
        PlatformAuthorityResult<PlatformProviderChildDomainLease> result = PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "no response");
        if (fault == "throw") Assert.Throws<IOException>(() => provider.CreateChildDomain(parent, intent));
        else result = provider.CreateChildDomain(parent, intent);
        Assert.Equal(fault == "exact", result.IsSuccess);
        Assert.Equal(1, runtime.CreateCalls);
        runtime.BeforeCreate = null;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object fact = domains[parent.LeaseId]!;
        Assert.False((bool)fact.GetType().GetProperty("ChildCreateInFlight")!.GetValue(fact)!);
        Assert.Equal(fault is not ("exact" or "not-invoked"), fact.GetType().GetProperty("FailedChildCreate")!.GetValue(fact) is not null);
        if (fault is not ("exact" or "not-invoked"))
        {
            Assert.Equal(fault == "late-parent" ? PlatformAuthorityStatus.Revoked : PlatformAuthorityStatus.NotAccepted,
                provider.CreateChildDomain(parent, intent).Status);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(1, runtime.CreateCalls);
        }
        if (fault == "not-invoked") Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("denied")]
    [InlineData("not-invoked")]
    [InlineData("exact")]
    [InlineData("late-parent")]
    public void TransitionOriginalFailurePinsChildAndCallbackInterlockPreventsRepeat(string fault)
    {
        ClosureProvider runtime = new ClosureProvider { TransitionFault = fault };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child, source, new(0, 4096), PlatformGuestMemoryAccess.Read)).Value!;
        runtime.BeforeTransition = () =>
        {
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.TransitionChildDomain(child, PlatformChildDomainTransition.Park).Status);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.Equal(0, runtime.CloseCalls);
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.UnmapGuestRegion(mapping).Status);
            Assert.Equal(0, runtime.UnmapCalls);
            if (fault == "late-parent")
            {
                BindingFlags ownerFlags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
                IDictionary domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", ownerFlags)!.GetValue(provider)!;
                object parentOwner = domains[parent.LeaseId]!;
                parentOwner.GetType().GetProperty("ExternalRevocationObserved")!.SetValue(parentOwner, true);
            }
        };
        if (fault == "throw") Assert.Throws<IOException>(() => provider.TransitionChildDomain(child, PlatformChildDomainTransition.Park));
        else Assert.Equal(fault == "exact", provider.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
        Assert.Equal(1, runtime.TransitionCalls);
        runtime.BeforeTransition = null;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary children = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerChildren", flags)!.GetValue(provider)!;
        object owner = children[child.LeaseId]!;
        Assert.False((bool)owner.GetType().GetProperty("TransitionInFlight")!.GetValue(owner)!);
        Assert.Equal(fault is not ("exact" or "not-invoked"), owner.GetType().GetProperty("FailedTransition")!.GetValue(owner) is not null);
        runtime.TransitionFault = "exact";
        PlatformAuthorityResult retry = provider.TransitionChildDomain(child, PlatformChildDomainTransition.Resume);
        Assert.Equal(fault is "exact" or "not-invoked", retry.IsSuccess);
        Assert.Equal(fault is "exact" or "not-invoked" ? 2 : 1, runtime.TransitionCalls);
        if (!retry.IsSuccess)
        {
            object? retained = owner.GetType().GetProperty("FailedTransition")!.GetValue(owner);
            Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
            Assert.Equal(0, runtime.UnmapCalls);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(0, runtime.CloseCalls);
            Assert.Same(retained, owner.GetType().GetProperty("FailedTransition")!.GetValue(owner));
        }
        else Assert.True(provider.UnmapGuestRegion(mapping).IsSuccess);
    }

    [Theory]
    [InlineData(false, "exact")] [InlineData(false, "unknown")]
    [InlineData(false, "throw")] [InlineData(false, "parent-owner")]
    [InlineData(true, "exact")] [InlineData(true, "unknown-status")]
    [InlineData(true, "throw")] [InlineData(true, "parent-owner")]
    public async Task ChildTransitionAndCloseKeepOriginalAdmissionUntilFinalPublication(bool closeFirst, string fault)
    {
        ClosureProvider runtime = new ClosureProvider { TransitionFault = fault, Fault = fault };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        object gate = provider.GetType().GetField("_childCreateCommitGate", flags)!.GetValue(provider)!;
        IDictionary children = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerChildren", flags)!.GetValue(provider)!;
        object originalChild = children[child.LeaseId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Action callback = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            if (fault == "parent-owner")
            {
                IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
                object original = owners[parent.LeaseId]!;
                owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                    original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
            }
        };
        if (closeFirst) runtime.BeforeClose = callback; else runtime.BeforeTransition = callback;
        Task<bool> operation = Task.Run(() => closeFirst ? provider.CloseChildDomain(child).IsSuccess :
            provider.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.TransitionChildDomain(child, PlatformChildDomainTransition.Resume))
                .WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False((await Task.Run(() => provider.CloseChildDomain(child)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(closeFirst ? 0 : 1, runtime.TransitionCalls);
            Assert.Equal(closeFirst ? 1 : 0, runtime.CloseCalls);
        }
        finally { release.Set(); }
        if (fault == "throw") await Assert.ThrowsAsync<IOException>(async () => { await operation; });
        else Assert.Equal(fault == "exact", await operation);
        Assert.False((bool)originalChild.GetType().GetProperty(closeFirst ? "CloseInFlight" : "TransitionInFlight")!.GetValue(originalChild)!);
        runtime.BeforeClose = runtime.BeforeTransition = null;
        if (fault != "exact")
        {
            Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Resume).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.Equal(closeFirst ? 0 : 1, runtime.TransitionCalls);
            Assert.Equal(closeFirst ? 1 : 0, runtime.CloseCalls);
            Assert.NotNull(originalChild.GetType().GetProperty(closeFirst && fault == "parent-owner" ? "ClosureReceipt" :
                closeFirst ? "FailedClose" : "FailedTransition")!.GetValue(originalChild));
        }
        else Assert.True(provider.CloseChildDomain(child).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostCreateParentRefusesTransitionAndCloseBeforeNeutralEffect(bool absent)
    {
        ClosureProvider runtime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object original = owners[parent.LeaseId]!;
        if (absent) owners.Remove(parent.LeaseId);
        else owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
            original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
        Assert.False(provider.CloseChildDomain(child).IsSuccess);
        Assert.Equal(0, runtime.TransitionCalls);
        Assert.Equal(0, runtime.CloseCalls);
        // Restore the original only for positive-control arrangement.
        owners[parent.LeaseId] = original;
        Assert.True(provider.CloseChildDomain(child).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData(false, "exact")] [InlineData(false, "unknown")]
    [InlineData(false, "throw")] [InlineData(false, "child-owner")]
    [InlineData(false, "mapping-owner")] [InlineData(false, "parent-owner")]
    [InlineData(true, "exact")] [InlineData(true, "unknown")]
    public async Task GuestMapAndTransitionReserveOriginalSourcesBeforeEffect(bool transitionFirst, string fault)
    {
        ClosureProvider runtime = new ClosureProvider { MapFault = fault, TransitionFault = fault };
        NeutralDomainRuntimeFacade neutral = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(neutral, runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformGuestRegionMappingRequest request = new PlatformGuestRegionMappingRequest(child, source, new(0, 512), PlatformGuestMemoryAccess.Read);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        object gate = provider.GetType().GetField("_childCreateCommitGate", flags)!.GetValue(provider)!;
        IDictionary children = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerChildren", flags)!.GetValue(provider)!;
        IDictionary mappings = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", flags)!.GetValue(provider)!;
        IDictionary domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object originalChild = children[child.LeaseId]!;
        object originalMapping = mappings[source.Lease.MappingId]!;
        object originalParent = domains[parent.LeaseId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Action callback = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            if (fault == "child-owner") children[child.LeaseId] = Activator.CreateInstance(originalChild.GetType(), child,
                originalChild.GetType().GetProperty("Neutral")!.GetValue(originalChild))!;
            if (fault == "mapping-owner") mappings[source.Lease.MappingId] = Activator.CreateInstance(originalMapping.GetType(), source,
                originalMapping.GetType().GetProperty("HybridCpuLease")!.GetValue(originalMapping))!;
            if (fault == "parent-owner") domains[parent.LeaseId] = Activator.CreateInstance(originalParent.GetType(), parent,
                originalParent.GetType().GetProperty("HybridCpuLease")!.GetValue(originalParent))!;
        };
        if (transitionFirst) runtime.BeforeTransition = callback; else runtime.BeforeMap = callback;
        Task<bool> operation = Task.Run(() => transitionFirst ? provider.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess :
            provider.MapGuestRegion(request).IsSuccess);
        FieldInfo counter = provider.GetType().GetField("_nextProviderGuestId", flags)!;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            object? reserved = counter.GetValue(provider);
            Assert.False((await Task.Run(() => provider.MapGuestRegion(request)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(reserved, counter.GetValue(provider));
            Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Resume).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            if (!transitionFirst)
            {
                Assert.False(provider.BeginRegionMappingRevocation(source.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
                Assert.False(provider.RevokeRegionMapping(source.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
                Assert.Equal(1, neutral.ActiveOwnedRegionMappingCount);
            }
            Assert.Equal(transitionFirst ? 0 : 1, runtime.MapCalls);
            Assert.Equal(transitionFirst ? 1 : 0, runtime.TransitionCalls);
            Assert.Equal(0, runtime.CloseCalls);
        }
        finally { release.Set(); }
        if (fault == "throw") await Assert.ThrowsAsync<IOException>(async () => { await operation; });
        else Assert.Equal(fault == "exact", await operation);
        runtime.BeforeMap = runtime.BeforeTransition = null;
        Assert.False((bool)originalChild.GetType().GetProperty("GuestMapInFlight")!.GetValue(originalChild)!);
        Assert.False((bool)originalMapping.GetType().GetProperty("GuestMapInFlight")!.GetValue(originalMapping)!);
        // Restore only the original source references for the retry/cleanup consequence test.
        children[child.LeaseId] = originalChild;
        mappings[source.Lease.MappingId] = originalMapping;
        domains[parent.LeaseId] = originalParent;
        if (fault != "exact")
        {
            Assert.False(provider.MapGuestRegion(request).IsSuccess);
            Assert.Equal(transitionFirst ? 0 : 1, runtime.MapCalls);
            if (!transitionFirst) Assert.False(provider.RevokeRegionMapping(source.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        }
    }

    [Theory]
    [InlineData("child", false, false)] [InlineData("child", true, false)]
    [InlineData("mapping", false, false)] [InlineData("mapping", true, false)]
    [InlineData("parent", false, false)] [InlineData("parent", true, false)]
    [InlineData("child", false, true)] [InlineData("child", true, true)]
    [InlineData("mapping", false, true)] [InlineData("mapping", true, true)]
    [InlineData("parent", false, true)] [InlineData("parent", true, true)]
    public async Task UnmapRequiresOriginalMapSourcesAndRetainsExactClosureAcrossLateLoss(string sourceKind, bool absent, bool late)
    {
        ClosureProvider runtime = new ClosureProvider();
        NeutralDomainRuntimeFacade neutral = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(neutral, runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformGuestRegionMappingRequest request = new PlatformGuestRegionMappingRequest(child, source, new(0, 512), PlatformGuestMemoryAccess.Read);
        PlatformProviderGuestRegionMappingLease lease = provider.MapGuestRegion(request).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField(sourceKind == "child" ? "_providerChildren" :
            sourceKind == "mapping" ? "_providerMappings" : "_domains", flags)!.GetValue(provider)!;
        object key = sourceKind == "child" ? child.LeaseId : sourceKind == "mapping" ? source.Lease.MappingId : parent.LeaseId;
        object original = owners[key]!;
        IDictionary guests = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerGuestMappings", flags)!.GetValue(provider)!;
        object originalGuest = guests[lease.LeaseId]!;
        void Lose()
        {
            if (absent) owners.Remove(key);
            else owners[key] = Activator.CreateInstance(original.GetType(), sourceKind == "mapping" ? source :
                original.GetType().GetProperty("Lease")!.GetValue(original),
                original.GetType().GetProperty(sourceKind == "child" ? "Neutral" : "HybridCpuLease")!.GetValue(original))!;
        }
        if (late)
        {
            using ManualResetEventSlim entered = new ManualResetEventSlim();
            using ManualResetEventSlim release = new ManualResetEventSlim();
            runtime.BeforeUnmap = () =>
            {
                Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate", flags)!.GetValue(provider)!));
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                Lose();
            };
            Task<PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>> operation = Task.Run(() => provider.UnmapGuestRegion(lease));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.False((await Task.Run(() => provider.UnmapGuestRegion(lease)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
                Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
                Assert.False(provider.MapGuestRegion(request).IsSuccess);
                Assert.False(provider.RevokeRegionMapping(source.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
                Assert.Equal(1, runtime.UnmapCalls);
            }
            finally { release.Set(); }
            Assert.False((await operation).IsSuccess);
            runtime.BeforeUnmap = null;
            Assert.NotNull(originalGuest.GetType().GetProperty("ClosureReceipt")!.GetValue(originalGuest));
        }
        else { Lose(); Assert.False(provider.UnmapGuestRegion(lease).IsSuccess); }
        Assert.False(provider.UnmapGuestRegion(lease).IsSuccess);
        Assert.Equal(late ? 1 : 0, runtime.UnmapCalls);
        Assert.Same(originalGuest, guests[lease.LeaseId]);
        Assert.False((bool)originalGuest.GetType().GetProperty("UnmapInFlight")!.GetValue(originalGuest)!);
        Assert.Equal(1, neutral.ActiveOwnedRegionMappingCount);
        // Restoration arranges exact cleanup; it is not a production recovery route.
        owners[key] = original;
        Assert.True(provider.UnmapGuestRegion(lease).IsSuccess);
        Assert.Equal(1, runtime.UnmapCalls);
        Assert.Empty(guests.Keys.Cast<object>());
        Assert.True(provider.RevokeRegionMapping(source.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.CloseChildDomain(child).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData(false, "child", false)] [InlineData(false, "mapping", false)] [InlineData(false, "parent", false)]
    [InlineData(true, "child", false)] [InlineData(true, "mapping", false)] [InlineData(true, "parent", false)]
    [InlineData(true, "guest", false)]
    [InlineData(false, "child", true)] [InlineData(false, "mapping", true)] [InlineData(false, "parent", true)]
    [InlineData(true, "child", true)] [InlineData(true, "mapping", true)] [InlineData(true, "parent", true)]
    [InlineData(true, "guest", true)] [InlineData(false, "guest", true)]
    public void ArtifactAdmissionKeepsOriginalSourcesBeforeEffectAndFinalPublication(bool start, string sourceKind, bool late)
    {
        ClosureProvider runtime = new ClosureProvider { StartFault = "exact" };
        NeutralDomainRuntimeFacade neutral = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(neutral, runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child, source, new(0, 512), PlatformGuestMemoryAccess.Read)).Value!;
        PlatformExecutableArtifactRequest bind = new PlatformExecutableArtifactRequest(child, mapping, new byte[] { 1 }, 10);
        PlatformExecutableArtifactReceipt artifact = start ? provider.BindExecutableArtifact(bind).Value! : default;
        PlatformChildExecutionStartRequest request = new PlatformChildExecutionStartRequest(child, artifact, new(1), new(1));
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField(sourceKind switch
            { "child" => "_providerChildren", "mapping" => "_providerMappings", "parent" => "_domains", _ => "_providerGuestMappings" }, flags)!.GetValue(provider)!;
        object key = sourceKind switch { "child" => child.LeaseId, "mapping" => source.Lease.MappingId, "parent" => parent.LeaseId, _ => mapping.LeaseId };
        object original = owners[key]!;
        IDictionary guests = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerGuestMappings", flags)!.GetValue(provider)!;
        object originalGuest = guests[mapping.LeaseId]!;
        IDictionary artifacts = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerArtifacts", flags)!.GetValue(provider)!;
        FieldInfo counter = provider.GetType().GetField("_nextProviderArtifactId", flags)!;
        object? before = counter.GetValue(provider);
        void Replace()
        {
            MethodInfo? clone = original.GetType().GetMethod("<Clone>$");
            owners[key] = clone is not null ? clone.Invoke(original, null)! :
                Activator.CreateInstance(original.GetType(), sourceKind == "mapping" ? source :
                    original.GetType().GetProperty("Lease")!.GetValue(original),
                    original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        }
        bool Invoke() => start ? provider.StartExecutableArtifact(request).IsSuccess : provider.BindExecutableArtifact(bind).IsSuccess;
        if (late)
        {
            Action lose = () =>
            {
                Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate", flags)!.GetValue(provider)!));
                Replace();
            };
            if (start) runtime.BeforeStart = lose; else runtime.BeforeBind = lose;
        }
        else Replace();
        Assert.False(Invoke());
        runtime.BeforeStart = null; runtime.BeforeBind = null;
        Assert.False(Invoke());
        Assert.Equal(start ? 1 : late ? 1 : 0, runtime.BindCalls);
        Assert.Equal(start && late ? 1 : 0, runtime.StartCalls);
        Assert.Equal(late && !start ? (ulong)before! + 1 : before, counter.GetValue(provider));
        Assert.False((bool)originalGuest.GetType().GetProperty("ArtifactBindInFlight")!.GetValue(originalGuest)!);
        Assert.Equal(1, neutral.ActiveOwnedRegionMappingCount);
        // Restoration is test arrangement; neither effect nor closure is repeated or inferred.
        owners[key] = original;
        if (late)
        {
            object fact = Assert.Single(artifacts.Values.Cast<object>());
            Assert.Equal(start, (bool)fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact)!);
            if (start)
            {
                Assert.NotNull(fact.GetType().GetProperty("OriginalExecution")!.GetValue(fact));
                Assert.False((bool)fact.GetType().GetProperty("ExecutionPublished")!.GetValue(fact)!);
            }
            Assert.False(Invoke());
            Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
            Assert.Equal(0, runtime.UnmapCalls);
        }
        else Assert.True(Invoke());
    }

    [Theory]
    [InlineData(false, "exact")] [InlineData(false, "unknown")] [InlineData(false, "throw")] [InlineData(false, "not-invoked")]
    [InlineData(true, "exact")] [InlineData(true, "unknown")] [InlineData(true, "throw")] [InlineData(true, "not-invoked")]
    public async Task ArtifactCallbacksKeepAdmissionReservedUntilOutcomeCommit(bool start, string fault)
    {
        ClosureProvider runtime = new ClosureProvider();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        PlatformGuestRegionMappingRequest mapRequest = new PlatformGuestRegionMappingRequest(child, source, new(0, 512), PlatformGuestMemoryAccess.Read);
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(mapRequest).Value!;
        PlatformExecutableArtifactRequest bind = new PlatformExecutableArtifactRequest(child, mapping, new byte[] { 1 }, 10);
        PlatformExecutableArtifactReceipt artifact = provider.BindExecutableArtifact(bind).Value!;
        PlatformChildExecutionStartRequest request = new PlatformChildExecutionStartRequest(child, artifact, new(1), new(1));
        bool Invoke() => start ? provider.StartExecutableArtifact(request).IsSuccess : provider.BindExecutableArtifact(bind).IsSuccess;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Action pause = () =>
        {
            Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate", flags)!.GetValue(provider)!));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        if (start) { runtime.StartFault = fault; runtime.BeforeStart = pause; }
        else { runtime.BindFault = fault; runtime.BeforeBind = pause; }
        Task<bool> operation = Task.Run(Invoke);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(await Task.Run(Invoke).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(provider.StartExecutableArtifact(request).IsSuccess);
            Assert.False(provider.BindExecutableArtifact(bind).IsSuccess);
            Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
            Assert.False(provider.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.False(provider.MapGuestRegion(mapRequest).IsSuccess);
            Assert.Equal(start ? 1 : 2, runtime.BindCalls);
            Assert.Equal(start ? 1 : 0, runtime.StartCalls);
        }
        finally { release.Set(); }
        if (fault == "throw") await Assert.ThrowsAsync<IOException>(async () => { await operation; });
        else Assert.Equal(fault == "exact", await operation);
        runtime.BeforeStart = null; runtime.BeforeBind = null;
        runtime.StartFault = "exact"; runtime.BindFault = null;
        if (fault == "not-invoked") Assert.True(Invoke());
        else if (start || fault != "exact") Assert.False(Invoke());
        Assert.False(provider.UnmapGuestRegion(mapping).IsSuccess);
        Assert.Equal(0, runtime.UnmapCalls);
    }

    private static void InjectRevokedOwnedMappingSource(HybridCpuPlatformAuthorityProvider provider, PlatformProviderRegionMappingLease source)
    {
        Assert.False(provider.RevokeRegionMapping(source, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        // ModelOnly external-source fault; normal admission must preserve the live guest dependency.
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary mappings = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", flags)!.GetValue(provider)!;
        object original = mappings[source.MappingId]!;
        original.GetType().GetProperty("Revoked")!.SetValue(original, true);
    }

    private sealed class ClosureProvider : INeutralChildDomainProvider, INeutralGuestMemoryProvider, INeutralChildExecutionProvider, INeutralVirtualIoProvider
    {
        public int IoBinds { get; private set; }
        public int IoCloses { get; private set; }
        public Action? BeforeIoBind { get; set; }
        public Action? AfterIoClose { get; set; }
        public string? IoFault { get; set; }
        public NeutralVirtualizationResult<NeutralVirtualIoLease> BindVirtualIo(NeutralVirtualIoRequest request)
        {
            IoBinds++;
            BeforeIoBind?.Invoke();
            if (IoFault == "bind-throw") throw new IOException("I/O reply lost");
            return new(NeutralVirtualizationStatus.Success, new(request.ChildLease, request.ParentDeviceLease, request.Profile, new((ulong)IoBinds), new(1)), string.Empty);
        }
        public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseVirtualIo(NeutralVirtualIoLease lease)
        {
            IoCloses++;
            AfterIoClose?.Invoke();
            if (IoFault == "close-throw") throw new IOException("I/O Close reply lost");
            NeutralVirtualIoCloseReceipt receipt = new NeutralVirtualIoCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true);
            if (IoFault == "close-foreign") receipt = receipt with { IoHandle = new(999) };
            return new(NeutralVirtualizationStatus.Success, receipt, string.Empty);
        }
        public string? Fault { get; set; }
        public int CloseCalls { get; private set; }
        public int TransitionCalls { get; private set; }
        public string? TransitionFault { get; set; }
        public Action? BeforeTransition { get; set; }
        public int BindCalls { get; private set; }
        public int StartCalls { get; private set; }
        public string? StartFault { get; set; }
        public Action? BeforeStart { get; set; }
        public string? BindFault { get; set; }
        public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> BindExecutableArtifact(NeutralExecutableArtifactRequest request)
        {
            BindCalls++;
            BeforeBind?.Invoke();
            if (BindFault == "throw") throw new IOException("artifact bind outcome lost");
            NeutralChildDomainLease child = request.ChildLease;
            NeutralGuestMappingLease map = request.GuestMapping;
            NeutralExecutableArtifactReceipt receipt = new NeutralExecutableArtifactReceipt(new((ulong)BindCalls), new(1), child.Handle, child.Epoch, map.Handle, map.Epoch,
                    child.ParentLease.Handle, child.ParentLease.Epoch,
                    Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(request.ImmutablePackage.Span)),
                    request.MaximumExecutionSteps);
            receipt = BindFault switch
            {
                "handle" => receipt with { ArtifactHandle = new(0) },
                "epoch" => receipt with { ArtifactEpoch = new(0) },
                "child" => receipt with { ChildEpoch = new(99) },
                "mapping" => receipt with { MappingEpoch = new(99) },
                "maximum" => receipt with { MaximumExecutionSteps = 99 },
                "digest" => receipt with { ContentDigest = new string('0', 64) },
                "null-digest" => receipt with { ContentDigest = null! },
                _ => receipt,
            };
            return new(BindFault == "unknown" ? (NeutralVirtualizationStatus)int.MaxValue :
                BindFault == "not-invoked" ? NeutralVirtualizationStatus.NotInvoked : NeutralVirtualizationStatus.Success,
                receipt, string.Empty);
        }
        public NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifact(NeutralChildExecutionStartRequest request)
        {
            StartCalls++;
            BeforeStart?.Invoke();
            if (StartFault == "throw") throw new IOException("original execution outcome lost");
            if (StartFault == "not-invoked") return new(NeutralVirtualizationStatus.NotInvoked, default, "pre-effect refusal");
            if (StartFault is not null)
            {
                NeutralExecutableArtifactReceipt a = request.Artifact;
                NeutralChildExecutionReceipt receipt = new NeutralChildExecutionReceipt(a.ArtifactHandle, a.ArtifactEpoch,
                    a.ChildHandle, a.ChildEpoch, a.MappingHandle, a.MappingEpoch, a.ParentHandle, a.ParentEpoch,
                    a.ContentDigest, request.OperationId, request.OperationGeneration, new(1), 1, 1, 0, true);
                receipt = StartFault switch
                {
                    "artifact" => receipt with { ArtifactHandle = new(99) },
                    "child" => receipt with { ChildEpoch = new(99) },
                    "mapping" => receipt with { MappingEpoch = new(99) },
                    "parent" => receipt with { ParentEpoch = new(99) },
                    "digest" => receipt with { ContentDigest = new string('0', 64) },
                    "operation" => receipt with { StartOperationId = new(99) },
                    "generation" => receipt with { StartOperationGeneration = new(99) },
                    _ => receipt,
                };
                return new(StartFault == "unknown" ? (NeutralVirtualizationStatus)int.MaxValue :
                    NeutralVirtualizationStatus.Success, receipt, string.Empty);
            }
            return new(NeutralVirtualizationStatus.NotInvoked, default, "test start refused before effect");
        }
        public int UnmapCalls { get; private set; }
        public int MapCalls { get; private set; }
        public Action? BeforeMap { get; set; }
        public Action? BeforeClose { get; set; }
        public Action? BeforeUnmap { get; set; }
        public Action? BeforeBind { get; set; }
        public string? MapFault { get; set; }
        public NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegion(NeutralGuestMappingRequest request)
        {
            MapCalls++;
            BeforeMap?.Invoke();
            if (MapFault == "throw") throw new IOException("mapping reply lost after possible effect");
            NeutralGuestMappingLease lease = new NeutralGuestMappingLease(request.ChildLease,
                request.ParentMapping, request.GuestRange, request.Access, new((ulong)(60 + MapCalls)), new(5));
            lease = MapFault switch
            {
                "handle" => lease with { Handle = new(0) },
                "epoch" => lease with { Epoch = new(0) },
                "child" => lease with { ChildLease = request.ChildLease with { Epoch = new(99) } },
                "range" => lease with { GuestRange = new(0, 256) },
                _ => lease,
            };
            return new(MapFault == "unknown" ? (NeutralVirtualizationStatus)int.MaxValue :
                MapFault == "not-invoked" ? NeutralVirtualizationStatus.NotInvoked : NeutralVirtualizationStatus.Success,
                lease, string.Empty);
        }
        public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegion(NeutralGuestMappingLease lease)
        {
            UnmapCalls++;
            BeforeUnmap?.Invoke();
            if (Fault == "throw") throw new IOException("original guest unmap outcome lost");
            NeutralGuestMappingCloseReceipt receipt = new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle,
                lease.ChildLease.Epoch, lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true);
            receipt = Fault switch
            {
                "mapping" => receipt with { MappingHandle = new(99) },
                "mapping-epoch" => receipt with { MappingEpoch = new(99) },
                "child" => receipt with { ChildHandle = new(99) },
                "child-epoch" => receipt with { ChildEpoch = new(99) },
                "parent" => receipt with { ParentHandle = new(99) },
                "parent-epoch" => receipt with { ParentEpoch = new(99) },
                "terminal" => receipt with { IsTerminal = false },
                _ => receipt,
            };
            return new(Fault == "denied" ? NeutralVirtualizationStatus.Denied :
                Fault == "not-invoked" ? NeutralVirtualizationStatus.NotInvoked :
                Fault == "unknown-status" ? (NeutralVirtualizationStatus)int.MaxValue :
                NeutralVirtualizationStatus.Success, receipt, string.Empty);
        }
        public int CreateCalls { get; private set; }
        public Action? BeforeCreate { get; set; }
        public bool RefuseCreate { get; set; }
        public string? CreateFault { get; set; }
        public NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(
            NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent)
        {
            CreateCalls++;
            BeforeCreate?.Invoke();
            if (CreateFault == "throw") throw new IOException("create outcome lost");
            NeutralChildDomainLease lease = new NeutralChildDomainLease(parentLease, new(51), new(3), intent);
            lease = CreateFault switch
            {
                "handle" => lease with { Handle = new(0) },
                "epoch" => lease with { Epoch = new(0) },
                "parent" => lease with { ParentLease = parentLease with { Handle = new(99) } },
                "parent-epoch" => lease with { ParentLease = parentLease with { Epoch = new(99) } },
                "intent" => lease with { Intent = intent with { Profile = new(2, 4096) } },
                _ => lease,
            };
            return new(RefuseCreate || CreateFault == "not-invoked" ? NeutralVirtualizationStatus.NotInvoked :
                CreateFault == "unknown" ? (NeutralVirtualizationStatus)int.MaxValue : NeutralVirtualizationStatus.Success,
                lease, string.Empty);
        }
        public NeutralVirtualizationResult TransitionChildDomain(
            NeutralChildDomainLease lease, NeutralChildDomainTransition transition)
        {
            TransitionCalls++;
            BeforeTransition?.Invoke();
            if (TransitionFault == "throw") throw new IOException("transition outcome lost");
            return new(TransitionFault switch
            {
                "unknown" => (NeutralVirtualizationStatus)int.MaxValue,
                "denied" => NeutralVirtualizationStatus.Denied,
                "not-invoked" => NeutralVirtualizationStatus.NotInvoked,
                _ => NeutralVirtualizationStatus.Success,
            }, string.Empty);
        }
        public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease lease)
        {
            CloseCalls++;
            BeforeClose?.Invoke();
            if (Fault == "throw") throw new IOException("original child close outcome lost");
            NeutralChildDomainCloseReceipt receipt = new NeutralChildDomainCloseReceipt(lease.Handle, lease.Epoch,
                lease.ParentLease.Handle, lease.ParentLease.Epoch, NeutralChildDomainState.Closed, true);
            receipt = Fault switch
            {
                "child" => receipt with { ChildHandle = new(99) },
                "child-epoch" => receipt with { ChildEpoch = new(99) },
                "parent" => receipt with { ParentHandle = new(99) },
                "parent-epoch" => receipt with { ParentEpoch = new(99) },
                "state" => receipt with { ResultingState = NeutralChildDomainState.Running },
                "terminal" => receipt with { IsTerminal = false },
                _ => receipt,
            };
            return new(Fault == "denied" ? NeutralVirtualizationStatus.Denied :
                Fault == "not-invoked" ? NeutralVirtualizationStatus.NotInvoked :
                Fault == "unknown-status" ? (NeutralVirtualizationStatus)int.MaxValue :
                NeutralVirtualizationStatus.Success, receipt, string.Empty);
        }
    }
}
