using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDeviceLeaseTests
{
    [Fact]
    public void SurvivingOriginalDeviceSourceRefusesNewEffectThroughEqualTupleRootClone()
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1175, 1275);
        Assert.True(kernel.AllocateRegion(owner, 4096).IsSuccess);
        PlatformDomainBinding root = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId first = MintDeviceCapability(kernel, owner, "device/original-root", CapabilityRights.Configure);
        Assert.True(kernel.BindPlatformDevice(owner, root, first, PlatformDeviceRights.Configure).IsSuccess);
        CapabilityId second = MintDeviceCapability(kernel, owner, "device/foreign-root", CapabilityRights.Configure);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = routes[root.BindingId]!;
        object foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        routes[root.BindingId] = foreign;
        ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!)[root.Subject] = foreign;
        Assert.False(kernel.BindPlatformDevice(owner, root, second, PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void SurvivingOriginalDeviceBlocksRootReplacementWithoutEitherRootIndex()
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1174, 1274);
        Assert.True(kernel.AllocateRegion(owner, 4096).IsSuccess);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, owner, "device/lost-root-indexes", CapabilityRights.Configure);
        Assert.True(kernel.BindPlatformDevice(owner, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!).Remove(binding.Subject);
        ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!).Remove(binding.BindingId);
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
        Assert.Equal(1, provider.DomainBindCalls);
        IDictionary routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        Assert.Empty(routes.Values.Cast<object>());
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostCanonicalRootBeforeDeviceEffectRefusesInvocation(bool foreign)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1173, 1273);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = originals[binding.Subject]!;
        KernelResult<PlatformDeviceLease> result = kernel.PlatformAuthority.BindDevice(binding, binding.Subject, new CapabilityId(1),
            new("device/pre-effect-root-loss"), PlatformDeviceRights.Configure, () =>
            {
                if (foreign) originals[binding.Subject] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else originals.Remove(binding.Subject);
                return KernelResult.Ok();
            }, (_, commit) => commit());
        Assert.False(result.IsSuccess);
        Assert.Equal(0, provider.DeviceBindCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(0, original.GetType().GetProperty("PendingDeviceBinds")!.GetValue(original));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostCanonicalRootCannotPublishDeviceOrInvokeCompensation(bool foreign)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1172, 1272);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, owner, "device/canonical-root-loss", CapabilityRights.Configure);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = originals[binding.Subject]!;
        provider.AfterBindMaterialize = () =>
        {
            if (foreign) originals[binding.Subject] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else originals.Remove(binding.Subject);
        };
        Assert.False(kernel.BindPlatformDevice(owner, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.False(kernel.BindPlatformDevice(owner, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateDeviceOriginalOwnerLossRetainsReceiptAndBacking(bool foreign)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1171, 1271);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, owner, "device/late-original-loss", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(owner, binding, capability, PlatformDeviceRights.Configure).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_originalDeviceOwners", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = originals[lease.LeaseId]!;
        provider.AfterDeviceRevokeEffect = () =>
        {
            if (foreign) originals[lease.LeaseId] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else originals.Remove(lease.LeaseId);
        };
        Assert.False(kernel.RevokePlatformDevice(owner, lease).IsSuccess);
        Assert.NotNull(original.GetType().GetProperty("RevokeObservation")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.False(kernel.RevokePlatformDevice(owner, lease).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostDeviceRouteCannotDischargeOriginalRootDependency(bool foreign)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1170, 1270);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, owner, "device/original-route", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(owner, binding, capability, PlatformDeviceRights.Configure).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = routes[lease.LeaseId]!;
        if (foreign)
        {
            routes[lease.LeaseId] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            Assert.False(kernel.RevokePlatformDevice(owner, lease).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
        else routes.Remove(lease.LeaseId);
        Assert.False(kernel.PlatformAuthority.RevokeDomain(binding, binding.Subject).IsSuccess);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), r => r.Handle == region.Handle).State);
        routes[lease.LeaseId] = original;
        Assert.True(kernel.RevokePlatformDevice(owner, lease).IsSuccess);
        routes.Remove(lease.LeaseId);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), r => r.Handle == region.Handle).State);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(1, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FalseClosedRootCannotDischargeUnknownDeviceBind(bool missingRoutes)
    {
        DeviceProvider provider = new DeviceProvider { BindStatusAfterMaterialize = PlatformAuthorityStatus.NotAccepted };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1168, 1268);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, owner, "device/unknown-closed-root", CapabilityRights.Configure);
        Assert.False(kernel.BindPlatformDevice(owner, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = domains[binding.BindingId]!;
        PropertyInfo state = original.GetType().GetProperty("AuthorityState")!;
        state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
        if (missingRoutes)
        {
            domains.Remove(binding.BindingId);
            ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!).Remove(owner);
        }
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), item => item.Handle == region.Handle).State);
        Assert.Equal(true, original.GetType().GetProperty("DeviceBindMayHaveEffect")!.GetValue(original));
        Assert.Single(((global::System.Collections.IDictionary)typeof(DeviceProvider).GetField("_devices", flags)!.GetValue(provider)!).Values.Cast<object>());
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, 0, false, false)]
    [InlineData((PlatformAuthorityStatus)999, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 1, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 2, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, false, true)]
    public void FailedOriginalDeviceBindRetainsRawIntentAndRefusesActualReclaim(
        PlatformAuthorityStatus status, int replacement, bool sourceThrows, bool lostReply)
    {
        DeviceProvider provider = new DeviceProvider { BindStatusAfterMaterialize = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1167, 1267);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/original-bind-outcome", CapabilityRights.Configure);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = domains[binding.BindingId]!;
        object? Read(string name) => original.GetType().GetProperty(name)!.GetValue(original);
        bool effectSeen = false;
        provider.AfterBindMaterialize = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.Equal(true, Read("DeviceBindMayHaveEffect"));
            Assert.Equal(capability, Read("DeviceBindCapabilityId"));
            Assert.Equal(new PlatformDeviceIdentity("device/original-bind-outcome"), Read("DeviceBindDevice"));
            Assert.Equal(PlatformDeviceRights.Configure, Read("DeviceBindRights"));
            Assert.NotNull(Read("DeviceBindIncarnation"));
            Assert.NotNull(Read("DeviceBindEpoch"));
            Assert.Same(original, Read("DeviceBindSource"));
            Assert.Null(Read("DeviceBindObservation"));
            Assert.Equal(1, ((global::System.Collections.IDictionary)typeof(DeviceProvider).GetField("_devices", flags)!.GetValue(provider)!).Count);
            effectSeen = true;
            if (replacement == 1)
                domains[binding.BindingId] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (replacement == 2) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (sourceThrows) provider.BeforeIncarnationRead = () =>
            {
                Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderDeviceLease>>(Read("DeviceBindObservation")).Status);
                throw new IOException("Late bind generation read lost.");
            };
            if (lostReply) throw new IOException("Original device bind reply lost after effect.");
        };
        Assert.False(kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        Assert.True(effectSeen);
        Assert.Equal(true, Read("DeviceBindMayHaveEffect"));
        Assert.Equal(0, Read("PendingDeviceBinds"));
        if (lostReply) Assert.Null(Read("DeviceBindObservation"));
        else Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderDeviceLease>>(Read("DeviceBindObservation")).Status);
        if (replacement != 0)
        {
            object retained = domains[binding.BindingId]!;
            Assert.Same(original, retained.GetType().GetProperty("DeviceBindSource")!.GetValue(retained));
            Assert.Null(retained.GetType().GetProperty("DeviceBindObservation")!.GetValue(retained));
        }
        provider.BeforeIncarnationRead = null;
        provider.AfterBindMaterialize = null;
        provider.BindStatusAfterMaterialize = null;
        Assert.False(kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public void ExactDeviceBindTransfersObservationAndAllowsOriginalClosure()
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1168, 1268);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/exact-bind-transfer", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object domain = domains[binding.BindingId]!;
        object device = devices[lease.LeaseId]!;
        Assert.Null(domain.GetType().GetProperty("DeviceBindObservation")!.GetValue(domain));
        Assert.Null(domain.GetType().GetProperty("DeviceBindSource")!.GetValue(domain));
        Assert.Equal(false, domain.GetType().GetProperty("DeviceBindMayHaveEffect")!.GetValue(domain));
        Assert.True(Assert.IsType<PlatformAuthorityResult<PlatformProviderDeviceLease>>(device.GetType().GetProperty("BindObservation")!.GetValue(device)).IsSuccess);
        Assert.True(kernel.RevokePlatformDevice(subject, lease).IsSuccess);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(1, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false, 0, false)] [InlineData(false, 0, true)]
    [InlineData(false, 1, false)] [InlineData(false, 1, true)]
    [InlineData(false, 2, false)] [InlineData(false, 2, true)]
    [InlineData(true, 0, false)] [InlineData(true, 0, true)]
    [InlineData(true, 1, false)] [InlineData(true, 1, true)]
    [InlineData(true, 2, false)] [InlineData(true, 2, true)]
    public void KnownDeviceClosureRetainsOriginalDomainAndPinsActualReclaim(bool rejectedBind, int ownerLoss, bool sourceThrows)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1165, 1265);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/known-original-domain", CapabilityRights.Configure);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object parent = domains[binding.BindingId]!;
        object? original = null;
        object? foreign = null;
        provider.AfterDeviceRevokeEffect = () =>
        {
            original = Assert.Single(devices.Values.Cast<object>());
            Assert.Same(parent, original.GetType().GetProperty("RevokeParent")!.GetValue(original));
            Assert.Equal(0, ((global::System.Collections.IDictionary)typeof(DeviceProvider).GetField("_devices", flags)!.GetValue(provider)!).Count);
            if (ownerLoss == 0) devices.Clear();
            else if (ownerLoss == 1) domains.Remove(binding.BindingId);
            else
            {
                foreign = Activator.CreateInstance(parent.GetType(), binding with { Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1) },
                    parent.GetType().GetProperty("ProviderLease")!.GetValue(parent))!;
                domains[binding.BindingId] = foreign;
            }
            if (sourceThrows) provider.ThrowOnIncarnation = true;
        };
        if (rejectedBind)
        {
            provider.AfterBindMaterialize = () => Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            Assert.False(kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        }
        else
        {
            PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).Value!;
            Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, lease).Error);
        }
        Assert.NotNull(original);
        Assert.True(((PlatformAuthorityResult)original.GetType().GetProperty("RevokeObservation")!.GetValue(original)!).IsSuccess);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(true, original.GetType().GetProperty("FaultPinned")!.GetValue(original));
        if (ownerLoss == 0) Assert.Equal(true, parent.GetType().GetProperty("DeviceBindMayHaveEffect")!.GetValue(parent));
        if (foreign is not null) Assert.Equal(false, foreign.GetType().GetProperty("DeviceBindMayHaveEffect")!.GetValue(foreign));
        provider.ThrowOnIncarnation = false;
        provider.AfterDeviceRevokeEffect = null;
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, false, false)]
    [InlineData((PlatformAuthorityStatus)999, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, true)]
    public void RejectedDeviceCleanupRetainsOriginalOutcomeWithoutRepeatOrRootReclaim(PlatformAuthorityStatus status, bool loseOwner, bool loseReply)
    {
        DeviceProvider provider = new DeviceProvider { RevokeReplyStatus = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1163, 1263);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/rejected-original-close", CapabilityRights.Configure);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object? original = null;
        provider.AfterBindMaterialize = () => Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
        provider.AfterDeviceRevokeEffect = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            original = Assert.Single(devices.Values.Cast<object>());
            Assert.Equal(true, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
            Assert.Equal(0, ((global::System.Collections.IDictionary)typeof(DeviceProvider).GetField("_devices", flags)!.GetValue(provider)!).Count);
            if (loseOwner) devices.Clear();
            if (loseReply) throw new IOException("Rejected-device original cleanup reply lost");
        };
        Assert.False(kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        Assert.NotNull(original);
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        object? raw = original.GetType().GetProperty("RevokeObservation")!.GetValue(original);
        if (loseReply) Assert.Null(raw);
        else Assert.Equal(status, ((PlatformAuthorityResult)raw!).Status);
        provider.AfterDeviceRevokeEffect = null;
        provider.RevokeReplyStatus = null;
        _ = kernel.RevokeCapability(capability);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RejectedDeviceCleanupRefusesLostParentBeforeCallback(bool replaceOwner)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1164, 1264);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/rejected-parent-loss", CapabilityRights.Configure);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object parent = domains[binding.BindingId]!;
        provider.AfterBindMaterialize = () =>
        {
            Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            int reads = 0;
            provider.BeforeIncarnationRead = () =>
            {
                if (++reads != 2) return; // Post-bind observation, then cleanup admission.
                provider.BeforeIncarnationRead = null;
                if (replaceOwner) domains[binding.BindingId] = parent.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(parent, null)!;
                else domains.Remove(binding.BindingId);
            };
        };
        Assert.False(kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        object original = Assert.Single(devices.Values.Cast<object>());
        Assert.Equal(false, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
        Assert.Null(original.GetType().GetProperty("RevokeObservation")!.GetValue(original));
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, false, false)]
    [InlineData((PlatformAuthorityStatus)999, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, true)]
    public void FailedOriginalDeviceRevokeRetainsOutcomeAndPinsActualRootTeardown(PlatformAuthorityStatus status, bool loseOwner, bool loseReply)
    {
        DeviceProvider provider = new DeviceProvider { RevokeReplyStatus = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1162, 1262);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/original-close", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, binding, capability, PlatformDeviceRights.Configure).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = devices[lease.LeaseId]!;
        object parent = domains[binding.BindingId]!;
        provider.AfterDeviceRevokeEffect = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.Equal(true, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
            Assert.Equal(0, ((global::System.Collections.IDictionary)typeof(DeviceProvider).GetField("_devices", flags)!.GetValue(provider)!).Count);
            if (loseOwner) devices.Remove(lease.LeaseId);
            if (loseReply) throw new IOException("Original device close reply lost");
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, lease).Error);
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        object? raw = original.GetType().GetProperty("RevokeObservation")!.GetValue(original);
        if (loseReply) Assert.Null(raw);
        else Assert.Equal(status, ((PlatformAuthorityResult)raw!).Status);
        if (loseOwner) Assert.Equal(true, parent.GetType().GetProperty("DeviceBindMayHaveEffect")!.GetValue(parent));
        provider.AfterDeviceRevokeEffect = null;
        provider.RevokeReplyStatus = null;
        Assert.False(kernel.RevokePlatformDevice(subject, lease).IsSuccess);
        _ = kernel.RevokeCapability(capability); // Permission revoke is not original closure.
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false, false)][InlineData(true, false)]
    [InlineData(false, true)][InlineData(true, true)]
    public void DeviceCleanupSourceCallbackOwnerLossRefusesEffectAndRetainsProcess(bool replacementOwner, bool domainOwner)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1161, 1261);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        PlatformDomainBinding parent = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/source-owner-loss", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, parent, capability, PlatformDeviceRights.Configure).Value!;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        object originalDevice = devices[lease.LeaseId]!;
        IDictionary records = domainOwner ? (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)! : devices;
        object key = domainOwner ? parent.BindingId : lease.LeaseId;
        object original = records[key]!;
        provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            if (replacementOwner)
                records[key] = original.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
            else records.Remove(key);
        };
        Assert.False(kernel.RevokePlatformDevice(subject, lease).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(false, originalDevice.GetType().GetProperty("ClosureInFlight")!.GetValue(originalDevice));
        Assert.Equal(false, originalDevice.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(originalDevice));
        Assert.Equal(false, originalDevice.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(originalDevice));
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        records[key] = original;
        Assert.True(kernel.RevokePlatformDevice(subject, lease).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityDeviceClosureAllowsWorkerAndRetainsUnknownEffect(bool fault)
    {
        DeviceProvider provider = new DeviceProvider { ThrowAfterDeviceRevoke = fault };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1160, 1260);
        PlatformDomainBinding parent = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject,
            "device/callback-closure", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, parent, capability, PlatformDeviceRights.Configure).Value!;
        bool observed = false;
        provider.BeforeDeviceRevoke = () =>
        {
            Task worker = Task.Run(() =>
            {
                Assert.True(kernel.MintCapability(new DomainId(1260), subject,
                    ResourceKind.Compute, "compute/device-closure", CapabilityRights.Execute).IsSuccess);
                Assert.Equal(KernelError.PlatformBindingActive,
                    kernel.RevokePlatformDevice(subject, lease).Error);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            observed = true;
        };
        KernelResult result = kernel.RevokeCapability(capability);
        Assert.True(observed);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(!fault, result.IsSuccess);
        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            provider.ThrowAfterDeviceRevoke = false;
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.TerminateProcess(subject).IsSuccess);
            Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
            Assert.Equal(0, provider.DomainRevokeCalls);
            Assert.Equal(1, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(subject).IsSuccess);
            Assert.Equal(1, provider.DeviceRevokeCalls);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RefusedDeviceAdmissionRetainsExactCleanupForActualTeardown(int fault)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1150, 1250);
        PlatformDomainBinding parent = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/refused-compensation", CapabilityRights.Configure);
        bool ownerObserved = false;
        provider.BeforeDeviceRevoke = () =>
        {
            ownerObserved = kernel.PlatformAuthority.ActiveDeviceLeasesForBinding(parent).Count == 1;
            if (fault == 1) provider.ThrowOnIncarnation = true;
            if (fault == 3) provider.Incarnation++;
        };
        provider.ThrowAfterDeviceRevoke = fault == 2;
        // Model injects refusal at the existing trusted-caller admission seam;
        // actual kernel teardown must consume the bridge-owned cleanup lifetime.
        int admissionReads = 0;
        KernelResult<PlatformDeviceLease> refused = kernel.PlatformAuthority.BindDevice(parent, parent.Subject, capability,
            new("device/refused-compensation"), PlatformDeviceRights.Configure,
            () => ++admissionReads == 1 ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.CapabilityRevoked, "Injected late admission refusal."),
            static (_, commit) => commit());
        Assert.False(refused.IsSuccess);
        Assert.True(ownerObserved);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        (SingProcess _, ProcessHandle unrelated) = TestFixtures.Create(kernel, 1151, 1251);
        Assert.True(kernel.TerminateProcess(unrelated).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        provider.ThrowOnIncarnation = false;
        provider.ThrowAfterDeviceRevoke = false;
        provider.Incarnation = 1;
        KernelResult terminated = kernel.TerminateProcess(subject);
        Assert.Equal(fault is 0 or 1, terminated.IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        if (fault is 2 or 3) Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void KnownDeviceClosureRetriesLocalReleaseWithoutDuplicateEffect(int consumer)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1148, 1248);
        PlatformDomainBinding parent = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/known-close", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, parent, capability, PlatformDeviceRights.Configure).Value!;
        provider.BeforeDeviceRevoke = () => provider.ThrowOnIncarnation = true;
        KernelResult Close() => consumer switch
        {
            0 => kernel.RevokePlatformDevice(subject, lease),
            1 => kernel.RevokeCapability(capability),
            _ => kernel.TerminateProcess(subject),
        };
        Assert.False(Close().IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        provider.ThrowOnIncarnation = false;
        Assert.True(Close().IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        if (consumer != 2) Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void KnownOrLostDeviceReplyCannotPromoteAcrossDiscontinuity(int fault)
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1149, 1249);
        PlatformDomainBinding parent = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/discontinuous-close", CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, parent, capability, PlatformDeviceRights.Configure).Value!;
        provider.BeforeDeviceRevoke = () =>
        {
            if (fault == 0) provider.Incarnation++;
            if (fault == 1) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowAfterDeviceRevoke = fault == 2;
        Assert.False(kernel.RevokePlatformDevice(subject, lease).IsSuccess);
        provider.Incarnation = 1;
        provider.ThrowAfterDeviceRevoke = false;
        Assert.False(kernel.RevokePlatformDevice(subject, lease).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
    }
    // Existing standalone bridge fixtures model trusted caller admission; no Kernel capability claim.
    private static KernelResult<PlatformDeviceLease> BindDeviceForModel(
        PlatformAuthorityBridge bridge, PlatformDomainBinding binding, PlatformDomainIdentity owner,
        CapabilityId capability, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
        bridge.BindDevice(binding, owner, capability, device, rights, static () => KernelResult.Ok(), static (_, commit) => commit());

    [Fact]
    public void ExactDeviceCapabilityMaterializesSeparateLeaseAndBlocksEarlyDomainClose()
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1101, 1110);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(
            kernel,
            subject,
            "device/net0",
            CapabilityRights.Read | CapabilityRights.Configure);

        KernelResult<PlatformDeviceLease> lease = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure);

        Assert.True(lease.IsSuccess, lease.Message);
        Assert.Equal("device/net0", lease.Value!.Device.ResourceId);
        Assert.Equal(
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            lease.Value.Rights);
        Assert.NotEqual(0UL, lease.Value.LeaseId.Value);
        Assert.NotEqual(0UL, lease.Value.Generation.Value);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal("device/net0", provider.LastDevice.ResourceId);

        KernelResult earlyDomainClose = kernel.RevokePlatformDomain(subject, binding);
        Assert.False(earlyDomainClose.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, earlyDomainClose.Error);
        Assert.Equal(0, provider.DomainRevokeCalls);

        Assert.True(kernel.RevokePlatformDevice(subject, lease.Value).IsSuccess);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(
            new[] { "bind-device:device/net0", "revoke-device:device/net0", "revoke-domain" },
            provider.Log);
    }

    [Fact]
    public void LocalAdmissionFailuresHappenBeforeProviderDeviceBinding()
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1102, 1120);
        (SingProcess _, ProcessHandle other) = TestFixtures.Create(kernel, 1103, 1130);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        PlatformDomainBinding otherBinding = kernel.BindPlatformDomain(other).Value!;
        CapabilityId readOnly = MintDeviceCapability(
            kernel,
            subject,
            "device/net0",
            CapabilityRights.Read);
        CapabilityId wrongResource = kernel.MintCapability(
            new DomainId(1120),
            subject,
            ResourceKind.MemoryRegion,
            "device/net0",
            CapabilityRights.Configure).Value!.CapabilityId;

        KernelResult<PlatformDeviceLease> invalidRights = kernel.BindPlatformDevice(
            subject,
            binding,
            readOnly,
            PlatformDeviceRights.None);
        Assert.Equal(KernelError.PlatformDenied, invalidRights.Error);

        KernelResult<PlatformDeviceLease> insufficient = kernel.BindPlatformDevice(
            subject,
            binding,
            readOnly,
            PlatformDeviceRights.Configure);
        Assert.Equal(KernelError.InsufficientRights, insufficient.Error);

        KernelResult<PlatformDeviceLease> wrongKind = kernel.BindPlatformDevice(
            subject,
            binding,
            wrongResource,
            PlatformDeviceRights.Configure);
        Assert.Equal(KernelError.WrongCapabilityResource, wrongKind.Error);

        KernelResult<PlatformDeviceLease> wrongDomain = kernel.BindPlatformDevice(
            subject,
            otherBinding,
            readOnly,
            PlatformDeviceRights.Read);
        Assert.Equal(KernelError.WrongPlatformDomain, wrongDomain.Error);

        ProcessHandle staleSubject = subject with { Generation = subject.Generation + 1 };
        KernelResult<PlatformDeviceLease> stale = kernel.BindPlatformDevice(
            staleSubject,
            binding,
            readOnly,
            PlatformDeviceRights.Read);
        Assert.Equal(KernelError.StaleHandle, stale.Error);

        Assert.Equal(0, provider.DeviceBindCalls);
    }

    [Theory]
    [InlineData(DeviceBindFault.WrongDevice)]
    [InlineData(DeviceBindFault.WrongDomain)]
    [InlineData(DeviceBindFault.WrongRights)]
    [InlineData(DeviceBindFault.Unmaterialized)]
    public void MalformedProviderDeviceAuthorityFailsClosedAndIsRevoked(
        DeviceBindFault fault)
    {
        DeviceProvider provider = new DeviceProvider { BindFault = fault };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1104, 1140);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(
            kernel,
            subject,
            "device/storage0",
            CapabilityRights.Configure);

        bool recordObserved = false;
        provider.BeforeDeviceRevoke = () => recordObserved = kernel.PlatformAuthority.ActiveDeviceLeasesForBinding(binding).Count == 1;

        KernelResult<PlatformDeviceLease> result = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure);

        Assert.False(recordObserved, "Foreign malformed returned receipt must not authorize compensation.");
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        object original = Assert.Single(records.Values.Cast<object>());
        Assert.Equal(false, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
        Assert.Null(original.GetType().GetProperty("RevokeObservation")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedDeviceCleanupFailureQuarantinesExistingDeviceOwner(bool throws)
    {
        DeviceProvider provider = new DeviceProvider
        {
            BindFault = DeviceBindFault.WrongRights,
            RevokeStatus = throws ? null : PlatformAuthorityStatus.Faulted,
            ThrowOnRevoke = throws,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1120, 1220);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/ambiguous0",
            CapabilityRights.Configure);

        KernelResult<PlatformDeviceLease> bound = kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure);

        Assert.Equal(KernelError.PlatformFaulted, bound.Error);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void CapabilityRevokeClosesDeviceLeaseAndOldLeaseCannotActAgain()
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1105, 1150);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(
            kernel,
            subject,
            "device/audio0",
            CapabilityRights.Write | CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Write).Value!;

        KernelResult revokeCapability = kernel.RevokeCapability(capability);

        Assert.True(revokeCapability.IsSuccess, revokeCapability.Message);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        KernelResult staleEffect = kernel.RevokePlatformDevice(subject, lease);
        Assert.False(staleEffect.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, staleEffect.Error);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesDeviceBeforeDomain()
    {
        DeviceProvider provider = new DeviceProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1106, 1160);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(
            kernel,
            subject,
            "device/controller0",
            CapabilityRights.Configure);
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);

        KernelResult terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(
            new[] { "bind-device:device/controller0", "revoke-device:device/controller0", "revoke-domain" },
            provider.Log);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }

    [Fact]
    public void DeviceCloseFaultPinsProcessTeardownAndPreventsDomainClose()
    {
        DeviceProvider provider = new DeviceProvider { RevokeStatus = PlatformAuthorityStatus.Faulted };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle subject) = TestFixtures.Create(kernel, 1107, 1170);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(
            kernel,
            subject,
            "device/fault0",
            CapabilityRights.Configure);
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);

        KernelResult terminate = kernel.TerminateProcess(subject);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        KernelResult<ProcessTeardownSnapshot> snapshot = kernel.QueryProcessTeardown(subject);
        Assert.True(snapshot.IsSuccess, snapshot.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, snapshot.Value!.Phase);
        Assert.False(snapshot.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void ProviderRevokedStatusCannotCloseExactDeviceLease()
    {
        DeviceProvider provider = new DeviceProvider { RevokeStatus = PlatformAuthorityStatus.Revoked };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1130, 1230);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/revoked-status",
            CapabilityRights.Configure);
        PlatformDeviceLease lease = kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure).Value!;

        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, lease).Error);
        provider.RevokeStatus = null;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, lease).Error);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void DeviceBindReceiptLossQuarantinesParentDomain()
    {
        DeviceProvider provider = new DeviceProvider { ThrowOnBindAfterMaterialize = true };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1131, 1231);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/lost-receipt",
            CapabilityRights.Configure);

        KernelResult<PlatformDeviceLease> result = kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.RevokePlatformDomain(subject, binding).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformDevice(subject, binding, capability,
                PlatformDeviceRights.Configure).Error);
        Assert.Equal(1, provider.DeviceBindCalls);
    }

    [Fact]
    public void BackendResetPreservesUnknownDeviceBindPin()
    {
        DeviceProvider provider = new DeviceProvider { ThrowOnBindAfterMaterialize = true };
        PlatformAuthorityBridge bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = new PlatformDomainIdentity(new DomainId(1233),
            new ProcessHandle(new ProcessId(1133), 1));
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        Assert.Equal(KernelError.PlatformFaulted,
            BindDeviceForModel(bridge, parent, owner, new CapabilityId(1), new("device/reset-pin"),
                PlatformDeviceRights.Configure).Error);

        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out PlatformDomainBinding current));
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void DeviceBindCallbackBlocksParentRevokeBeforeLeasePublication()
    {
        DeviceProvider provider = new DeviceProvider();
        PlatformAuthorityBridge bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = new PlatformDomainIdentity(new DomainId(1234),
            new ProcessHandle(new ProcessId(1134), 1));
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        provider.AfterBindMaterialize = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
            Assert.Equal(0, provider.DomainRevokeCalls);
        };

        PlatformDeviceLease lease = BindDeviceForModel(bridge, parent, owner, new CapabilityId(1),
            new("device/pending-bind"), PlatformDeviceRights.Configure).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.True(bridge.RevokeDevice(lease, owner).IsSuccess);
        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetInsideDeviceBindCallbackPinsNewParentGeneration(bool terminalEpoch)
    {
        DeviceProvider provider = new DeviceProvider();
        PlatformAuthorityBridge bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = new PlatformDomainIdentity(new DomainId(1235),
            new ProcessHandle(new ProcessId(1135), 1));
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        if (terminalEpoch)
            typeof(PlatformAuthorityBridge).GetField("_backendEpoch",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bridge, ulong.MaxValue);
        provider.AfterBindMaterialize = () =>
        {
            KernelResult<PlatformBackendResetSnapshot> reset = bridge.ObserveBackendReset();
            Assert.Equal(terminalEpoch ? KernelError.CapacityExhausted : KernelError.None, reset.Error);
        };

        Assert.Equal(KernelError.PlatformFaulted,
            BindDeviceForModel(bridge, parent, owner, new CapabilityId(1),
                new("device/reset-during-bind"), PlatformDeviceRights.Configure).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out PlatformDomainBinding current));
        Assert.NotEqual(parent.Generation, current.Generation);
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void ParentRevokeCallbackRejectsDeviceBindBeforeProvider()
    {
        DeviceProvider provider = new DeviceProvider();
        PlatformAuthorityBridge bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = new PlatformDomainIdentity(new DomainId(1236),
            new ProcessHandle(new ProcessId(1136), 1));
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        provider.BeforeDomainRevoke = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                BindDeviceForModel(bridge, parent, owner, new CapabilityId(1),
                    new("device/revoke-in-flight"), PlatformDeviceRights.Configure).Error);
            Assert.Equal(0, provider.DeviceBindCalls);
        };

        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
        Assert.Equal(1, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Faulted)]
    [InlineData(PlatformAuthorityStatus.Denied)]
    public void DeviceBindNonAcceptanceWithoutExactNoEffectPinsParentDomain(
        PlatformAuthorityStatus status)
    {
        DeviceProvider provider = new DeviceProvider { BindStatusAfterMaterialize = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1132, 1232);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId capability = MintDeviceCapability(kernel, subject, "device/ambiguous-status",
            CapabilityRights.Configure);

        Assert.False(kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.RevokePlatformDomain(subject, binding).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformDevice(subject, binding, capability,
                PlatformDeviceRights.Configure).Error);
        Assert.Equal(1, provider.DeviceBindCalls);
    }

    [Fact]
    public void DeviceLeaseSurfaceKeepsProviderAndHardwareAuthorityPrivate()
    {
        Type[] surface = new[]
        {
            typeof(PlatformDeviceRights),
            typeof(PlatformDeviceIdentity),
            typeof(PlatformDeviceLeaseId),
            typeof(PlatformDeviceLeaseGeneration),
            typeof(PlatformDeviceLease),
        };
        string[] forbidden = new[]
        {
            "PlatformProvider",
            "HybridCPU",
            "Neutral",
            "Physical",
            "Address",
            "Pte",
            "PageTable",
            "Iommu",
            "Dma",
            "Mmio",
            "Irq",
            "InterruptVector",
            "Vmx",
            "Vmcs",
            "Lane",
            "Opcode",
        };

        foreach (Type? type in surface)
        foreach (MemberInfo member in type.GetMembers(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static))
        {
                string signature = member.ToString() ?? member.Name;
            foreach (string? term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static CapabilityId MintDeviceCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        string resourceId,
        CapabilityRights rights)
    {
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.Device,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    public enum DeviceBindFault
    {
        None = 0,
        WrongDevice,
        WrongDomain,
        WrongRights,
        Unmaterialized,
    }

    private sealed class DeviceProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformProviderIncarnationSource
    {
        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease> _domains = [];
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private ulong _nextDomain = 1;
        private ulong _nextDevice = 1;

        public DeviceBindFault BindFault { get; set; }
        public ulong Incarnation { get; set; } = 1;
        public bool ThrowOnIncarnation { get; set; }
        public Action? BeforeIncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                BeforeIncarnationRead?.Invoke();
                return ThrowOnIncarnation ? throw new InvalidOperationException("Injected late device source observation failure.") : new(Incarnation);
            }
        }
        public PlatformAuthorityStatus? RevokeStatus { get; set; }
        public PlatformAuthorityStatus? RevokeReplyStatus { get; set; }
        public Action? AfterDeviceRevokeEffect { get; set; }
        public bool ThrowOnRevoke { get; set; }
        public bool ThrowAfterDeviceRevoke { get; set; }
        public bool ThrowOnBindAfterMaterialize { get; set; }
        public PlatformAuthorityStatus? BindStatusAfterMaterialize { get; set; }
        public Action? AfterBindMaterialize { get; set; }
        public Action? BeforeDomainRevoke { get; set; }
        public Action? BeforeDeviceRevoke { get; set; }
        public int DeviceBindCalls { get; private set; }
        public int DomainBindCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public int DomainRevokeCalls { get; private set; }
        public PlatformDeviceIdentity LastDevice { get; private set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("device-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            DomainBindCalls++;
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            DomainRevokeCalls++;
            Log.Add("revoke-domain");
            BeforeDomainRevoke?.Invoke();
            _domains.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            DeviceBindCalls++;
            LastDevice = device;
            Log.Add($"bind-device:{device.ResourceId}");
            if (!_domains.TryGetValue(domainLease.LeaseId, out PlatformProviderDomainLease live) || live != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unknown domain lease.");
            }

            PlatformProviderDomainLease returnedDomain = BindFault == DeviceBindFault.WrongDomain
                ? domainLease with
                {
                    Generation = new PlatformProviderLeaseGeneration(domainLease.Generation.Value + 1),
                }
                : domainLease;
            PlatformDeviceIdentity returnedDevice = BindFault == DeviceBindFault.WrongDevice
                ? new PlatformDeviceIdentity(device.ResourceId + "-other")
                : device;
            PlatformDeviceRights returnedRights = BindFault == DeviceBindFault.WrongRights
                ? PlatformDeviceRights.Read
                : rights;
            PlatformProviderDeviceLeaseId id = BindFault == DeviceBindFault.Unmaterialized
                ? new PlatformProviderDeviceLeaseId(0)
                : new PlatformProviderDeviceLeaseId(_nextDevice++);
            PlatformProviderLeaseGeneration generation = BindFault == DeviceBindFault.Unmaterialized
                ? new PlatformProviderLeaseGeneration(0)
                : new PlatformProviderLeaseGeneration(1);
            PlatformProviderDeviceLease lease = new PlatformProviderDeviceLease(
                id,
                generation,
                returnedDomain,
                returnedDevice,
                returnedRights);
            if (lease.LeaseId.Value != 0)
                _devices[lease.LeaseId] = lease;
            AfterBindMaterialize?.Invoke();
            if (ThrowOnBindAfterMaterialize)
                throw new InvalidOperationException("Injected device binding receipt loss.");
            if (BindStatusAfterMaterialize is { } status)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(status,
                    "Injected ambiguous device binding response.");
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            BeforeDeviceRevoke?.Invoke();
            DeviceRevokeCalls++;
            Log.Add($"revoke-device:{lease.Device.ResourceId}");
            if (ThrowOnRevoke)
                throw new InvalidOperationException("Injected malformed lease cleanup receipt loss.");
            if (RevokeStatus is { } status)
            {
                return PlatformAuthorityResult.Fail(
                    status,
                    "Injected device revoke failure.");
            }

            if (lease.LeaseId.Value != 0)
                _devices.Remove(lease.LeaseId);
            AfterDeviceRevokeEffect?.Invoke();
            if (ThrowAfterDeviceRevoke)
                throw new InvalidOperationException("Device effect completed but reply lost.");
            if (RevokeReplyStatus is { } replyStatus)
                return PlatformAuthorityResult.Fail(replyStatus, "Injected failed original device close reply after effect.");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by device lease tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by device lease tests.");
    }
}
