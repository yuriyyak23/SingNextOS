using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformMmioLeaseTests
{
    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true)]
    [InlineData(PlatformAuthorityStatus.Denied, false)]
    [InlineData(PlatformAuthorityStatus.Faulted, false)]
    [InlineData(PlatformAuthorityStatus.Revoked, false)]
    public void MmioBindFailureNeedsStableExactNoEffectBeforeParentReclaim(PlatformAuthorityStatus status, bool reset)
    {
        var provider = new MmioProvider { MmioMapStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1237, 1770);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-failure", "registers", 128);
        if (reset) provider.BeforeMmioMap = () => Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        var result = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(0, provider.MmioRevokeCalls);
        AssertMmioAdmissionBalanced(kernel, device);
        var noEffect = status == PlatformAuthorityStatus.NotAccepted && !reset;
        Assert.Equal(noEffect, kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(noEffect, kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(noEffect ? 1 : 0, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MmioLostReceiptKeepsSourcePinAfterPendingAccountingSettles(bool reset)
    {
        var provider = new MmioProvider { ThrowOnMmioMap = true };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1238, 1780);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-receipt-loss", "registers", 128);
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
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = records[device.LeaseId]!;
        Assert.Equal(0, (int)record.GetType().GetProperty("PendingMmioBinds")!.GetValue(record)!);
        var pending = (global::System.Collections.ICollection)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMmioCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        Assert.Empty(pending);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MmioAdmissionGetterRevokeDeniesEffectOrClosesKnownLateReceipt(int read)
    {
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1235, 1750);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-getter-admission", "registers", 128);
        var reads = 0;
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
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1236, 1760);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-process", "registers", 128);
        if (!exiting)
        {
            var stale = device with { Generation = new PlatformDeviceLeaseGeneration(device.Generation.Value + 1) };
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
        var provider = new MmioProvider { ThrowOnMmioRevoke = fault };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1233, 1730);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-admission", "registers", 128);
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
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1234, 1740);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-parent", "registers", 128);
        provider.BeforeMmioMap = () =>
        {
            provider.BeforeMmioMap = null;
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Error);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        var result = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read);
        Assert.Equal(!reset, result.IsSuccess);
        Assert.Equal(1, provider.MmioMapCalls);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
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
        var provider = new MmioProvider { ThrowOnMmioRevoke = fault };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1230, 1700);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-overlap", "registers", 128);
        var lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
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
            var task = Task.Run(() => kernel.RevokePlatformMmio(subject, lease));
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
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mmioLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = records[lease.LeaseId]!;
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
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1231, 1710);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-getter", "registers", 128);
        var lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        var reads = 0;
        provider.IncarnationRead = () =>
        {
            if (++reads != read) return;
            provider.IncarnationRead = null;
            Assert.Equal(KernelError.PlatformBindingActive, kernel.RevokePlatformMmio(subject, lease).Error);
            if (fault == 1) throw new InvalidOperationException("Injected MMIO generation read loss.");
            if (fault == 2) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        var result = kernel.RevokePlatformMmio(subject, lease);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mmioLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = records[lease.LeaseId]!;
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
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
    }

    [Fact]
    public void MmioClosureNotAcceptedAllowsExactExistingConsumerRetry()
    {
        var provider = new MmioProvider { MmioRevokeStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1232, 1720);
        var (device, capability) = CreateAuthorities(kernel, subject, "device/mmio-retry", "registers", 128);
        var lease = kernel.BindPlatformMmio(subject, device, capability, 0, 64, PlatformMmioAccess.Read).Value!;
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(1, provider.MmioRevokeCalls);
        provider.MmioRevokeStatus = null;
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(2, provider.MmioRevokeCalls);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Fact]
    public void CanonicalMmioCapabilityRoundTripsSemanticDeviceRegionAndExtent()
    {
        var id = CapabilityResourceIds.MmioRegion("device/uart0", "uart0/registers", 4096);

        Assert.True(CapabilityResourceIds.TryParseMmioRegion(id, out var parsed));
        Assert.Equal("device/uart0", parsed.DeviceResourceId);
        Assert.Equal("uart0/registers", parsed.RegionResourceId);
        Assert.Equal(4096, parsed.ByteLength);
        Assert.False(CapabilityResourceIds.TryParseMmioRegion("uart0/registers", out _));
    }

    [Fact]
    public void ExactMmioCapabilityMaterializesBoundedLeaseAndDeviceRevokeDrainsItFirst()
    {
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1201, 1210);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel, subject, ResourceKind.Device, "device/uart0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var mmioCapability = Mint(
            kernel,
            subject,
            ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/uart0", "uart0/registers", 4096),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);

        var lease = kernel.BindPlatformMmio(
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

        var revokeDevice = kernel.RevokePlatformDevice(subject, device);
        Assert.True(revokeDevice.IsSuccess, revokeDevice.Message);
        Assert.Equal(
            new[] { "map-mmio:uart0/registers", "revoke-mmio:uart0/registers", "revoke-device:device/uart0" },
            provider.Log);
    }

    [Fact]
    public void AdmissionFailuresAreRejectedBeforeProviderMmioMapping()
    {
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1202, 1220);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel, subject, ResourceKind.Device, "device/net0",
            CapabilityRights.Read | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject, binding, deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;

        var wrongDevice = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/other", "net0/status", 256),
            CapabilityRights.Map | CapabilityRights.Read);
        var tooSmallRights = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/net0", "net0/status", 256),
            CapabilityRights.Map);
        var nonCanonical = Mint(
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

        var good = Mint(
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
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1203, 1230);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel, subject, ResourceKind.Device, "device/readonly0", CapabilityRights.Read);
        var device = kernel.BindPlatformDevice(
            subject, binding, deviceCapability, PlatformDeviceRights.Read).Value!;
        var mmioCapability = Mint(
            kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/readonly0", "readonly0/status", 64),
            CapabilityRights.Map | CapabilityRights.Read);

        var result = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 8, PlatformMmioAccess.Read);

        Assert.Equal(KernelError.InsufficientRights, result.Error);
        Assert.Equal(0, provider.MmioMapCalls);
    }

    [Fact]
    public void MalformedProviderMmioAuthorityFailsClosedAndIsBestEffortRevoked()
    {
        var provider = new MmioProvider { ReturnWrongRange = true };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1204, 1240);
        var (device, mmioCapability) = CreateAuthorities(kernel, subject, "device/storage0", "storage0/admin", 1024);

        var result = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 64, PlatformMmioAccess.Read);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, provider.MmioMapCalls);
        Assert.Equal(1, provider.MmioRevokeCalls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void DelegatedRootMmioRevokeClosesPublishedDescendant(int depth, bool fault)
    {
        var provider = new MmioProvider { MmioRevokeStatus = fault ? PlatformAuthorityStatus.Faulted : null };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1205, 1250);
        var (device, mmioCapability) = CreateAuthorities(kernel, subject, "device/audio0", "audio0/control", 512);
        var root = Mint(kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/audio0", "audio0/control", 512), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Delegate);
        mmioCapability = root;
        for (var i = 0; i < depth; i++)
            mmioCapability = kernel.DelegateCapability(subject, subject, mmioCapability,
                CapabilityRights.Map | CapabilityRights.Read | (i + 1 < depth ? CapabilityRights.Delegate : CapabilityRights.None)).Value!.CapabilityId;
        var lease = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 32, 32, PlatformMmioAccess.Read).Value!;

        var revoke = kernel.RevokeCapability(root);

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
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1205, 1250);
        var (device, mmioCapability) = CreateAuthorities(kernel, subject, "device/audio0", "audio0/control", 512);
        var lease = kernel.BindPlatformMmio(
            subject, device, mmioCapability, 32, 32, PlatformMmioAccess.Read).Value!;

        var revoke = kernel.RevokeCapability(mmioCapability);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(1, provider.MmioRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingRevoked, kernel.RevokePlatformMmio(subject, lease).Error);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesMmioThenDeviceThenDomain()
    {
        var provider = new MmioProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1206, 1260);
        var (device, mmioCapability) = CreateAuthorities(kernel, subject, "device/controller0", "controller0/registers", 2048);
        Assert.True(kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 256, PlatformMmioAccess.Read).IsSuccess);

        var terminate = kernel.TerminateProcess(subject);

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
        var provider = new MmioProvider { MmioRevokeStatus = PlatformAuthorityStatus.Faulted };
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = TestFixtures.Create(kernel, 1207, 1270);
        var (device, mmioCapability) = CreateAuthorities(kernel, subject, "device/fault0", "fault0/registers", 128);
        Assert.True(kernel.BindPlatformMmio(
            subject, device, mmioCapability, 0, 16, PlatformMmioAccess.Read).IsSuccess);

        var terminate = kernel.TerminateProcess(subject);

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
        var provider = new MmioProvider
        {
            MmioRevokeStatus = throws ? null : PlatformAuthorityStatus.Revoked,
            ThrowOnMmioRevoke = throws,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, throws ? 1208UL : 1209UL,
            throws ? 1280UL : 1290UL);
        var (device, capability) = CreateAuthorities(kernel, subject,
            "device/ambiguous-mmio", "ambiguous/registers", 128);
        var lease = kernel.BindPlatformMmio(subject, device, capability,
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
        var provider = new MmioProvider
        {
            ReturnWrongRange = true,
            MmioRevokeStatus = throws ? null : PlatformAuthorityStatus.Revoked,
            ThrowOnMmioRevoke = throws,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, throws ? 1210UL : 1211UL,
            throws ? 1300UL : 1310UL);
        var (device, capability) = CreateAuthorities(kernel, subject,
            "device/malformed-mmio", "malformed/registers", 128);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformMmio(subject, device, capability,
                0, 16, PlatformMmioAccess.Read).Error);
        Assert.Equal(1, provider.MmioRevokeCalls);
        provider.MmioRevokeStatus = null;
        provider.ThrowOnMmioRevoke = false;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, device).Error);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Fact]
    public void MmioMapReceiptLossPinsParentDevice()
    {
        var provider = new MmioProvider { ThrowOnMmioMap = true };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1212, 1320);
        var (device, capability) = CreateAuthorities(kernel, subject,
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
        var surface = new[]
        {
            typeof(PlatformMmioAccess),
            typeof(PlatformMmioRegionIdentity),
            typeof(PlatformMmioRange),
            typeof(PlatformMmioLeaseId),
            typeof(PlatformMmioLeaseGeneration),
            typeof(PlatformMmioLease),
        };
        var forbidden = new[]
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

        foreach (var type in surface)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var term in forbidden)
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
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            deviceResourceId,
            CapabilityRights.Read | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var mmioCapability = Mint(
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
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var minted = kernel.MintCapability(
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
        public bool ThrowOnMmioMap { get; set; }
        public PlatformAuthorityStatus? MmioMapStatus { get; set; }
        public PlatformAuthorityStatus? MmioRevokeStatus { get; set; }
        public bool ThrowOnMmioRevoke { get; set; }
        public Action? BeforeMmioRevoke { get; set; }
        public Action? BeforeMmioMap { get; set; }
        public Action? IncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get { IncarnationRead?.Invoke(); return new PlatformProviderIncarnation(1); }
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
            var lease = new PlatformProviderDomainLease(
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
            var lease = new PlatformProviderDeviceLease(
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
            var returnedRange = ReturnWrongRange
                ? new PlatformMmioRange(range.Offset + 1, range.Length)
                : range;
            var lease = new PlatformProviderMmioLease(
                new PlatformProviderMmioLeaseId(_nextMmio++),
                new PlatformProviderLeaseGeneration(1),
                deviceLease,
                region,
                returnedRange,
                access);
            _mmio[lease.LeaseId] = lease;
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
