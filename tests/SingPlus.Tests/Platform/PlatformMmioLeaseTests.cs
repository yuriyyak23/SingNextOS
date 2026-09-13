using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformMmioLeaseTests
{
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
        IPlatformMmioLeaseProvider
    {
        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease> _domains = [];
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderMmioLeaseId, PlatformProviderMmioLease> _mmio = [];
        private ulong _nextDomain = 1;
        private ulong _nextDevice = 1;
        private ulong _nextMmio = 1;

        public bool ReturnWrongRange { get; set; }
        public PlatformAuthorityStatus? MmioRevokeStatus { get; set; }
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
            MmioMapCalls++;
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
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease)
        {
            MmioRevokeCalls++;
            Log.Add($"revoke-mmio:{lease.Region.ResourceId}");
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
