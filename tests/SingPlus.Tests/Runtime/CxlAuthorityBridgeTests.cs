using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlAuthorityBridgeTests
{
    [Theory]
    [InlineData(false, false, 0)] [InlineData(true, false, 0)]
    [InlineData(false, true, 0)] [InlineData(true, true, 0)]
    [InlineData(false, false, 1)] [InlineData(true, false, 1)]
    [InlineData(false, true, 1)] [InlineData(true, true, 1)]
    [InlineData(false, false, 2)] [InlineData(true, false, 2)]
    [InlineData(false, true, 2)] [InlineData(true, true, 2)]
    [InlineData(false, false, 3)] [InlineData(true, false, 3)]
    [InlineData(false, true, 3)] [InlineData(true, true, 3)]
    public void FabricPublicationAndCompletionRequireOriginalOwnerAfterReply(bool backing, bool foreign, int fault)
    {
        Scenario s = CreateScenario();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object cxlGate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        object platformGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        AuthorityProvider platform = (AuthorityProvider)typeof(PlatformAuthorityBridge).GetField("_provider", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        object? original = null;
        object? replacement = null;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(cxlGate));
            Assert.False(Monitor.IsEntered(platformGate));
            lock (cxlGate)
            {
                if (foreign)
                {
                    replacement = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                    replacement.GetType().GetProperty("PendingChildren")!.SetValue(replacement, 23);
                    fabrics[s.Cxl.ReturnedFabric!.BindingId] = replacement;
                }
                else fabrics.Remove(s.Cxl.ReturnedFabric!.BindingId);
            }
        }
        s.Cxl.FabricQueryHook = () =>
        {
            s.Cxl.FabricQueryHook = null;
            original = fabrics[s.Cxl.ReturnedFabric!.BindingId]!;
            if (fault is 0 or 3) Lose();
            else platform.BeforeIncarnation = () =>
            {
                platform.BeforeIncarnation = null;
                Lose();
                if (fault == 2) throw new IOException("incarnation observation failed after owner loss");
            };
            if (fault == 3) throw new IOException("fabric receipt Query reply lost after owner loss");
        };
        CxlFabricBindingRequest request = Request(s.Cxl.Endpoint);
        KernelResult<CxlFabricBinding> result = backing ? s.Bridge.BindFabricForBacking(s.Owner, s.Backing.Handle, s.Subject, s.Lease, request)
            : s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, request);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.NotNull(original);
        if (foreign) Assert.NotNull(replacement);
        else Assert.False(fabrics.Contains(s.Cxl.ReturnedFabric!.BindingId));
        Assert.Equal(false, original.GetType().GetProperty("CreationInFlight")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("Admitted")!.GetValue(original));
        if (replacement is not null)
        {
            Assert.Equal(true, replacement.GetType().GetProperty("CreationInFlight")!.GetValue(replacement));
            Assert.Equal(false, replacement.GetType().GetProperty("Admitted")!.GetValue(replacement));
            Assert.Equal(23, replacement.GetType().GetProperty("PendingChildren")!.GetValue(replacement));
        }
        CxlDeviceReservation reservation = (CxlDeviceReservation)original.GetType().GetProperty("DeviceReservation")!.GetValue(original)!;
        Assert.Same(original, reservation.OriginalFabricSource);
        Assert.Equal(request, reservation.OriginalFabricRequest);
        Assert.Equal(s.Cxl.ReturnedFabric, reservation.OriginalFabricOutcome!.Value.Value);
        Assert.True(s.Cxl.Query(s.Cxl.ReturnedFabric!.BindingId).IsSuccess);
        Assert.Equal(1, s.Cxl.FabricBindCalls);
        Assert.Equal(0, s.Cxl.FabricUnbindCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.False(s.Bridge.BindMemory(s.Owner, s.Backing.Handle, s.Cxl.ReturnedFabric!).IsSuccess);
        Assert.False(s.Kernel.RevokePlatformDevice(s.Subject.Process, s.Lease).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Subject.Process).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Backing.Region, s.Owner).IsSuccess);
        IEnumerable children = (global::System.Collections.IEnumerable)reservation.OriginalDeviceSource!.GetType().GetProperty("CxlChildren")!.GetValue(reservation.OriginalDeviceSource)!;
        Assert.Single(children.Cast<object>());
        Assert.Equal(0, s.Cxl.FabricUnbindCalls);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    public void FabricCleanupRequiresOriginalTrackedOwnerBeforeEffectAndFinalParentRelease(bool foreign, int stage)
    {
        Scenario s = CreateScenario();
        CxlFabricBinding fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = fabrics[fabric.BindingId]!;
        CxlDeviceReservation reservation = (CxlDeviceReservation)original.GetType().GetProperty("DeviceReservation")!.GetValue(original)!;
        Assert.Same(original, reservation.OriginalFabricSource);
        object device = reservation.OriginalDeviceSource!;
        IEnumerable children = (global::System.Collections.IEnumerable)device.GetType().GetProperty("CxlChildren")!.GetValue(device)!;
        object cxlGate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        object platformGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(cxlGate));
            Assert.False(Monitor.IsEntered(platformGate));
            lock (cxlGate)
            {
                if (foreign) fabrics[fabric.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else fabrics.Remove(fabric.BindingId);
            }
        }
        if (stage == 0) Lose();
        if (stage == 1) s.Cxl.FabricQueryHook = Lose;
        if (stage == 2)
        {
            int queries = 0;
            s.Cxl.EndpointQueryHook = () => { if (++queries == 2) Lose(); };
        }
        if (stage == 3) s.Cxl.BeforeUnbind = Lose;
        if (stage == 4)
        {
            AuthorityProvider platform = (AuthorityProvider)typeof(PlatformAuthorityBridge).GetField("_provider", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
            s.Cxl.BeforeUnbind = () => platform.BeforeIncarnation = () => { platform.BeforeIncarnation = null; Lose(); };
        }
        Assert.False(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        s.Cxl.FabricQueryHook = null; s.Cxl.EndpointQueryHook = null; s.Cxl.BeforeUnbind = null;
        Assert.Equal(stage >= 3 ? 1 : 0, s.Cxl.FabricUnbindCalls);
        Assert.Equal(stage >= 3 ? fabric : null, original.GetType().GetProperty("ReleasedBinding")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Single(children.Cast<object>());
        Assert.False(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.Equal(stage >= 3 ? 1 : 0, s.Cxl.FabricUnbindCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Subject.Process).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Backing.Region, s.Owner).IsSuccess);
        Assert.Single(children.Cast<object>());
        Assert.Equal(stage >= 3 ? 1 : 0, s.Cxl.FabricUnbindCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KnownFabricClosureSurvivesTrackedOwnerLossWithoutAnotherUnbind(bool foreign)
    {
        Scenario s = CreateScenario();
        CxlFabricBinding fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = fabrics[fabric.BindingId]!;
        s.Cxl.BeforeUnbind = () =>
        {
            if (foreign) fabrics[fabric.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else fabrics.Remove(fabric.BindingId);
        };
        Assert.False(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.Equal(1, s.Cxl.FabricUnbindCalls);
        Assert.False(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        s.Cxl.BeforeUnbind = null;
        fabrics[fabric.BindingId] = original; // test restoration, not durable recovery or renewed permission
        Assert.True(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.Equal(1, s.Cxl.FabricUnbindCalls);
        Assert.False(fabrics.Contains(fabric.BindingId));
        Assert.True(s.Kernel.RevokeCapability(DeviceCapability(s)).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentFabricClosureAndChildAdmissionPreserveOneEffectAndOriginalPins()
    {
        Scenario s = CreateScenario();
        CxlFabricBinding fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Cxl.BeforeUnbind = () => { entered.TrySetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(15))); };
        Task<KernelResult> closing = Task.Run(() => s.Bridge.ReleaseFabric(fabric));
        KernelResult result = default;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(fabric).Error);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.BindMemory(s.Owner, s.Backing.Handle, fabric).Error);
            Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
            Assert.Equal(1, s.Cxl.FabricUnbindCalls);
        }
        finally { resume.Set(); result = await closing.WaitAsync(TimeSpan.FromSeconds(15)); }
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, s.Cxl.FabricUnbindCalls);
        Assert.True(s.Kernel.RevokeCapability(DeviceCapability(s)).IsSuccess);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public void FabricClosureRetainsOriginalParentReservationAcrossOwnerLoss(bool parent, bool foreign, bool late)
    {
        Scenario s = CreateScenario();
        CxlFabricBinding fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AuthorityProvider platform = (AuthorityProvider)typeof(PlatformAuthorityBridge).GetField("_provider", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        object originalDevice = devices[s.Lease.LeaseId]!;
        IEnumerable children = (global::System.Collections.IEnumerable)originalDevice.GetType().GetProperty("CxlChildren")!.GetValue(originalDevice)!;
        object reservation = Assert.Single(children.Cast<object>());
        Assert.Same(originalDevice, reservation.GetType().GetProperty("OriginalDeviceSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reservation));
        IDictionary owners = parent ? (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)! : devices;
        object key = parent ? s.Lease.DomainBinding.BindingId : s.Lease.LeaseId;
        object original = owners[key]!;
        void Lose()
        {
            object gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
            Assert.False(Monitor.IsEntered(gate));
            if (foreign) owners[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else owners.Remove(key);
        }
        if (late) s.Cxl.BeforeUnbind = () => platform.BeforeIncarnation = () => { platform.BeforeIncarnation = null; Lose(); };
        else Lose();
        Assert.False(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.Single(children.Cast<object>());
        Assert.Equal(1, s.Cxl.FabricUnbindCalls);
        s.Cxl.BeforeUnbind = null;
        Assert.False(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.Equal(1, s.Cxl.FabricUnbindCalls); // known original closure never invokes Unbind again
        Assert.Single(children.Cast<object>());
        owners[key] = original; // test restoration, not durable recovery or renewed permission
        Assert.True(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.Empty(children.Cast<object>());
        Assert.Equal(1, s.Cxl.FabricUnbindCalls);
        Assert.True(s.Kernel.RevokeCapability(DeviceCapability(s)).IsSuccess);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    [InlineData(false, 5)] [InlineData(true, 5)]
    public void MalformedFabricReturnedObjectCleanupCannotReleaseOriginalInvocation(bool backing, int fault)
    {
        Scenario s = CreateScenario();
        CxlFabricBindingRequest request = Request(s.Cxl.Endpoint);
        s.Cxl.RewriteFabricReceipt = binding => fault switch
        {
            1 => binding with { EndpointId = new("foreign") },
            2 => binding with { CapacityBytes = 1 },
            3 => binding with { Generation = new(0) },
            4 => binding,
            _ => binding with { BindingId = new(101) }
        };
        s.Cxl.ThrowFabricQuery = fault == 4;
        s.Cxl.BeforeUnbind = () =>
        {
            object gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Bridge)!;
            Assert.False(Monitor.IsEntered(gate));
            Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseFabric(s.Cxl.ReturnedFabric!).Error);
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Subject.Process, s.Lease).Error);
            if (fault == 5) throw new IOException("returned-object compensation reply lost");
        };
        KernelResult<CxlFabricBinding> result = backing
            ? s.Bridge.BindFabricForBacking(s.Owner, s.Backing.Handle, s.Subject, s.Lease, request)
            : s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, request);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(1, s.Cxl.FabricBindCalls);
        Assert.Equal(fault == 4 ? 0 : 1, s.Cxl.FabricUnbindCalls);
        Assert.True(s.Cxl.Query(s.Cxl.OriginalFabric!.BindingId).IsSuccess); // original provider effect remains live
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        object original = devices[s.Lease.LeaseId]!;
        IEnumerable children = (global::System.Collections.IEnumerable)original.GetType().GetProperty("CxlChildren")!.GetValue(original)!;
        CxlDeviceReservation reservation = Assert.IsType<CxlDeviceReservation>(Assert.Single(children.Cast<object>()));
        Assert.Equal(request, reservation.OriginalFabricRequest);
        Assert.Equal(s.Cxl.ReturnedFabric, reservation.OriginalFabricOutcome!.Value.Value);
        Assert.False(reservation.OriginalFabricReceiptValidated);
        if (fault is 4 or 5) Assert.Null(reservation.ReturnedObjectCompensation);
        else Assert.True(reservation.ReturnedObjectCompensation!.Value.IsSuccess);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        for (int retry = 0; retry < 2; retry++)
            Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseFabric(s.Cxl.ReturnedFabric!).Error);
        Assert.Equal(fault == 4 ? 0 : 1, s.Cxl.FabricUnbindCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Subject.Process).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Backing.Region, s.Owner).IsSuccess);
        Assert.Single(children.Cast<object>());
    }

    [Fact]
    public void DirectFabricReceiptRetainsParentUntilExactClosure()
    {
        Scenario s = CreateScenario();
        CxlFabricBinding fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Subject.Process, s.Lease).Error);
        CapabilityId capability = DeviceCapability(s);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(capability).Error);
        Assert.True(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.True(s.Kernel.RevokeCapability(capability).IsSuccess);
    }

    [Fact]
    public void EndpointQueryRevocationPreventsNewDirectFabricEffect()
    {
        Scenario s = CreateScenario();
        s.Cxl.EndpointQueryHook = () => Assert.True(s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)).IsSuccess);
        KernelResult<CxlFabricBinding> result = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint));
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, s.Cxl.FabricBindCalls);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Subject.Process, s.Lease).IsSuccess);
    }

    [Fact]
    public void CoherentAdmissionRejectsRevokedSourceBeforeProviderCallback()
    {
        Scenario s = CreateScenario();
        CxlFabricBinding fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
        KernelResult<CxlCoherentBinding> result = s.Bridge.BindCoherentAccess(s.Owner, s.Use.Handle, fabric);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, s.Cxl.CoherentBindCalls);
        Assert.True(s.Bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.True(s.Kernel.RevokeCapability(DeviceCapability(s)).IsSuccess);
    }

    private static CapabilityId DeviceCapability(Scenario s) => s.Kernel.CapabilityAuthority.InspectionSnapshot()
        .Single(item => item.Descriptor.ResourceKind == ResourceKind.Device && item.Descriptor.ResourceId == s.Lease.Device.ResourceId)
        .Descriptor.CapabilityId;

    [Fact]
    public void NarrowProviderRolesRemainIndependentAndPhysicalIdentityIsNotPublic()
    {
        Assembly assembly = typeof(ICxlDiscoveryProvider).Assembly;
        Assert.Null(assembly.GetType("SingPlus.Platform.ICxlProvider"));
        foreach (Type? role in new[] { typeof(ICxlDiscoveryProvider), typeof(ICxlIoProvider), typeof(ICxlMemoryProvider),
                     typeof(ICxlCoherentAccessProvider), typeof(ICxlFabricProvider), typeof(ICxlSecurityEvidenceProvider) })
            Assert.True(role.IsInterface);

        IEnumerable<string> publicSurface = assembly.GetExportedTypes()
            .Where(type => type.Namespace == "SingPlus.Platform" && type.Name.Contains("Cxl", StringComparison.Ordinal))
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(member => member.Name);
        foreach (string? forbidden in new[] { "Hdm", "Dpa", "Hpa", "Decoder", "Pasid", "Requester", "RegisterOffset", "Mailbox" })
            Assert.DoesNotContain(publicSurface, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CxlIoUsesExistingPlatformDeviceLeasePath()
    {
        Scenario s = CreateScenario();
        KernelResult<PlatformDeviceIdentity> resolved = s.Bridge.ResolveIoDevice(s.Cxl.Endpoint);
        Assert.True(resolved.IsSuccess, resolved.Message);
        Assert.Equal(s.Lease.Device, resolved.Value);
        Assert.Equal("device:cxl-semantic-0", s.Lease.Device.ResourceId);
    }

    [Fact]
    public void StaleDeviceGenerationRejectsBeforeFabricEffect()
    {
        Scenario s = CreateScenario();
        CxlEndpointSnapshot selected = s.Cxl.Endpoint;
        s.Cxl.DeviceGeneration++;
        KernelResult<CxlFabricBinding> result = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(selected));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, s.Cxl.FabricBindCalls);
    }

    [Fact]
    public void StaleFabricAndMemoryGenerationsRejectRevalidation()
    {
        Scenario s = CreateScenario();
        CxlFabricBinding fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        CxlMemoryBinding memory = s.Bridge.BindMemory(s.Owner, s.Backing.Handle, fabric).Value!;
        Assert.True(s.Bridge.RevalidateBacking(s.Owner, s.Backing.Handle, s.Cxl.Endpoint, fabric, memory).IsSuccess);

        s.Cxl.FabricGeneration++;
        KernelResult staleFabric = s.Bridge.RevalidateBacking(s.Owner, s.Backing.Handle, s.Cxl.Endpoint, fabric, memory);
        Assert.False(staleFabric.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleFabric.Error);

        s.Cxl.FabricGeneration--;
        s.Cxl.MemoryGeneration++;
        KernelResult staleMemory = s.Bridge.RevalidateBacking(s.Owner, s.Backing.Handle, s.Cxl.Endpoint, fabric, memory);
        Assert.False(staleMemory.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleMemory.Error);
    }

    [Fact]
    public void DirectCoherentWriteIsFutureGatedUntilCpuAliasExclusionExists()
    {
        AuthorityProvider platform = new AuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(platform);
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 905, 906);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 64).Value!;
        KernelResult<RegionUseDescriptor> result = kernel.AcquireRegionUse(handle, buffer.Handle, RegionUseMode.DirectCoherentWrite, new(0, 64));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
    }

    [Fact]
    public void TrustedSecurityEvidenceDoesNotCreateRegionAuthority()
    {
        Scenario s = CreateScenario();
        Assert.True(s.Bridge.QuerySecurityEvidence(s.Cxl.Endpoint).IsSuccess);
        RegionUseHandle fabricatedUse = s.Use.Handle with { UseId = new RegionUseId(9999) };

        KernelResult<CxlFabricBinding> result = s.Bridge.BindFabric(s.Owner, fabricatedUse, s.Subject, s.Lease, Request(s.Cxl.Endpoint));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Cxl.FabricBindCalls);
    }

    private static CxlFabricBindingRequest Request(CxlEndpointSnapshot endpoint) =>
        new(endpoint.EndpointId, endpoint.DeviceGeneration, 64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive);

    private static Scenario CreateScenario(RegionUseMode mode = RegionUseMode.StagedOutput)
    {
        AuthorityProvider platform = new AuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(platform);
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 901, 902);
        SingProcess process = kernel.Processes.Resolve(handle).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, handle.Generation);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 64).Value!;
        RegionBackingLeaseDescriptor backing = kernel.Regions.ReserveBacking(buffer.Handle, owner).Value!;
        RegionUseDescriptor use = kernel.AcquireRegionUse(handle, buffer.Handle, mode, new(0, 64)).Value!;
        PlatformDomainBinding domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        CapabilityDescriptorV1 capability = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, "device:cxl-semantic-0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease lease = kernel.BindPlatformDevice(handle, domain, capability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        CxlProviders cxl = new CxlProviders(process.DomainId, handle);
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(kernel, cxl, cxl, cxl, cxl, cxl, cxl);
        return new(kernel, owner, new PlatformDomainIdentity(process.DomainId, handle), backing, use, lease, cxl, bridge);
    }

    private sealed record Scenario(RuntimeKernel Kernel, RegionOwner Owner, PlatformDomainIdentity Subject,
        RegionBackingLeaseDescriptor Backing, RegionUseDescriptor Use, PlatformDeviceLease Lease, CxlProviders Cxl, CxlAuthorityBridge Bridge);

    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider, IPlatformProviderIncarnationSource
    {
        internal Action? BeforeIncarnation;
        public PlatformProviderIncarnation CurrentIncarnation { get { BeforeIncarnation?.Invoke(); return new(1); } }
        public PlatformProviderDescriptor Descriptor { get; } = new(new("cxl-test-platform"), 1, PlatformAuthorityFeatures.NeutralDomainBinding);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), subject));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), domainLease, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(PlatformAuthorityStatus.Unsupported, "Not used.");
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();
    }

    private sealed class CxlProviders : ICxlDiscoveryProvider, ICxlIoProvider, ICxlFabricProvider,
        ICxlMemoryProvider, ICxlCoherentAccessProvider, ICxlSecurityEvidenceProvider
    {
        private readonly EvidenceSubjectIdentity _subject;
        public CxlProviders(DomainId domain, ProcessHandle process) =>
            _subject = new(domain, process.ProcessId, process.Generation, null, null);
        public ulong DeviceGeneration { get; set; } = 1;
        public ulong FabricGeneration { get; set; } = 1;
        public ulong MemoryGeneration { get; set; } = 1;
        public ulong CoherentGeneration { get; set; } = 1;
        public CxlEndpointFeatures Features { get; set; } = CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory | CxlEndpointFeatures.CoherentAccess;
        public int FabricBindCalls { get; private set; }
        internal Action? EndpointQueryHook { get; set; }
        internal Action? BeforeUnbind;
        internal Action? FabricQueryHook;
        internal int FabricUnbindCalls;
        internal Func<CxlFabricBinding, CxlFabricBinding>? RewriteFabricReceipt;
        internal bool ThrowFabricQuery;
        internal CxlFabricBinding? OriginalFabric;
        internal CxlFabricBinding? ReturnedFabric;
        internal int CoherentBindCalls { get; private set; }
        public CxlEndpointSnapshot Endpoint => new(new("endpoint-0"), new(DeviceGeneration), Features, true);
        private CxlFabricBinding? _fabric;
        private CxlMemoryBinding? _memory;
        private CxlCoherentBinding? _coherent;

        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpointId)
        {
            EndpointQueryHook?.Invoke();
            return endpointId == Endpoint.EndpointId ? PlatformAuthorityResult<CxlEndpointSnapshot>.Ok(Endpoint) : Fail<CxlEndpointSnapshot>();
        }
        public PlatformAuthorityResult<PlatformDeviceIdentity> ResolveDevice(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration) =>
            expectedGeneration.Value == DeviceGeneration ? PlatformAuthorityResult<PlatformDeviceIdentity>.Ok(new("device:cxl-semantic-0")) : Stale<PlatformDeviceIdentity>();
        public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId endpointId) =>
            PlatformAuthorityResult<CxlMemoryCapacitySnapshot>.Ok(new(endpointId, new(DeviceGeneration), 4096, 4096,
                CxlMemoryPersistence.Volatile, 0, 1, 1));
        public PlatformAuthorityResult<CxlFabricBinding> Bind(CxlFabricBindingRequest request)
        {
            FabricBindCalls++;
            _fabric = new(new(1), new(FabricGeneration), request.EndpointId, request.DeviceGeneration, request.CapacityBytes);
            OriginalFabric = _fabric;
            ReturnedFabric = RewriteFabricReceipt?.Invoke(_fabric) ?? _fabric;
            return PlatformAuthorityResult<CxlFabricBinding>.Ok(ReturnedFabric);
        }
        public PlatformAuthorityResult<CxlFabricBinding> Query(CxlFabricBindingId bindingId)
        {
            FabricQueryHook?.Invoke();
            if (ThrowFabricQuery) { ThrowFabricQuery = false; throw new IOException("fabric query reply lost"); }
            return _fabric is null ? Fail<CxlFabricBinding>() :
                PlatformAuthorityResult<CxlFabricBinding>.Ok(_fabric with { Generation = new(FabricGeneration) });
        }
        public PlatformAuthorityResult Unbind(CxlFabricBinding binding)
        {
            FabricUnbindCalls++; BeforeUnbind?.Invoke();
            if (_fabric == binding) _fabric = null;
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabricBinding, RegionBackingLeaseDescriptor backingLease)
        {
            _memory = new(new(1), new(MemoryGeneration), fabricBinding, backingLease.Handle);
            return PlatformAuthorityResult<CxlMemoryBinding>.Ok(_memory);
        }
        public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId bindingId) => _memory is null ? Fail<CxlMemoryBinding>() :
            PlatformAuthorityResult<CxlMemoryBinding>.Ok(_memory with { Generation = new(MemoryGeneration) });
        public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding) { _memory = null; return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding fabricBinding, RegionUseDescriptor regionUse)
        {
            _coherent = new(new(1), new(CoherentGeneration), fabricBinding, regionUse.Handle);
            CoherentBindCalls++;
            return PlatformAuthorityResult<CxlCoherentBinding>.Ok(_coherent);
        }
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId bindingId) => _coherent is null ? Fail<CxlCoherentBinding>() :
            PlatformAuthorityResult<CxlCoherentBinding>.Ok(_coherent with { Generation = new(CoherentGeneration) });
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding) { _coherent = null; return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration) =>
            PlatformAuthorityResult<EvidenceRecord>.Ok(new(new("cxl-security", 1), _subject, new("fake-security"),
                EvidenceVisibilityClass.SecurityMeasurement, "trusted", "opaque", new(1, 1), false));
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Denied, "Unknown.");
        private static PlatformAuthorityResult<T> Stale<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Stale, "Stale.");
    }
}
