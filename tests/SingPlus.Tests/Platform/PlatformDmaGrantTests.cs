using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaGrantTests
{
    [Fact]
    public void ExactDeviceAndMappingMaterializeBoundedAdmissionOnlyGrant()
    {
        var scenario = CreateScenario(1301, 1310,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        var result = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            32,
            128,
            PlatformDmaDirection.DeviceReadsMemory);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(scenario.Device, result.Value!.DeviceLease);
        Assert.Equal(scenario.Mapping, result.Value.Mapping);
        Assert.Equal(new PlatformDmaRange(32, 128), result.Value.Range);
        Assert.Equal(PlatformDmaDirection.DeviceReadsMemory, result.Value.Direction);
        Assert.Equal(1, scenario.Provider.DmaBindCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.DoesNotContain(
            typeof(RuntimeKernel).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            static method => method.Name.Contains("SubmitDma", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingDeviceOrRegionAuthorityIsRejectedBeforeProviderDmaAdmission()
    {
        var scenario = CreateScenario(1302, 1320,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);

        var missingDevice = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            default,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(KernelError.PlatformBindingNotFound, missingDevice.Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);

        var missingMapping = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            default,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(KernelError.PlatformBindingNotFound, missingMapping.Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);
    }

    [Fact]
    public void InvalidRangeDirectionAndAccessFailBeforeProviderDmaAdmission()
    {
        var scenario = CreateScenario(1303, 1330,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);

        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.BindPlatformDma(
                scenario.Subject,
                scenario.Device,
                scenario.Mapping,
                240,
                32,
                PlatformDmaDirection.DeviceReadsMemory).Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);

        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.BindPlatformDma(
                scenario.Subject,
                scenario.Device,
                scenario.Mapping,
                0,
                32,
                (PlatformDmaDirection)0xff).Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);

        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.BindPlatformDma(
                scenario.Subject,
                scenario.Device,
                scenario.Mapping,
                0,
                32,
                PlatformDmaDirection.DeviceWritesMemory).Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);
    }

    [Fact]
    public void DeviceRightsMustCoverConfigureAndDmaDirection()
    {
        var scenario = CreateScenario(1304, 1340,
            PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        var result = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);
    }

    [Fact]
    public void MappingRevokeClosesDmaGrantBeforeMappingClosure()
    {
        var scenario = CreateScenario(1305, 1350,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        var grant = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).Value!;

        var revoke = scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject,
            scenario.Mapping);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(
            new[] { "bind-dma", "revoke-dma", "revoke-mapping" },
            scenario.Provider.Log.Where(static entry =>
                entry is "bind-dma" or "revoke-dma" or "revoke-mapping").ToArray());
        Assert.Equal(
            KernelError.PlatformBindingRevoked,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, grant).Error);
    }

    [Fact]
    public void DeviceRevokeClosesDmaGrantBeforeDeviceClosure()
    {
        var scenario = CreateScenario(1306, 1360,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        Assert.True(scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);

        var revoke = scenario.Kernel.RevokePlatformDevice(
            scenario.Subject,
            scenario.Device);

        Assert.True(revoke.IsSuccess, revoke.Message);
        var relevant = scenario.Provider.Log.Where(static entry =>
            entry is "bind-dma" or "revoke-dma" or "revoke-device").ToArray();
        Assert.Equal(new[] { "bind-dma", "revoke-dma", "revoke-device" }, relevant);
    }

    [Fact]
    public void DmaRevokeFaultPinsMappingClosureAndKeepsReservationLive()
    {
        var scenario = CreateScenario(1307, 1370,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        scenario.Provider.DmaRevokeStatus = PlatformAuthorityStatus.Faulted;
        Assert.True(scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            32,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);

        var revoke = scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject,
            scenario.Mapping);

        Assert.Equal(KernelError.PlatformFaulted, revoke.Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(PlatformExternalClosureState.Active,
            scenario.Kernel.QueryPlatformRegionMappingLifecycle(
                scenario.Subject,
                scenario.Mapping.Mapping).Value!.PlatformClosure);
    }

    [Fact]
    public void DmaFeatureIsAdmissionOnlyAndPublicGrantContainsNoProviderOrHardwareIdentity()
    {
        var scenario = CreateScenario(1308, 1380,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        var feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaGrantContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        var surface = new[]
        {
            typeof(PlatformDmaDirection),
            typeof(PlatformDmaRange),
            typeof(PlatformDmaGrantId),
            typeof(PlatformDmaGrantGeneration),
            typeof(PlatformDmaGrant),
        };
        var forbidden = new[]
        {
            "PlatformProvider",
            "Neutral",
            "Physical",
            "BusAddress",
            "Iommu",
            "PageTable",
            "Pte",
            "Descriptor",
            "ScatterGather",
            "Queue",
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

    private static Scenario CreateScenario(
        ulong processId,
        ulong domainId,
        PlatformDeviceRights deviceRights,
        PlatformMemoryAccess mappingAccess)
    {
        var provider = new DmaProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma0",
            ToCapabilityRights(deviceRights));
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;

        var region = kernel.AllocateBuffer<byte>(subject, 512).Value!;
        var regionRights = CapabilityRights.Map;
        if ((mappingAccess & PlatformMemoryAccess.Read) != 0) regionRights |= CapabilityRights.Read;
        if ((mappingAccess & PlatformMemoryAccess.Write) != 0) regionRights |= CapabilityRights.Write;
        var regionCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            regionRights);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            regionCapability,
            region.Handle,
            64,
            256,
            mappingAccess).Value!;

        return new Scenario(kernel, provider, subject, device, mapping);
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

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        var result = CapabilityRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0) result |= CapabilityRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0) result |= CapabilityRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0) result |= CapabilityRights.Configure;
        return result;
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        DmaProvider Provider,
        ProcessHandle Subject,
        PlatformDeviceLease Device,
        PlatformOwnedRegionSliceMapping Mapping);

    private sealed class DmaProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionRevocationProvider,
        IPlatformDmaGrantProvider
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformOperationId, PlatformCompletionReceipt> _operations = [];
        private PlatformProviderDomainLease? _domain;
        private ulong _nextDevice = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextOperation = 1;

        public int DmaBindCalls { get; private set; }
        public int DmaRevokeCalls { get; private set; }
        public int MappingRevokeCalls { get; private set; }
        public PlatformAuthorityStatus? DmaRevokeStatus { get; set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("dma-admission-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaGrantContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(1),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domain = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            PlatformAuthorityResult.Ok();

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
            if (_grants.Values.Any(grant => grant.DeviceLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "DMA grant remains live.");
            Log.Add("revoke-device");
            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            var mapped = MapOwnedRegionSlice(
                domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return mapped.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(mapped.Status, mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (_domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain.");
            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            var mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, mapped);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapped);
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
            PlatformDmaGrantRequest request)
        {
            DmaBindCalls++;
            Log.Add("bind-dma");
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) ||
                !_mappings.TryGetValue(request.MappingLease.MappingId, out var mapped) ||
                mapped.Slice != request.MappingSlice)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Exact device or mapping is not live.");
            }
            var grant = new PlatformProviderDmaGrant(
                new PlatformProviderDmaGrantId(_nextGrant++),
                new PlatformProviderLeaseGeneration(1),
                request.DeviceLease,
                request.MappingLease,
                request.Range,
                request.Direction);
            _grants.Add(grant.GrantId, grant);
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
        }

        public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
        {
            DmaRevokeCalls++;
            Log.Add("revoke-dma");
            if (DmaRevokeStatus is { } status)
                return PlatformAuthorityResult.Fail(status, "Injected DMA grant revoke fault.");
            _grants.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (_grants.Values.Any(grant => grant.MappingLease == mapping))
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }
            MappingRevokeCalls++;
            Log.Add("revoke-mapping");
            _mappings.Remove(mapping.MappingId);
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            var receipt = new PlatformCompletionReceipt(
                operation.OperationId,
                operation.Generation,
                operation.DomainLease,
                PlatformCompletionState.Closed);
            _operations.Add(operation.OperationId, receipt);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation) =>
            _operations.TryGetValue(operation.OperationId, out var receipt)
                ? PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(receipt)
                : PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown operation.");
    }
}
