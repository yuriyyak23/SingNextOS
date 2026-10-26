using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformMmioLeaseTests
{
    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, 0, false, false)]
    [InlineData((PlatformAuthorityStatus)999, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 1, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 2, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, false, true)]
    public void FailedOriginalMmioMapAfterEffectRetainsRawAndPinsActualConsumers(
        PlatformAuthorityStatus status, int ownerLoss, bool sourceThrows, bool lostReply)
    {
        MmioProvider provider = new MmioProvider { MmioMapReplyStatus = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1248, 1808);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/failed-original-map", "registers", 128);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = devices[device.LeaseId]!;
        object parent = domains[device.DomainBinding.BindingId]!;
        HashSet<object> sources = (HashSet<object>)parent.GetType().GetProperty("MmioSources")!.GetValue(parent)!;
        object? foreign = null;
        object? Read(string name) => original.GetType().GetProperty(name)!.GetValue(original);
        bool effectSeen = false;
        provider.BeforeMmioMap = () =>
        {
            Assert.Equal(true, Read("MmioMapMayHaveEffect"));
            Assert.Same(parent, Read("MmioMapParent"));
            Assert.Contains(original, sources);
            Assert.Null(Read("MmioMapObservation"));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
        };
        provider.AfterMmioMapEffect = () =>
        {
            IDictionary mappings = (global::System.Collections.IDictionary)provider.GetType().GetField("_mmio", flags)!.GetValue(provider)!;
            Assert.Equal(1, mappings.Count);
            effectSeen = true;
            if (ownerLoss == 1) devices.Remove(device.LeaseId);
            if (ownerLoss == 2)
            {
                foreign = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                devices[device.LeaseId] = foreign;
            }
            if (sourceThrows) provider.IncarnationRead = () =>
            {
                Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderMmioLease>>(Read("MmioMapObservation")).Status);
                throw new IOException("Late map source observation failed.");
            };
            if (lostReply) throw new IOException("Original map reply lost after provider effect.");
        };
        Assert.False(kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).IsSuccess);
        Assert.True(effectSeen);
        Assert.Equal(true, Read("MmioMapMayHaveEffect"));
        Assert.Equal(true, Read("FaultPinned"));
        Assert.Equal(0, Read("PendingMmioBinds"));
        Assert.Contains(original, sources);
        if (lostReply) Assert.Null(Read("MmioMapObservation"));
        else Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderMmioLease>>(Read("MmioMapObservation")).Status);
        if (foreign is not null) Assert.Null(foreign.GetType().GetProperty("MmioMapObservation")!.GetValue(foreign));
        provider.IncarnationRead = null;
        provider.AfterMmioMapEffect = null;
        provider.BeforeMmioMap = null;
        provider.MmioMapReplyStatus = null;
        Assert.False(kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).IsSuccess);
        if (ownerLoss == 0) Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.PlatformAuthority.RevokeDomain(device.DomainBinding, device.DomainBinding.Subject).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KnownMapSourceLifetimeSurvivesLostPublicationIndexAndReleasesOnlyOriginalClosure(bool loseIndex)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1249, 1809);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/map-lifetime-index", "registers", 128);
        PlatformMmioLease map = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = mappings[map.LeaseId]!;
        object parent = domains[device.DomainBinding.BindingId]!;
        HashSet<object> sources = (HashSet<object>)parent.GetType().GetProperty("MmioSources")!.GetValue(parent)!;
        Assert.Contains(original, sources);
        Assert.True(Assert.IsType<PlatformAuthorityResult<PlatformProviderMmioLease>>(original.GetType().GetProperty("MapObservation")!.GetValue(original)).IsSuccess);
        if (loseIndex)
        {
            mappings.Remove(map.LeaseId);
            IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
            devices.Remove(device.LeaseId);
            Assert.False(kernel.PlatformAuthority.RevokeDomain(device.DomainBinding, device.DomainBinding.Subject).IsSuccess);
            Assert.Contains(original, sources);
            Assert.Equal(0, provider.DomainRevokeCalls);
            Assert.Equal(0, provider.MmioRevokeCalls);
        }
        else
        {
            Assert.True(kernel.RevokePlatformMmio(subject, map).IsSuccess);
            Assert.Empty(sources);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.True(kernel.PlatformAuthority.RevokeDomain(device.DomainBinding, device.DomainBinding.Subject).IsSuccess);
            Assert.Equal(1, provider.MmioRevokeCalls);
            Assert.Equal(1, provider.DomainRevokeCalls);
        }
    }

    [Theory]
    [InlineData(false, 0, false)] [InlineData(false, 0, true)]
    [InlineData(false, 1, false)] [InlineData(false, 1, true)]
    [InlineData(false, 2, false)] [InlineData(false, 2, true)]
    [InlineData(true, 0, false)] [InlineData(true, 0, true)]
    [InlineData(true, 1, false)] [InlineData(true, 1, true)]
    [InlineData(true, 2, false)] [InlineData(true, 2, true)]
    public void KnownMmioClosureRetainsOriginalDomainAndPinsActualReclaim(bool rejectedBind, int ownerLoss, bool sourceThrows)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1392, 1892);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/known-child-domain", "registers", 128);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object originalDomain = domains[device.DomainBinding.BindingId]!;
        object deviceOwner = devices[device.LeaseId]!;
        object original = null!;
        object? foreign = null;
        provider.AfterMmioRevokeEffect = () =>
        {
            original = Assert.Single(children.Values.Cast<object>());
            Assert.Same(originalDomain, original.GetType().GetProperty("RevokeParent")!.GetValue(original));
            IDictionary providerFacts = (global::System.Collections.IDictionary)provider.GetType().GetField("_mmio", flags)!.GetValue(provider)!; Assert.Empty(providerFacts);
            if (ownerLoss == 0) domains.Remove(device.DomainBinding.BindingId);
            else if (ownerLoss == 1)
            {
                foreign = originalDomain.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(originalDomain, null)!;
                domains[device.DomainBinding.BindingId] = foreign;
            }
            else originalDomain.GetType().GetProperty("ParentRevokeMayHaveEffect")!.SetValue(originalDomain, true);
            if (sourceThrows) provider.IncarnationRead = () => throw new IOException("Final child source observation failed.");
        };
        if (rejectedBind)
        {
            provider.BeforeMmioMap = () => Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            Assert.False(kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).IsSuccess);
        }
        else
        {
            PlatformMmioLease child = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
            Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformMmio(subject, child).Error);
        }
        Assert.NotNull(original);
        Assert.True(((PlatformAuthorityResult)original.GetType().GetProperty("RevokeObservation")!.GetValue(original)!).IsSuccess);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(true, deviceOwner.GetType().GetProperty("FaultPinned")!.GetValue(deviceOwner));
        if (foreign is not null)
        {
            Assert.Equal(false, foreign.GetType().GetProperty("ParentRevokeMayHaveEffect")!.GetValue(foreign));
            Assert.Null(foreign.GetType().GetProperty("RootRevokeObservation")!.GetValue(foreign));
        }
        provider.IncarnationRead = null;
        provider.AfterMmioRevokeEffect = null;
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }


    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MalformedMmioCompensationRetainsRawFactWithoutOriginalClosure(int outcome)
    {
        MmioProvider provider = new MmioProvider { ReturnedMutation = 0 };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1390, 1890);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/malformed-fact", "registers", 128);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary facts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = facts[device.LeaseId]!;
        object? Read(string name) => original.GetType().GetProperty(name)!.GetValue(original);
        if (outcome == 1) provider.MmioRevokeStatus = PlatformAuthorityStatus.NotAccepted;
        if (outcome == 2) provider.MmioRevokeStatus = (PlatformAuthorityStatus)999;
        if (outcome == 3) provider.AfterMmioRevokeEffect = () => throw new IOException("Compensation reply lost after provider effect.");
        provider.BeforeMmioRevoke = () =>
        {
            Assert.Equal(false, Read("MalformedMmioCleanupMayHaveEffect"));
            Assert.NotNull(Read("MalformedMmioReceipt"));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
        Assert.Equal(false, Read("MalformedMmioCleanupMayHaveEffect"));
        Assert.Null(Read("MalformedMmioCleanupObservation"));
        Assert.Equal(false, Read("ProviderClosureConfirmed"));
        Assert.True(kernel.PlatformAuthority.HasUnresolvedMmioEffect(id => id == capability));
        provider.MmioRevokeStatus = null;
        provider.AfterMmioRevokeEffect = null;
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MalformedMmioCompensationRefusesReplacedParentBeforeCallback(bool domainOwner)
    {
        MmioProvider provider = new MmioProvider { ReturnedMutation = 0 };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1391, 1891);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/malformed-fact", "registers", 128);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary deviceFacts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object deviceOwner = deviceFacts[device.LeaseId]!;
        IDictionary facts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField(domainOwner ? "_domains" : "_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object key = domainOwner ? device.DomainBinding.BindingId : device.LeaseId;
        object original = facts[key]!;
        bool replaced = false;
        provider.BeforeMmioMap = () =>
        {
            int reads = 0;
            provider.IncarnationRead = () =>
            {
                if (++reads != 1) return;
                provider.IncarnationRead = null;
                facts[key] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                replaced = true;
            };
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
        Assert.True(replaced);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Equal(false, deviceOwner.GetType().GetProperty("MalformedMmioCleanupMayHaveEffect")!.GetValue(deviceOwner));
        Assert.Null(deviceOwner.GetType().GetProperty("MalformedMmioCleanupObservation")!.GetValue(deviceOwner));
        Assert.NotNull(deviceOwner.GetType().GetProperty("MalformedMmioReceipt")!.GetValue(deviceOwner));
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RejectedMmioCleanupRefusesChangedOriginalParentBeforeEffect(bool domainOwner)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1245, 1805);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/rejected-mmio-source", "registers", 128);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary facts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField(domainOwner ? "_domains" : "_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object key = domainOwner ? device.DomainBinding.BindingId : device.LeaseId;
        object original = facts[key]!;
        bool replaced = false;
        provider.BeforeMmioMap = () =>
        {
            Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            int reads = 0;
            provider.IncarnationRead = () =>
            {
                if (++reads != 2) return;
                provider.IncarnationRead = null;
                facts[key] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                replaced = true;
            };
        };
        Assert.False(kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).IsSuccess);
        Assert.True(replaced);
        Assert.Equal(0, provider.MmioRevokeCalls);
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object child = Assert.Single(children.Values.Cast<object>());
        Assert.Equal(false, child.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(child));
        Assert.Null(child.GetType().GetProperty("RevokeObservation")!.GetValue(child));
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, false, false)]
    [InlineData((PlatformAuthorityStatus)999, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, true)]
    public void RejectedMmioCleanupRetainsOriginalOutcomeAndPinsActualTeardown(PlatformAuthorityStatus status, bool loseOwner, bool loseReply)
    {
        MmioProvider provider = new MmioProvider { MmioRevokeReplyStatus = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1244, 1804);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/rejected-mmio-outcome", "registers", 128);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object? original = null;
        provider.BeforeMmioMap = () => Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
        provider.AfterMmioRevokeEffect = () =>
        {
            original = Assert.Single(children.Values.Cast<object>());
            Assert.Equal(true, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.Equal(0, ((global::System.Collections.IDictionary)typeof(MmioProvider).GetField("_mmio", flags)!.GetValue(provider)!).Count);
            if (loseOwner) children.Clear();
            if (loseReply) throw new IOException("Rejected MMIO original cleanup reply lost");
        };
        Assert.False(kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).IsSuccess);
        Assert.NotNull(original);
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        object? raw = original.GetType().GetProperty("RevokeObservation")!.GetValue(original);
        if (loseReply) Assert.Null(raw); else Assert.Equal(status, ((PlatformAuthorityResult)raw!).Status);
        provider.MmioRevokeReplyStatus = null;
        provider.AfterMmioRevokeEffect = null;
        _ = kernel.RevokeCapability(capability);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void KnownMmioClosureWithLostChildOwnerPinsOriginalParent(bool foreignOwner, bool sourceThrows)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1243, 1803);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/known-mmio-child-loss", "registers", 128);
        PlatformMmioLease child = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary parents = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = children[child.LeaseId]!;
        object parent = parents[device.LeaseId]!;
        object? foreign = null;
        provider.AfterMmioRevokeEffect = () =>
        {
            Assert.Equal(0, ((global::System.Collections.IDictionary)typeof(MmioProvider).GetField("_mmio", flags)!.GetValue(provider)!).Count);
            if (foreignOwner)
            {
                foreign = Activator.CreateInstance(original.GetType(), child with { Generation = new PlatformMmioLeaseGeneration(child.Generation.Value + 1) },
                    original.GetType().GetProperty("ProviderLease")!.GetValue(original), capability)!;
                children[child.LeaseId] = foreign;
            }
            else children.Remove(child.LeaseId);
            if (sourceThrows) provider.IncarnationRead = () => throw new IOException("Source observation failed after owner loss");
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformMmio(subject, child).Error);
        Assert.True(((PlatformAuthorityResult)original.GetType().GetProperty("RevokeObservation")!.GetValue(original)!).IsSuccess);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(true, parent.GetType().GetProperty("FaultPinned")!.GetValue(parent));
        if (foreign is not null)
        {
            Assert.Null(foreign.GetType().GetProperty("RevokeObservation")!.GetValue(foreign));
            Assert.Equal(false, foreign.GetType().GetProperty("FaultPinned")!.GetValue(foreign));
        }
        provider.IncarnationRead = null;
        provider.AfterMmioRevokeEffect = null;
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MmioCleanupRefusesAbsentOrForeignOriginalDeviceBeforeEffect(bool foreign)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1242, 1802);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-device-source", "registers", 128);
        PlatformMmioLease child = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary parents = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = parents[device.LeaseId]!;
        object originalChild = children[child.LeaseId]!;
        if (foreign) parents[device.LeaseId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        else parents.Remove(device.LeaseId);
        Assert.False(kernel.RevokePlatformMmio(subject, child).IsSuccess);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Same(original, originalChild.GetType().GetProperty("MapDeviceSource")!.GetValue(originalChild));
        Assert.Null(originalChild.GetType().GetProperty("RevokeObservation")!.GetValue(originalChild));
        Assert.Equal(false, originalChild.GetType().GetProperty("ClosureInFlight")!.GetValue(originalChild));
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false, false, 0)]
    [InlineData(PlatformAuthorityStatus.Denied, false, false, 0)]
    [InlineData(PlatformAuthorityStatus.Stale, false, false, 0)]
    [InlineData((PlatformAuthorityStatus)999, false, false, 0)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false, 0)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, true, 0)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false, 1)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false, 2)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, true, 1)]
    public void OriginalMmioRevokeAfterEffectPinsActualTeardown(PlatformAuthorityStatus status, bool loseOwner, bool loseReply, int parentLoss)
    {
        MmioProvider provider = new MmioProvider { MmioRevokeReplyStatus = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1242, 1802);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-original-outcome", "registers", 128);
        PlatformMmioLease child = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary parents = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = children[child.LeaseId]!;
        object parent = parents[device.LeaseId]!;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object originalDomain = domains[device.DomainBinding.BindingId]!;
        provider.AfterMmioRevokeEffect = () =>
        {
            object gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
            Assert.False(Monitor.IsEntered(gate));
            Assert.Equal(true, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
            Assert.Equal(0, ((global::System.Collections.IDictionary)typeof(MmioProvider).GetField("_mmio", flags)!.GetValue(provider)!).Count);
            if (loseOwner) children.Remove(child.LeaseId);
            if (parentLoss == 1) domains.Remove(device.DomainBinding.BindingId);
            if (parentLoss == 2)
            {
                object foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(originalDomain, null)!;
                foreign.GetType().GetProperty("MmioSources")!.SetValue(foreign, new HashSet<object>(ReferenceEqualityComparer.Instance));
                domains[device.DomainBinding.BindingId] = foreign;
            }
            if (loseReply) throw new IOException("Original MMIO reply lost after effect");
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformMmio(subject, child).Error);
        Assert.Equal(true, parent.GetType().GetProperty("FaultPinned")!.GetValue(parent));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        object? raw = original.GetType().GetProperty("RevokeObservation")!.GetValue(original);
        if (loseReply) Assert.Null(raw);
        else Assert.Equal(status, ((PlatformAuthorityResult)raw!).Status);
        provider.AfterMmioRevokeEffect = null;
        provider.MmioRevokeReplyStatus = null;
        Assert.False(kernel.RevokePlatformMmio(subject, child).IsSuccess);
        // Original parent lifetime references retain unresolved cleanup even when the child index is absent.
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)][InlineData(false, true)]
    [InlineData(true, false)][InlineData(true, true)]
    public void CleanupSourceOwnerLossRefusesEffectAndPinsTeardown(bool parentOwner, bool replacementOwner)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1241, 1801);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-capture", "registers", 128);
        PlatformMmioLease child = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        object childKey = child.LeaseId;
        KernelResult Close() => kernel.RevokePlatformMmio(subject, child);
        OwnedRegion<int> buffer = kernel.AllocateRegion(subject, 4096).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mmioLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object originalChild = children[childKey]!;
        IDictionary facts = parentOwner ? (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)! : children;
        object key = parentOwner ? device.LeaseId : childKey;
        object original = facts[key]!;
        provider.IncarnationRead = () =>
        {
            provider.IncarnationRead = null;
            if (replacementOwner)
            {
                object replacement = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                if (parentOwner) replacement.GetType().GetProperty("ProviderIncarnation")!.SetValue(replacement, new PlatformProviderIncarnation(2));
                facts[key] = replacement;
            }
            else facts.Remove(key);
        };
        Assert.False(Close().IsSuccess);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Equal(false, originalChild.GetType().GetProperty("ClosureInFlight")!.GetValue(originalChild));
        Assert.Equal(false, originalChild.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(originalChild));
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        Assert.Equal(0, provider.MmioRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityMmioClosureAllowsWorkerAndRetainsOriginalFault(bool fault)
    {
        MmioProvider provider = new MmioProvider { ThrowOnMmioRevoke = fault };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1240, 1800);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject,
            "device/capability-mmio", "registers", 128);
        PlatformMmioLease lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        bool observed = false;
        provider.BeforeMmioRevoke = () =>
        {
            Task worker = Task.Run(() =>
            {
                Assert.True(kernel.MintCapability(new DomainId(1800), subject,
                    ResourceKind.Compute, "compute/mmio-closure", CapabilityRights.Execute).IsSuccess);
                Assert.Equal(KernelError.PlatformBindingActive,
                    kernel.RevokePlatformMmio(subject, lease).Error);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            observed = true;
        };
        KernelResult result = kernel.RevokeCapability(capability);
        Assert.True(observed);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(!fault, result.IsSuccess);
        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            provider.ThrowOnMmioRevoke = false;
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.Equal(1, provider.MmioRevokeCalls);
        }
        else
        {
            Assert.Equal(KernelError.PlatformBindingRevoked,
                kernel.RevokePlatformMmio(subject, lease).Error);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    public void RefusedMmioPublicationRetainsCleanupOwnerAndKnownReply(int consumer, int fault)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1241, 1741);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/refused-mmio", "registers", 128);
        bool admissionRefused = false;
        provider.BeforeMmioMap = () => admissionRefused = !kernel.RevokeCapability(capability).IsSuccess;
        bool ownerObserved = false;
        provider.BeforeMmioRevoke = () =>
        {
            ownerObserved = kernel.PlatformAuthority.ActiveMmioLeasesForDevice(device).Count == 1;
            if (fault == 1) provider.IncarnationRead = () => throw new InvalidOperationException("Late compensation query loss");
            if (fault == 3) provider.Incarnation++;
        };
        provider.ThrowAfterMmioRevoke = fault == 2;
        KernelResult<PlatformMmioLease> bind = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read);
        Assert.Equal(KernelError.CapabilityRevoked, bind.Error);
        Assert.True(admissionRefused);
        Assert.True(ownerObserved);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(1, provider.MmioRevokeCalls);
        provider.IncarnationRead = null;
        provider.Incarnation = 1;
        provider.ThrowAfterMmioRevoke = false;
        KernelResult closed = consumer switch
        {
            0 => kernel.RevokeCapability(capability),
            1 => kernel.RevokePlatformDevice(subject, device),
            _ => kernel.TerminateProcess(subject),
        };
        Assert.Equal(fault is 0 or 1, closed.IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        if (fault is 0 or 1)
        {
            if (consumer != 2) Assert.True(kernel.TerminateProcess(subject).IsSuccess);
            Assert.Equal(1, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.False(kernel.TerminateProcess(subject).IsSuccess);
            Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 2)]
    [InlineData(5, 0)] [InlineData(5, 1)] [InlineData(5, 2)]
    public void MalformedMmioCleanupCannotClearOriginalEffectPin(int mutation, int cleanup)
    {
        MmioProvider provider = new MmioProvider { ReturnedMutation = mutation };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1240, 1740);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/malformed-original", "registers", 128);
        bool pinObserved = false;
        provider.BeforeMmioRevoke = () => pinObserved =
            kernel.PlatformAuthority.HasUnresolvedMmioEffect(id => id == capability) &&
            kernel.RevokePlatformDevice(subject, device).Error == KernelError.PlatformFaulted;
        if (cleanup == 1) provider.MmioRevokeStatus = PlatformAuthorityStatus.Faulted;
        if (cleanup == 2) provider.ThrowOnMmioRevoke = true;
        KernelResult<PlatformMmioLease> bind = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read);
        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.False(pinObserved, "Malformed tuple must not authorize compensation callback.");
        Assert.True(kernel.PlatformAuthority.HasUnresolvedMmioEffect(id => id == capability));
        provider.MmioRevokeStatus = null;
        provider.ThrowOnMmioRevoke = false;
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void KnownMmioReplyRetriesActualConsumerWithoutDuplicateEffect(int consumer)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1238, 1738);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/known-mmio", "registers", 128);
        PlatformMmioLease lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        provider.BeforeMmioRevoke = () => provider.IncarnationRead = () => throw new InvalidOperationException("Late MMIO query fault");
        KernelResult Close() => consumer switch
        {
            0 => kernel.RevokePlatformMmio(subject, lease),
            1 => kernel.RevokeCapability(capability),
            2 => kernel.RevokePlatformDevice(subject, device),
            _ => kernel.TerminateProcess(subject),
        };
        Assert.False(Close().IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        provider.IncarnationRead = null;
        Assert.True(Close().IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        if (consumer != 3) Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(1, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MmioKnownMismatchOrLostReplyPinsParentAndPreventsReclaim(int fault)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1239, 1739);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/lost-mmio", "registers", 128);
        PlatformMmioLease lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        provider.BeforeMmioRevoke = () =>
        {
            if (fault == 0) provider.Incarnation++;
            if (fault == 1) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowAfterMmioRevoke = fault == 2;
        Assert.False(kernel.RevokePlatformMmio(subject, lease).IsSuccess);
        provider.Incarnation = 1;
        provider.ThrowAfterMmioRevoke = false;
        Assert.False(kernel.RevokePlatformMmio(subject, lease).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }
    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true)]
    [InlineData(PlatformAuthorityStatus.Denied, false)]
    [InlineData(PlatformAuthorityStatus.Faulted, false)]
    [InlineData(PlatformAuthorityStatus.Revoked, false)]
    public void MmioBindFailureNeedsStableExactNoEffectBeforeParentReclaim(PlatformAuthorityStatus status, bool reset)
    {
        MmioProvider provider = new MmioProvider { MmioMapStatus = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1237, 1770);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-failure", "registers", 128);
        if (reset) provider.BeforeMmioMap = () => Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        KernelResult<PlatformMmioLease> result = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(0, provider.MmioRevokeCalls);
        AssertMmioAdmissionBalanced(kernel, device);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MmioLostReceiptKeepsSourcePinAfterPendingAccountingSettles(bool reset)
    {
        MmioProvider provider = new MmioProvider { ThrowOnMmioMap = true };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1238, 1780);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-receipt-loss", "registers", 128);
        if (reset) provider.BeforeMmioMap = () => Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
        AssertMmioAdmissionBalanced(kernel, device);
        provider.ThrowOnMmioMap = false;
        provider.BeforeMmioMap = null;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokeCapability(capability).Error);
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokeCapability(capability).Error);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(0, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    private static void AssertMmioAdmissionBalanced(RuntimeKernel kernel, PlatformDeviceLease device)
    {
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        object record = records[device.LeaseId]!;
        Assert.Equal(0, (int)record.GetType().GetProperty("PendingMmioBinds")!.GetValue(record)!);
        ICollection pending = (global::System.Collections.ICollection)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMmioCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        Assert.Empty(pending);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MmioAdmissionGetterRevokeDeniesEffectOrClosesKnownLateReceipt(int read)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1235, 1750);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-getter-admission", "registers", 128);
        int reads = 0;
        provider.IncarnationRead = () =>
        {
            if (++reads != read) return;
            provider.IncarnationRead = null;
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
        };
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
        Assert.Equal(read == 1 ? 0 : 1, provider.MmioMapCalls);
        Assert.Equal(read == 1 ? 0 : 1, provider.MmioRevokeCalls);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MmioAdmissionStaleIdentityHasNoEffectAndProcessExitClosesKnownReceipt(bool exiting)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1236, 1760);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-process", "registers", 128);
        if (!exiting)
        {
            PlatformDeviceLease stale = device with { Generation = new PlatformDeviceLeaseGeneration(device.Generation.Value + 1) };
            Assert.Equal(KernelError.StaleGeneration,
                kernel.BindPlatformMmio(subject, stale, capability, 0, 64, PlatformMmioAccess.Read).Error);
            Assert.Equal(0, provider.MmioMapCalls);
            Assert.True(kernel.RevokeCapability(capability).IsSuccess);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            return;
        }
        provider.BeforeMmioMap = () =>
        {
            provider.BeforeMmioMap = null;
            Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        };
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MmioBindRevokeRejectsLatePublicationAndRetainsAmbiguousCleanup(bool fault)
    {
        MmioProvider provider = new MmioProvider { ThrowOnMmioRevoke = fault };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1233, 1730);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-admission", "registers", 128);
        provider.BeforeMmioMap = () =>
        {
            provider.BeforeMmioMap = null;
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        };
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(1, provider.MmioRevokeCalls);
        if (fault) Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        else Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(!fault, kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MmioBindParentInterlockRejectsNestedAdmissionAndResetPinsReceipt(bool reset)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1234, 1740);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-parent", "registers", 128);
        provider.BeforeMmioMap = () =>
        {
            provider.BeforeMmioMap = null;
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        KernelResult<PlatformMmioLease> result = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read);
        Assert.Equal(!reset, result.IsSuccess);
        Assert.Equal(1, provider.MmioMapCalls);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mmioLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        Assert.Single(records);
        if (reset) Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        else Assert.True(kernel.RevokeCapability(capability).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MmioClosureCallbackRejectsOverlappingExactClosure(bool concurrent, bool fault)
    {
        MmioProvider provider = new MmioProvider { ThrowOnMmioRevoke = fault };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1230, 1700);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-overlap", "registers", 128);
        PlatformMmioLease lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        KernelResult? nested = null;
        provider.BeforeMmioRevoke = () =>
        {
            provider.BeforeMmioRevoke = null;
            if (concurrent)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
            else nested = kernel.RevokePlatformMmio(subject, lease);
        };
        KernelResult result;
        if (concurrent)
        {
            Task<KernelResult> task = Task.Run(() => kernel.RevokePlatformMmio(subject, lease));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                nested = kernel.RevokePlatformMmio(subject, lease);
            }
            finally { release.Set(); }
            result = await task;
        }
        else result = kernel.RevokePlatformMmio(subject, lease);
        Assert.Equal(KernelError.PlatformBindingActive, nested!.Value.Error);
        Assert.Equal(1, provider.MmioRevokeCalls);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mmioLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        object record = records[lease.LeaseId]!;
        Assert.False((bool)record.GetType().GetProperty("ClosureInFlight")!.GetValue(record)!);
        Assert.True((bool)record.GetType().GetProperty("LocalAuthorizationRevoked")!.GetValue(record)!);
        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public void MmioClosureGenerationGetterPreservesExactInterlockAndPin(int read, int fault)
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1231, 1710);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-getter", "registers", 128);
        PlatformMmioLease lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        int reads = 0;
        provider.IncarnationRead = () =>
        {
            if (++reads != read) return;
            provider.IncarnationRead = null;
            Assert.Equal(KernelError.PlatformBindingActive, kernel.RevokePlatformMmio(subject, lease).Error);
            if (fault == 1) throw new InvalidOperationException("Injected MMIO generation read loss.");
            if (fault == 2) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        KernelResult result = kernel.RevokePlatformMmio(subject, lease);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mmioLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        object record = records[lease.LeaseId]!;
        Assert.False((bool)record.GetType().GetProperty("ClosureInFlight")!.GetValue(record)!);
        Assert.Equal(fault == 0 || read == 2 ? 1 : 0, provider.MmioRevokeCalls);
        if (fault == 0)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
        else
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            if (read == 2 && fault == 1)
            {
                Assert.True((bool)record.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(record)!);
                Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
                Assert.Equal(1, provider.MmioRevokeCalls);
                Assert.True(kernel.TerminateProcess(subject).IsSuccess);
                return;
            }
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
    }

    [Fact]
    public void MmioClosureGenericNotAcceptedPinsOriginalCapabilityCleanup()
    {
        MmioProvider provider = new MmioProvider { MmioRevokeStatus = PlatformAuthorityStatus.NotAccepted };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1232, 1720);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject, "device/mmio-retry", "registers", 128);
        PlatformMmioLease lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        provider.MmioRevokeStatus = null;
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Fact]
    public void CanonicalMmioCapabilityRoundTripsSemanticDeviceRegionAndExtent()
    {
        string id = CapabilityResourceIds.MmioRegion("device/uart0", "uart0/registers", 4096);

        Assert.True(CapabilityResourceIds.TryParseMmioRegion(id, out MmioRegionResourceId parsed));
        Assert.Equal("device/uart0", parsed.DeviceResourceId);
        Assert.Equal("uart0/registers", parsed.RegionResourceId);
        Assert.Equal(4096, parsed.ByteLength);
        Assert.False(CapabilityResourceIds.TryParseMmioRegion("uart0/registers", out _));
    }

    [Fact]
    public void ExactMmioCapabilityMaterializesBoundedLeaseAndDeviceRevokeDrainsItFirst()
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1201, 1210);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel, subject, ResourceKind.Device, "device/uart0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        CapabilityId mmioCapability = Mint(
            kernel,
            subject,
            ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/uart0", "uart0/registers", 4096),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);

        KernelResult<PlatformMmioLease> lease = kernel.BindPlatformMmio(
            subject,
            device,
            mmioCapability,
            256,
            128,
            PlatformMmioAccess.Read | PlatformMmioAccess.Write);

        Assert.True(lease.IsSuccess, lease.Message);
        Assert.Equal(new PlatformMmioRegionIdentity("uart0/registers", 4096), lease.Value!.Region);
        Assert.Equal(new PlatformMmioRange(256, 128), lease.Value.Range);
        Assert.Equal(1, provider.MmioMapCalls);

        KernelResult revokeDevice = kernel.RevokePlatformDevice(subject, device);
        Assert.True(revokeDevice.IsSuccess, revokeDevice.Message);
        Assert.Equal(
            new[] { "map-mmio:uart0/registers", "revoke-mmio:uart0/registers", "revoke-device:device/uart0" },
            provider.Log);
    }

    [Fact]
    public void AdmissionFailuresAreRejectedBeforeProviderMmioMapping()
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1202, 1220);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel, subject, ResourceKind.Device, "device/net0",
            CapabilityRights.Read | CapabilityRights.Configure);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject, binding, deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;

        CapabilityId wrongDevice = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/other", "net0/status", 256),
            CapabilityRights.Map | CapabilityRights.Read);
        CapabilityId tooSmallRights = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/net0", "net0/status", 256),
            CapabilityRights.Map);
        CapabilityId nonCanonical = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            "net0/status",
            CapabilityRights.Map | CapabilityRights.Read);

        Assert.Equal(
            KernelError.WrongCapabilityResource,
            kernel.BindPlatformMmio(subject, device, wrongDevice, 0, 16, PlatformMmioAccess.Read).Error);
        Assert.Equal(
            KernelError.InsufficientRights,
            kernel.BindPlatformMmio(subject, device, tooSmallRights, 0, 16, PlatformMmioAccess.Read).Error);
        Assert.Equal(
            KernelError.WrongCapabilityResource,
            kernel.BindPlatformMmio(subject, device, nonCanonical, 0, 16, PlatformMmioAccess.Read).Error);

        CapabilityId good = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/net0", "net0/status2", 256),
            CapabilityRights.Map | CapabilityRights.Read);
        Assert.Equal(
            KernelError.PlatformDenied,
            kernel.BindPlatformMmio(subject, device, good, 240, 32, PlatformMmioAccess.Read).Error);
        Assert.Equal(0, provider.MmioMapCalls);
    }

    [Fact]
    public void DeviceRightsMustCoverConfigureAndMmioAccess()
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1203, 1230);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel, subject, ResourceKind.Device, "device/readonly0", CapabilityRights.Read);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject, binding, deviceCapability, PlatformDeviceRights.Read).Value!;
        CapabilityId mmioCapability = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/readonly0", "readonly0/status", 64),
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult<PlatformMmioLease> result = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 8, PlatformMmioAccess.Read);

        Assert.Equal(KernelError.InsufficientRights, result.Error);
        Assert.Equal(0, provider.MmioMapCalls);
    }

    [Fact]
    public void MalformedProviderMmioAuthorityFailsClosedAndIsBestEffortRevoked()
    {
        MmioProvider provider = new MmioProvider { ReturnWrongRange = true };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1204, 1240);
        (PlatformDeviceLease device, CapabilityId mmioCapability) = CreateAuthorities(kernel, subject, "device/storage0", "storage0/admin", 1024);

        KernelResult<PlatformMmioLease> result = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 64, PlatformMmioAccess.Read);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(0, provider.MmioRevokeCalls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void DelegatedRootMmioRevokeClosesPublishedDescendant(int depth, bool fault)
    {
        MmioProvider provider = new MmioProvider { MmioRevokeStatus = fault ? PlatformAuthorityStatus.Faulted : null };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1205, 1250);
        (PlatformDeviceLease device, CapabilityId mmioCapability) = CreateAuthorities(kernel, subject, "device/audio0", "audio0/control", 512);
        CapabilityId root = Mint(kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/audio0", "audio0/control", 512), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Delegate);
        mmioCapability = root;
        for (int i = 0; i < depth; i++)
            mmioCapability = kernel.DelegateCapability(subject, subject, mmioCapability,
                CapabilityRights.Map | CapabilityRights.Read | (i + 1 < depth ? CapabilityRights.Delegate : CapabilityRights.None)).Value!.CapabilityId;
        PlatformMmioLease lease = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 32, 32, PlatformMmioAccess.Read).Value!;

        KernelResult revoke = kernel.RevokeCapability(root);

        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, revoke.Error);
            Assert.Equal(1, provider.MmioRevokeCalls);
            Assert.Equal(KernelError.CapabilityRevoked,
                kernel.PlatformAuthority.ValidateMmioLease(lease, device.DomainBinding.Subject).Error);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.False(kernel.RevokeCapability(root).IsSuccess);
            return;
        }
        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingRevoked, kernel.RevokePlatformMmio(subject, lease).Error);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Fact]
    public void MmioCapabilityRevokeClosesOnlyDerivedMmioAuthority()
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1205, 1250);
        (PlatformDeviceLease device, CapabilityId mmioCapability) = CreateAuthorities(kernel, subject, "device/audio0", "audio0/control", 512);
        PlatformMmioLease lease = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 32, 32, PlatformMmioAccess.Read).Value!;

        KernelResult revoke = kernel.RevokeCapability(mmioCapability);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingRevoked, kernel.RevokePlatformMmio(subject, lease).Error);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesMmioThenDeviceThenDomain()
    {
        MmioProvider provider = new MmioProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1206, 1260);
        (PlatformDeviceLease device, CapabilityId mmioCapability) = CreateAuthorities(kernel, subject, "device/controller0", "controller0/registers", 2048);
        Assert.True(kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 256, PlatformMmioAccess.Read).IsSuccess);

        KernelResult terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(
            new[]
            {
                "map-mmio:controller0/registers",
                "revoke-mmio:controller0/registers",
                "revoke-device:device/controller0",
                "revoke-domain",
            },
            provider.Log);
    }

    [Fact]
    public void MmioCloseFaultPinsTeardownBeforeDeviceAndDomainClose()
    {
        MmioProvider provider = new MmioProvider { MmioRevokeStatus = PlatformAuthorityStatus.Faulted };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle subject) = TestFixtures.Create(kernel, 1207, 1270);
        (PlatformDeviceLease device, CapabilityId mmioCapability) = CreateAuthorities(kernel, subject, "device/fault0", "fault0/registers", 128);
        Assert.True(kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 16, PlatformMmioAccess.Read).IsSuccess);

        KernelResult terminate = kernel.TerminateProcess(subject);

        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, kernel.QueryProcessTeardown(subject).Value!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevokedOrThrowingMmioClosureKeepsDevicePinned(bool throws)
    {
        MmioProvider provider = new MmioProvider
        {
            MmioRevokeStatus = throws ? null : PlatformAuthorityStatus.Revoked,
            ThrowOnMmioRevoke = throws,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, throws ? 1208UL : 1209UL,
            throws ? 1280UL : 1290UL);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject,
            "device/ambiguous-mmio", "ambiguous/registers", 128);
        PlatformMmioLease lease = kernel.BindPlatformMmio(subject, device, capability,
            0, 16, PlatformMmioAccess.Read).Value!;

        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformMmio(subject, lease).Error);
        provider.MmioRevokeStatus = null;
        provider.ThrowOnMmioRevoke = false;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformMmio(subject, lease).Error);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedMmioCleanupWithoutExactSuccessPinsParentDevice(bool throws)
    {
        MmioProvider provider = new MmioProvider
        {
            ReturnWrongRange = true,
            MmioRevokeStatus = throws ? null : PlatformAuthorityStatus.Revoked,
            ThrowOnMmioRevoke = throws,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, throws ? 1210UL : 1211UL,
            throws ? 1300UL : 1310UL);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject,
            "device/malformed-mmio", "malformed/registers", 128);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformMmio(subject, device, capability,
                0, 16, PlatformMmioAccess.Read).Error);
        Assert.Equal(0, provider.MmioRevokeCalls);
        provider.MmioRevokeStatus = null;
        provider.ThrowOnMmioRevoke = false;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, device).Error);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Fact]
    public void MmioMapReceiptLossPinsParentDevice()
    {
        MmioProvider provider = new MmioProvider { ThrowOnMmioMap = true };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1212, 1320);
        (PlatformDeviceLease device, CapabilityId capability) = CreateAuthorities(kernel, subject,
            "device/lost-mmio-receipt", "lost/registers", 128);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformMmio(subject, device, capability,
                0, 16, PlatformMmioAccess.Read).Error);
        provider.ThrowOnMmioMap = false;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, device).Error);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Fact]
    public void PublicMmioSurfaceCarriesNoProviderOrHardwareAuthorityIdentity()
    {
        Type[] surface = new[]
        {
            typeof(PlatformMmioAccess),
            typeof(PlatformMmioRegionIdentity),
            typeof(PlatformMmioRange),
            typeof(PlatformMmioLeaseId),
            typeof(PlatformMmioLeaseGeneration),
            typeof(PlatformMmioLease),
        };
        string[] forbidden = new[]
        {
            "PlatformProvider",
            "HybridCPU",
            "Neutral",
            "Physical",
            "PageTable",
            "Pte",
            "BarNumber",
            "InterruptVector",
            "Iommu",
            "DmaWindow",
            "Vmcs",
            "Vmx",
            "Lane",
            "Opcode",
        };

        foreach (Type? type in surface)
        foreach (MemberInfo member in type.GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
                string signature = member.ToString() ?? member.Name;
            foreach (string? term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (PlatformDeviceLease Device, CapabilityId MmioCapability) CreateAuthorities(
        RuntimeKernel kernel,
        ProcessHandle subject,
        string deviceResourceId,
        string mmioResourceId,
        long byteLength)
    {
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            deviceResourceId,
            CapabilityRights.Read | CapabilityRights.Configure);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        CapabilityId mmioCapability = Mint(
            kernel,
            subject,
            ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion(deviceResourceId, mmioResourceId, byteLength),
            CapabilityRights.Map | CapabilityRights.Read);
        return (device, mmioCapability);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private sealed class MmioProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformMmioLeaseProvider,
        IPlatformProviderIncarnationSource
    {
        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease> _domains = [];
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderMmioLeaseId, PlatformProviderMmioLease> _mmio = [];
        private ulong _nextDomain = 1;
        private ulong _nextDevice = 1;
        private ulong _nextMmio = 1;

        public bool ReturnWrongRange { get; set; }
        public int ReturnedMutation { get; set; } = -1;
        public bool ThrowOnMmioMap { get; set; }
        public PlatformAuthorityStatus? MmioMapStatus { get; set; }
        public PlatformAuthorityStatus? MmioMapReplyStatus { get; set; }
        public Action? AfterMmioMapEffect { get; set; }
        public PlatformAuthorityStatus? MmioRevokeStatus { get; set; }
        public PlatformAuthorityStatus? MmioRevokeReplyStatus { get; set; }
        public Action? AfterMmioRevokeEffect { get; set; }
        public bool ThrowOnMmioRevoke { get; set; }
        public bool ThrowAfterMmioRevoke { get; set; }
        public ulong Incarnation { get; set; } = 1;
        public Action? BeforeMmioRevoke { get; set; }
        public Action? BeforeMmioMap { get; set; }
        public Action? IncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get { IncarnationRead?.Invoke(); return new PlatformProviderIncarnation(Incarnation); }
        }
        public int MmioMapCalls { get; private set; }
        public int MmioRevokeCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public int DomainRevokeCalls { get; private set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("mmio-test"),
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
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.MmioMapping,
                PlatformMmioLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_devices.Values.Any(device => device.DomainLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Device remains live.");
            DomainRevokeCalls++;
            Log.Add("revoke-domain");
            _domains.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            PlatformProviderDeviceLease lease = new PlatformProviderDeviceLease(
                new PlatformProviderDeviceLeaseId(_nextDevice++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                device,
                rights);
            _devices.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            if (_mmio.Values.Any(mmio => mmio.DeviceLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "MMIO remains live.");
            DeviceRevokeCalls++;
            Log.Add($"revoke-device:{lease.Device.ResourceId}");
            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(
            PlatformProviderDeviceLease deviceLease,
            PlatformMmioRegionIdentity region,
            PlatformMmioRange range,
            PlatformMmioAccess access)
        {
            BeforeMmioMap?.Invoke();
            MmioMapCalls++;
            if (MmioMapStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(status, "Injected MMIO admission response.");
            Log.Add($"map-mmio:{region.ResourceId}");
            PlatformMmioRange returnedRange = ReturnWrongRange
                ? new PlatformMmioRange(range.Offset + 1, range.Length)
                : range;
            PlatformProviderMmioLease lease = new PlatformProviderMmioLease(
                new PlatformProviderMmioLeaseId(_nextMmio++),
                new PlatformProviderLeaseGeneration(1),
                deviceLease,
                region,
                returnedRange,
                access);
            lease = ReturnedMutation switch
            {
                0 => lease with { DeviceLease = deviceLease with { Generation = new(99) } },
                1 => lease with { Region = new("wrong-region", region.ByteLength) },
                2 => lease with { Range = new(range.Offset + 1, range.Length) },
                3 => lease with { Access = PlatformMmioAccess.Write },
                4 => lease with { LeaseId = new(0) },
                5 => lease with { Generation = new(0) },
                _ => lease,
            };
            _mmio[lease.LeaseId] = lease;
            AfterMmioMapEffect?.Invoke();
            if (MmioMapReplyStatus is { } replyStatus)
                return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(replyStatus, "Original map failed after effect.");
            if (ThrowOnMmioMap) throw new InvalidOperationException("Injected MMIO map receipt loss.");
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease)
        {
            BeforeMmioRevoke?.Invoke();
            MmioRevokeCalls++;
            Log.Add($"revoke-mmio:{lease.Region.ResourceId}");
            if (ThrowOnMmioRevoke) throw new InvalidOperationException("Injected MMIO revoke receipt loss.");
            if (MmioRevokeStatus is { } status)
                return PlatformAuthorityResult.Fail(status, "Injected MMIO revoke fault.");
            _mmio.Remove(lease.LeaseId);
            AfterMmioRevokeEffect?.Invoke();
            if (ThrowAfterMmioRevoke) throw new InvalidOperationException("MMIO removed but response lost");
            if (MmioRevokeReplyStatus is { } replyStatus)
                return PlatformAuthorityResult.Fail(replyStatus, "Injected failed reply after original MMIO effect.");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by MMIO tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by MMIO tests.");
    }
}
