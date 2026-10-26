using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Authority;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void VersionedExternalRuntimePackageExposesAdapterSessionWithoutGrantingAuthority()
    {
        Assembly assembly = typeof(ExternalOperationAdapterSession).Assembly;
        Assert.Equal("HybridCPU_ExternalRuntime", assembly.GetName().Name);
        // Validate the adopted package, rather than a superseded release number.
        string projectDirectory = Path.GetDirectoryName(SourcePath())!;
        using System.Text.Json.JsonDocument packageLock = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectDirectory, "../HybridCpu_ExecutableAdapter/packages.lock.json")));
        string version = packageLock.RootElement.GetProperty("dependencies").GetProperty("net11.0")
            .GetProperty("HybridCPU.ExternalRuntime").GetProperty("resolved").GetString()!;
        Version packageVersion = Version.Parse(version.Split('-')[0]);
        Assert.Equal(new Version(packageVersion.Major, packageVersion.Minor, packageVersion.Build, 0), assembly.GetName().Version);
        Assert.Throws<ArgumentNullException>(() => new ExternalOperationAdapterSession(null!));
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void OperationalChildAdapterPublishesOnlyQualifiedFamilies()
    {
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter();
        NeutralRuntimeFeatureManifest features = adapter.QueryNeutralFeatures();
        Assert.Equal(NeutralRuntimeFeatureAvailability.Executable,
            features.Resolve(NeutralRuntimeFeatureFamily.ChildExecutableArtifact).Availability);
        Assert.Equal(NeutralRuntimeFeatureAvailability.Unavailable,
            features.Resolve(NeutralRuntimeFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal(NeutralRuntimeFeatureAvailability.Unavailable,
            features.Resolve(NeutralRuntimeFeatureFamily.ChildTrapDelivery).Availability);
        Assert.IsNotAssignableFrom<INeutralVirtualTrapProvider>(adapter);
        Assert.False(typeof(INeutralDomainRuntime).IsAssignableFrom(adapter.GetType()));
    }

    [Fact]
    public void NoProviderCompositionCannotBecomeAnExecutableFallback()
    {
        NeutralRuntimeFeatureManifest admissionOnly = new NeutralRuntimeFeatureManifest(
        [
            new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, 1, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        ]);

        Assert.DoesNotContain(admissionOnly.Features,
            feature => feature.Availability == NeutralRuntimeFeatureAvailability.Executable);
        Assert.False(typeof(INeutralDomainRuntime).IsAssignableFrom(typeof(HybridCpuExecutableChildAdapter)));
    }

    [Fact]
    public void VirtualIoWithoutProviderOwnedParentDeviceIdentityNeverCallsExternalBind()
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Io;
        NeutralChildDomainLease child = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority))).Value;
        NeutralDeviceLease device = new NeutralDeviceLease(parent, new("device:test"), NeutralDeviceRights.Read,
            new(7), new(3));

        Assert.Equal(NeutralVirtualizationStatus.Unsupported,
            adapter.BindVirtualIo(new(child, device, new(NeutralDeviceRights.Read, 512))).Status);
        Assert.Equal(0, injector.BindIoCalls);
    }

    [Fact]
    public void RejectedExternalParkQuarantinesChildUntilExplicitClose()
    {
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter();
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution;
        NeutralVirtualizationResult<NeutralChildDomainLease> child = adapter.CreateChildDomain(parent,
            new(new(1, 4096), new(authority, authority)));
        Assert.True(child.IsSuccess, child.Reason);

        NeutralVirtualizationResult park = adapter.TransitionChildDomain(child.Value, NeutralChildDomainTransition.Park);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, park.Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child.Value, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child.Value, NeutralChildDomainTransition.BeginDrain).Status);

        NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> close = adapter.CloseChildDomain(child.Value);
        Assert.True(close.IsSuccess, close.Reason);
        Assert.True(close.Value.IsTerminal);
    }

    [Fact]
    public void ForgedTransitionReceiptCannotAdvanceOrReleaseChild()
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        ((FaultingChildRuntimeProxy)proxy).Inner = runtime;
        ((FaultingChildRuntimeProxy)proxy).ForgeTransition = true;
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralChildDomainLease child = CreateExecutionChild(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
    }

    [Fact]
    public void ThrowingExternalTransitionQuarantinesChildAndCannotRetry()
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ThrowAfterTransition = true;
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralChildDomainLease child = CreateExecutionChild(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
        Assert.Equal(1, injector.TransitionCalls);
    }

    [Fact]
    public void ClosedChildCannotBeResurrectedByLocalDrainOrExternalTransition()
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralChildDomainLease child = CreateExecutionChild(adapter);

        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
        Assert.Equal(0, injector.TransitionCalls);
        Assert.Equal(NeutralVirtualizationStatus.Stale, adapter.CloseChildDomain(child).Status);
    }

    [Fact]
    public void ForgedTerminalCloseReceiptCannotReleaseChild()
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        ((FaultingChildRuntimeProxy)proxy).Inner = runtime;
        ((FaultingChildRuntimeProxy)proxy).ForgeClose = true;
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralChildDomainLease child = CreateExecutionChild(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("forged")]
    [InlineData("throw")]
    public void UnreceiptedExternalChildClosePinsExactChildWithoutRetry(string fault)
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.LoseCloseReceipt = fault == "lost";
        injector.ForgeClose = fault == "forged";
        injector.ThrowAfterClose = fault == "throw";
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralChildDomainLease child = CreateExecutionChild(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(1, injector.CloseCalls);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("forged")]
    [InlineData("throw")]
    public void UnreceiptedParentBindPinsHandleAcrossEpochs(string fault)
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuExternalRuntime proxy = DispatchProxy.Create<IHybridCpuExternalRuntime, FaultingRootRuntimeProxy>();
        FaultingRootRuntimeProxy injector = (FaultingRootRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.LoseBindReceipt = fault == "lost";
        injector.ForgeBind = fault == "forged";
        injector.ThrowAfterBind = fault == "throw";
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(proxy, runtime);
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution;
        NeutralChildDomainIntent intent = new NeutralChildDomainIntent(new(1, 4096), new(authority, authority));

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.CreateChildDomain(parent with { Epoch = new(2) }, intent).Status);
        Assert.Equal(1, injector.BindCalls);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("forged")]
    [InlineData("throw")]
    public void UnreceiptedChildCreatePinsParentAndPreventsSecondCreate(string fault)
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.LoseCreateReceipt = fault == "lost";
        injector.ForgeCreate = fault == "forged";
        injector.ThrowAfterCreate = fault == "throw";
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution;
        NeutralChildDomainIntent intent = new NeutralChildDomainIntent(new(1, 4096), new(authority, authority));

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.CreateChildDomain(parent with { Epoch = new(2) }, intent).Status);
        Assert.Equal(1, injector.CreateCalls);
    }

    [Fact]
    public void SuccessfulParentBindRejectsEpochChangeBeforeExternalRebind()
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuExternalRuntime proxy = DispatchProxy.Create<IHybridCpuExternalRuntime, FaultingRootRuntimeProxy>();
        FaultingRootRuntimeProxy injector = (FaultingRootRuntimeProxy)proxy;
        injector.Inner = runtime;
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(proxy, runtime);
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution;
        NeutralChildDomainIntent intent = new NeutralChildDomainIntent(new(1, 4096), new(authority, authority));

        Assert.True(adapter.CreateChildDomain(parent, intent).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            adapter.CreateChildDomain(parent with { Epoch = new(2) }, intent).Status);
        Assert.Equal(1, injector.BindCalls);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("forged")]
    [InlineData("generation")]
    [InlineData("throw")]
    public void UnreceiptedExternalGuestMapPinsChildBeforeClose(string fault)
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.LoseMapReceipt = fault == "lost";
        injector.ForgeMap = fault == "forged";
        injector.ForgeMapGeneration = fault == "generation";
        injector.ThrowAfterMap = fault == "throw";
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.GuestMemory;
        NeutralChildDomainLease child = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority))).Value;
        NeutralOwnedRegionMappingLease parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        NeutralGuestMappingRequest request = new NeutralGuestMappingRequest(child, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.MapGuestRegion(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.MapGuestRegion(request).Status);
        Assert.Equal(1, injector.MapCalls);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(0, injector.CloseCalls);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("forged")]
    [InlineData("throw")]
    public void UnreceiptedExternalGuestUnmapPinsMappingAndBlocksRetry(string fault)
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.LoseUnmapReceipt = fault == "lost";
        injector.ForgeUnmap = fault == "forged";
        injector.ThrowAfterUnmap = fault == "throw";
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.GuestMemory;
        NeutralChildDomainLease child = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority))).Value;
        NeutralOwnedRegionMappingLease parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        NeutralGuestMappingLease mapping = adapter.MapGuestRegion(new(child, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read)).Value;

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(1, injector.UnmapCalls);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(0, injector.CloseCalls);
    }

    [Fact]
    public void ExactGuestUnmapClosesBeforeChildAndStaleGenerationCannotCallProvider()
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        FaultingChildRuntimeProxy injector = (FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        HybridCpuExecutableChildAdapter adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.GuestMemory;
        NeutralChildDomainLease child = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority))).Value;
        NeutralOwnedRegionMappingLease parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        NeutralGuestMappingLease mapping = adapter.MapGuestRegion(new(child, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read)).Value;

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(0, injector.CloseCalls);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            adapter.UnmapGuestRegion(mapping with { Epoch = new(mapping.Epoch.Value + 1) }).Status);
        Assert.Equal(0, injector.UnmapCalls);
        injector.BeforeUnmapForTest = () =>
        {
            NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> reentry = Task.Run(() => adapter.UnmapGuestRegion(mapping))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.Equal(NeutralVirtualizationStatus.Denied, reentry.Status);
            Assert.Equal(1, injector.UnmapCalls);
        };
        Assert.True(adapter.UnmapGuestRegion(mapping).IsSuccess);
        Assert.Equal(1, injector.UnmapCalls);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Stale, adapter.CloseChildDomain(child).Status);
        Assert.Equal(1, injector.CloseCalls);
    }

    private static NeutralChildDomainLease CreateExecutionChild(HybridCpuExecutableChildAdapter adapter)
    {
        NeutralDomainBindingLease parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution;
        NeutralVirtualizationResult<NeutralChildDomainLease> child = adapter.CreateChildDomain(parent,
            new(new(1, 4096), new(authority, authority)));
        Assert.True(child.IsSuccess, child.Reason);
        return child.Value;
    }

    public class FaultingRootRuntimeProxy : DispatchProxy
    {
        public IHybridCpuExternalRuntime Inner { get; set; } = null!;
        public bool LoseBindReceipt { get; set; }
        public bool ForgeBind { get; set; }
        public bool ThrowAfterBind { get; set; }
        public int BindCalls { get; private set; }
        public Action? BeforeBindForTest { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IHybridCpuExternalRuntime.BindDomain)) BindCalls++;
            if (method.Name == nameof(IHybridCpuExternalRuntime.BindDomain)) BeforeBindForTest?.Invoke();
            object? result = method.Invoke(Inner, args);
            if (method.Name == nameof(IHybridCpuExternalRuntime.BindDomain) && result is ExternalDomainBindResult bind)
            {
                if (ThrowAfterBind) throw new InvalidOperationException("External parent bind receipt was lost.");
                if (LoseBindReceipt)
                    return new ExternalDomainBindResult(ExternalRuntimeOutcome.Unknown, null,
                        "Injected missing parent bind receipt.");
                if (ForgeBind && bind.Receipt is { } receipt)
                    return bind with { Receipt = receipt with { ManifestGeneration = receipt.ManifestGeneration + 1 } };
            }
            return result;
        }
    }

    public class FaultingChildRuntimeProxy : DispatchProxy
    {
        public IHybridCpuChildDomainRuntimeV3 Inner { get; set; } = null!;
        public bool ForgeTransition { get; set; }
        public bool ThrowAfterTransition { get; set; }
        public int TransitionCalls { get; private set; }
        public bool LoseCreateReceipt { get; set; }
        public bool ForgeCreate { get; set; }
        public bool ThrowAfterCreate { get; set; }
        public int CreateCalls { get; private set; }
        public bool ForgeClose { get; set; }
        public bool LoseCloseReceipt { get; set; }
        public bool ThrowAfterClose { get; set; }
        public int? ExactStartWorkForTest { get; set; }
        public bool ForgeStart { get; set; }
        public bool ThrowAfterStart { get; set; }
        public int StartCalls { get; private set; }
        public Action? BeforeStartForTest { get; set; }
        public Action? AfterStartForTest { get; set; }
        public Action? AfterMapForTest { get; set; }
        public Action? AfterArtifactBindForTest { get; set; }
        public Action? BeforeArtifactBindForTest { get; set; }
        public Action? BeforeUnmapForTest { get; set; }
        public Action? BeforeTransitionForTest { get; set; }
        public Action? BeforeCloseForTest { get; set; }
        public Action? BeforeMapForTest { get; set; }
        public Action? BeforeCreateForTest { get; set; }
        public int ArtifactBindCalls { get; private set; }
        public bool ForgeBind { get; set; }
        public bool ForgeBindDigest { get; set; }
        public bool LoseBindReceipt { get; set; }
        public bool ThrowAfterBind { get; set; }
        public bool ForgeMap { get; set; }
        public bool ForgeMapGeneration { get; set; }
        public bool LoseMapReceipt { get; set; }
        public bool ThrowAfterMap { get; set; }
        public bool ForgeUnmap { get; set; }
        public bool LoseUnmapReceipt { get; set; }
        public bool ThrowAfterUnmap { get; set; }
        public int MapCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int BindIoCalls { get; private set; }
        public int UnmapCalls { get; private set; }
        private ExternalChildArtifactBindReceipt? boundArtifact;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IHybridCpuChildDomainRuntimeV3.UnmapChildGuestMemory))
                UnmapCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CreateChildDomain))
                CreateCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.TransitionChildDomain))
                TransitionCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory))
                MapCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CloseChildDomain))
                CloseCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution))
                StartCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact))
                ArtifactBindCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildVirtualIo))
                BindIoCalls++;
            if (method!.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact))
                BeforeArtifactBindForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.UnmapChildGuestMemory))
                BeforeUnmapForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.TransitionChildDomain))
                BeforeTransitionForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CloseChildDomain))
                BeforeCloseForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory))
                BeforeMapForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CreateChildDomain))
                BeforeCreateForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution))
                BeforeStartForTest?.Invoke();
            object? result = method.Invoke(Inner, args);
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact))
                AfterArtifactBindForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory))
                AfterMapForTest?.Invoke();
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution))
                AfterStartForTest?.Invoke();
            if (ThrowAfterTransition && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.TransitionChildDomain))
                throw new InvalidOperationException("External child transition receipt was lost.");
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CreateChildDomain) &&
                result is ExternalChildResult<ExternalChildDomainCreateReceipt> create)
            {
                if (ThrowAfterCreate) throw new InvalidOperationException("External child create receipt was lost.");
                if (LoseCreateReceipt)
                    return new ExternalChildResult<ExternalChildDomainCreateReceipt>(
                        ExternalRuntimeOutcome.Unknown, null, "Injected missing child create receipt.");
                if (ForgeCreate && create.Receipt is { } receipt)
                    return create with { Receipt = receipt with { ManifestGeneration = receipt.ManifestGeneration + 1 } };
            }
            if (ThrowAfterStart && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution))
                throw new InvalidOperationException("External start receipt was lost.");
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory) &&
                result is ExternalChildResult<ExternalGuestMemoryMapReceipt> map)
            {
                if (ThrowAfterMap) throw new InvalidOperationException("External guest map receipt was lost.");
                if (LoseMapReceipt)
                    return new ExternalChildResult<ExternalGuestMemoryMapReceipt>(
                        ExternalRuntimeOutcome.Unknown, null, "Injected missing guest map receipt.");
                if (ForgeMap && map.Receipt is { } receipt)
                    return map with { Receipt = receipt with { ChildOffsetBytes = receipt.ChildOffsetBytes + 1 } };
                if (ForgeMapGeneration && map.Receipt is { } generationReceipt)
                    return map with { Receipt = generationReceipt with { ManifestGeneration = generationReceipt.ManifestGeneration + 1 } };
            }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.UnmapChildGuestMemory) &&
                result is ExternalChildResult<ExternalGuestMemoryUnmapReceipt> unmap)
            {
                if (ThrowAfterUnmap) throw new InvalidOperationException("External guest unmap receipt was lost.");
                if (LoseUnmapReceipt)
                    return new ExternalChildResult<ExternalGuestMemoryUnmapReceipt>(
                        ExternalRuntimeOutcome.Unknown, null, "Injected missing guest unmap receipt.");
                if (ForgeUnmap && unmap.Receipt is { } receipt)
                    return unmap with { Receipt = receipt with { ManifestGeneration = receipt.ManifestGeneration + 1 } };
            }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact) &&
                result is ExternalChildResult<ExternalChildArtifactBindReceipt> bind)
            {
                boundArtifact = bind.Receipt;
                if (ThrowAfterBind) throw new InvalidOperationException("External artifact bind receipt was lost.");
                if (LoseBindReceipt)
                    return new ExternalChildResult<ExternalChildArtifactBindReceipt>(
                        ExternalRuntimeOutcome.Unknown, null, "Injected missing bind receipt.");
                if (ForgeBind && bind.Receipt is { } receipt)
                    return bind with { Receipt = receipt with { MappingEpoch = new(receipt.MappingEpoch.Value + 1) } };
                if (ForgeBindDigest && bind.Receipt is { } digestReceipt)
                    return bind with { Receipt = digestReceipt with { PackageSha256 = new string('a', 64) } };
            }
            if (ExactStartWorkForTest is { } work && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution) &&
                boundArtifact is { } admittedArtifact)
            {
                ExternalOperationIdentity operation = ((ExternalChildExecutionStartRequest)args![1]!).Operation;
                ExternalChildExecutionReceipt receipt = new ExternalChildExecutionReceipt(admittedArtifact.ArtifactHandle,
                    admittedArtifact.ArtifactEpoch, admittedArtifact.ChildHandle, admittedArtifact.ChildEpoch,
                    admittedArtifact.MappingHandle, admittedArtifact.MappingEpoch, admittedArtifact.Parent,
                    admittedArtifact.PackageSha256, new(1), work, 1, 0, operation,
                    admittedArtifact.ContractVersion, admittedArtifact.ManifestGeneration, true);
                return new ExternalChildResult<ExternalChildExecutionReceipt>(ExternalRuntimeOutcome.Succeeded, receipt, string.Empty);
            }
            if (ForgeStart && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution) &&
                boundArtifact is { } artifact)
            {
                ExternalOperationIdentity operation = ((ExternalChildExecutionStartRequest)args![1]!).Operation;
                ExternalChildExecutionReceipt receipt = new ExternalChildExecutionReceipt(artifact.ArtifactHandle,
                    artifact.ArtifactEpoch, artifact.ChildHandle, artifact.ChildEpoch,
                    artifact.MappingHandle, artifact.MappingEpoch, artifact.Parent,
                    artifact.PackageSha256, new(1), 1, 1, 0, operation,
                    artifact.ContractVersion, artifact.ManifestGeneration + 1, true);
                return new ExternalChildResult<ExternalChildExecutionReceipt>(
                    ExternalRuntimeOutcome.Succeeded, receipt, string.Empty);
            }
            if (ForgeTransition && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.TransitionChildDomain))
            {
                ExternalChildDomainLease lease = (ExternalChildDomainLease)args![0]!;
                ExternalChildDomainTransition transition = (ExternalChildDomainTransition)args[1]!;
                ExternalOperationIdentity operation = (ExternalOperationIdentity)args[2]!;
                HybridCpuExternalFeatureManifest manifest = ((HybridCpuExternalRuntime)Inner).QueryFeatures();
                ExternalChildDomainTransitionReceipt receipt = new ExternalChildDomainTransitionReceipt(lease, operation,
                    manifest.ContractVersion, manifest.Generation + 1, transition,
                    ExternalChildDomainState.Parked);
                return new ExternalChildResult<ExternalChildDomainTransitionReceipt>(
                    ExternalRuntimeOutcome.Succeeded, receipt, string.Empty);
            }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CloseChildDomain) &&
                result is ExternalChildResult<ExternalChildDomainCloseReceipt> close)
            {
                if (ThrowAfterClose) throw new InvalidOperationException("External child close receipt was lost.");
                if (LoseCloseReceipt)
                    return new ExternalChildResult<ExternalChildDomainCloseReceipt>(
                        ExternalRuntimeOutcome.Unknown, null, "Injected missing child close receipt.");
                if (ForgeClose && close.Receipt is not null)
                    return close with { Receipt = close.Receipt with { ManifestGeneration = close.Receipt.ManifestGeneration + 1 } };
            }
            return result;
        }
    }

    [Fact]
    public void AssemblyReferencesContractsButNotModelAuthorityOrHybridCpuInternals()
    {
        AssemblyName[] references = typeof(HybridCpuExecutableAdapterProject).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, x => x.Name == "HybridCPU_NeutralRuntime.Contracts");
        Assert.DoesNotContain(references, x => x.Name?.Contains("Model", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(references, x => x.Name == "HybridCPU_NeutralRuntime.AuthorityCore");
        Assert.DoesNotContain(references, x => x.Name?.Contains("ISE", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes(),
            type => type.Name.Contains("Scaffold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LifecycleFixtureIncludesEveryFailClosedStateAndRejectsOptimisticTransitions()
    {
        Assert.Equal(
            ["Opening", "Active", "Draining", "Closed", "Revoked", "Faulted", "Quarantined"],
            Enum.GetNames<AdapterResourceLifecycle>());

        LifecycleSemanticsFixture fixture = new LifecycleSemanticsFixture();
        Assert.False(fixture.TryTransition(AdapterResourceLifecycle.Closed));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Active));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Draining));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Closed));
        Assert.False(fixture.TryTransition(AdapterResourceLifecycle.Active));
    }

    [Fact]
    public void AuthorityReservationsAreUniqueAndLateGenerationCannotCrossApply()
    {
        OperationReservationRegistry operations = new OperationReservationRegistry();
        NeutralOperationIdentity first = operations.Reserve();
        NeutralOperationIdentity second = operations.Reserve();
        Assert.NotEqual(first.Handle, second.Handle);
        Assert.Equal(NeutralLateResultDisposition.RejectedDifferentGeneration,
            operations.Reconcile(first with { Generation = new(first.Generation.Value + 1) }, true));
        Assert.Equal(NeutralLateResultDisposition.Applied, operations.Reconcile(first, true));
    }

    [Fact]
    public void UnknownBackendOutcomeQuarantinesWithoutReleasingReservation()
    {
        OperationReservationRegistry operations = new OperationReservationRegistry();
        NeutralOperationIdentity operation = operations.Reserve();
        Assert.Equal(NeutralLateResultDisposition.QuarantinedUnknownOutcome,
            operations.Reconcile(operation, false));
        Assert.True(operations.IsReserved(operation));
        Assert.Equal(NeutralLateResultDisposition.Applied, operations.Reconcile(operation, true));
        Assert.False(operations.IsReserved(operation));
    }

    [Fact]
    public void PublicSurfaceContainsNoRawHardwareIdentityOrDmaExecutionOperation()
    {
        string[] forbidden = ["DomainTag", "AddressSpaceTag", "Vmx", "Vmcs", "Apic", "Msi", "Gsi", "Iommu", "Physical", "BusAddress", "SubmitDma", "CompleteDma", "Descriptor", "Queue"];
        IEnumerable<string> names = typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(member => member.Name);
        foreach (string fragment in forbidden)
            Assert.DoesNotContain(names, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class LifecycleSemanticsFixture
    {
        public AdapterResourceLifecycle Lifecycle { get; private set; } = AdapterResourceLifecycle.Opening;

        public bool TryTransition(AdapterResourceLifecycle next)
        {
            bool allowed = (Lifecycle, next) switch
            {
                (AdapterResourceLifecycle.Opening, AdapterResourceLifecycle.Active or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                (AdapterResourceLifecycle.Active, AdapterResourceLifecycle.Draining or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                (AdapterResourceLifecycle.Draining, AdapterResourceLifecycle.Closed or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                _ => false,
            };
            if (allowed) Lifecycle = next;
            return allowed;
        }
    }
}









