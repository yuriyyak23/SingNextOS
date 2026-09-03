using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaVisibilityTests
{
    [Fact]
    public void ExactGrantPrepareAndPostWriteAcquireReturnLocalCycleEvidence()
    {
        var scenario = CreateScenario(
            1401,
            1410,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);

        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(prepare.Value!.IsSatisfied);
        Assert.Equal(scenario.Grant.GrantId, prepare.Value.GrantId);
        Assert.Equal(scenario.Grant.Generation, prepare.Value.GrantGeneration);
        Assert.Equal(scenario.Grant.Direction, prepare.Value.Direction);
        Assert.NotEqual(0UL, prepare.Value.Cycle.Value);
        Assert.Equal(1, scenario.Provider.PrepareCalls);
        Assert.Equal(0, scenario.Provider.AcquireCalls);

        var acquire = scenario.Kernel.AcquirePlatformDmaForCpu(
            scenario.Subject,
            scenario.Grant);
        Assert.True(acquire.IsSuccess, acquire.Message);
        Assert.True(acquire.Value!.IsSatisfied);
        Assert.Equal(prepare.Value.Cycle, acquire.Value.Cycle);
        Assert.Equal(PlatformMemoryAcquireRequirement.AcquisitionFence, acquire.Value.Requirement);
        Assert.Equal(1, scenario.Provider.AcquireCalls);
    }

    [Fact]
    public void AcquireRequiresPreparedCycleAndDeviceWriteDirection()
    {
        var writer = CreateScenario(
            1402,
            1420,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        var early = writer.Kernel.AcquirePlatformDmaForCpu(writer.Subject, writer.Grant);
        Assert.Equal(KernelError.PlatformDenied, early.Error);
        Assert.Equal(0, writer.Provider.AcquireCalls);

        var reader = CreateScenario(
            1403,
            1430,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(reader.Kernel.PreparePlatformDmaForDevice(reader.Subject, reader.Grant).IsSuccess);
        var unnecessary = reader.Kernel.AcquirePlatformDmaForCpu(reader.Subject, reader.Grant);
        Assert.Equal(KernelError.PlatformDenied, unnecessary.Error);
        Assert.Equal(0, reader.Provider.AcquireCalls);
    }

    [Fact]
    public void AcquiredCycleIsConsumedAndReprepareCreatesFreshLocalAndProviderCycles()
    {
        var scenario = CreateScenario(
            1404,
            1440,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);

        var first = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(first.IsSuccess);
        Assert.True(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).Error);

        var second = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value!.Cycle, second.Value!.Cycle);
        Assert.True(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(2, scenario.Provider.PrepareCalls);
        Assert.Equal(2, scenario.Provider.AcquireCalls);
    }

    [Fact]
    public void StaleOrForgedGrantFailsBeforeProviderVisibilityCall()
    {
        var scenario = CreateScenario(
            1405,
            1450,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceReadsMemory);

        var stale = scenario.Grant with
        {
            Generation = new PlatformDmaGrantGeneration(scenario.Grant.Generation.Value + 1),
        };
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, stale).Error);
        Assert.Equal(0, scenario.Provider.PrepareCalls);

        var forged = scenario.Grant with
        {
            Range = new PlatformDmaRange(scenario.Grant.Range.Offset + 1, scenario.Grant.Range.Length),
        };
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, forged).Error);
        Assert.Equal(0, scenario.Provider.PrepareCalls);
    }

    [Fact]
    public void MalformedProviderPrepareOrAcquireEvidenceFailsClosed()
    {
        var prepareScenario = CreateScenario(
            1406,
            1460,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        prepareScenario.Provider.MalformedPrepare = true;
        Assert.Equal(
            KernelError.PlatformFaulted,
            prepareScenario.Kernel.PreparePlatformDmaForDevice(
                prepareScenario.Subject,
                prepareScenario.Grant).Error);

        var acquireScenario = CreateScenario(
            1407,
            1470,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(acquireScenario.Kernel.PreparePlatformDmaForDevice(
            acquireScenario.Subject,
            acquireScenario.Grant).IsSuccess);
        acquireScenario.Provider.MalformedAcquire = true;
        Assert.Equal(
            KernelError.PlatformFaulted,
            acquireScenario.Kernel.AcquirePlatformDmaForCpu(
                acquireScenario.Subject,
                acquireScenario.Grant).Error);
    }

    [Fact]
    public void DmaVisibilityV2RemainsRuntimeAdmissionAndPublicEvidenceHasNoProviderOrCompletionIdentity()
    {
        var scenario = CreateScenario(
            1408,
            1480,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceReadsMemory);
        var feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaVisibilityContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        var surface = new[]
        {
            typeof(PlatformDmaVisibilityCycle),
            typeof(PlatformDmaPrepareEvidence),
            typeof(PlatformDmaAcquireEvidence),
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
            "Queue",
            "Vector",
            "Controller",
            "Completion",
            "Operation",
            "Submit",
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

        var methods = typeof(RuntimeKernel)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(static method => method.Name)
            .ToArray();
        Assert.Contains(nameof(RuntimeKernel.PreparePlatformDmaForDevice), methods);
        Assert.Contains(nameof(RuntimeKernel.AcquirePlatformDmaForCpu), methods);
        Assert.DoesNotContain(methods, static name =>
            name.Contains("SubmitDma", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("CompleteDma", StringComparison.OrdinalIgnoreCase));
    }

    private static Scenario CreateScenario(
        ulong processId,
        ulong domainId,
        PlatformDeviceRights deviceRights,
        PlatformMemoryAccess mappingAccess,
        PlatformDmaDirection direction)
    {
        var provider = new VisibilityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-visibility0",
            ToCapabilityRights(deviceRights));
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;

        var buffer = kernel.AllocateBuffer<byte>(subject, 512).Value!;
        var memoryRights = CapabilityRights.Map;
        if ((mappingAccess & PlatformMemoryAccess.Read) != 0) memoryRights |= CapabilityRights.Read;
        if ((mappingAccess & PlatformMemoryAccess.Write) != 0) memoryRights |= CapabilityRights.Write;
        var memoryCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            memoryRights);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            memoryCapability,
            buffer.Handle,
            64,
            256,
            mappingAccess).Value!;
        var grant = kernel.BindPlatformDma(
            subject,
            device,
            mapping,
            32,
            64,
            direction).Value!;

        return new Scenario(kernel, provider, subject, grant);
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
        VisibilityProvider Provider,
        ProcessHandle Subject,
        PlatformDmaGrant Grant);

    private sealed class VisibilityProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformDmaGrantProvider,
        IPlatformDmaVisibilityProvider
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaVisibilityCycle> _cycles = [];
        private PlatformProviderDomainLease? _domain;
        private ulong _nextDevice = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextCycle = 1;

        public int PrepareCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public bool MalformedPrepare { get; set; }
        public bool MalformedAcquire { get; set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("dma-visibility-test"),
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
                PlatformDmaVisibilityContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
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

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            _mappings.Remove(mapping.MappingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
            PlatformDmaGrantRequest request)
        {
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(validation.Status, validation.Message!);
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
            _grants.Remove(grant.GrantId);
            _cycles.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            PrepareCalls++;
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant)
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown grant.");
            var cycle = new PlatformProviderDmaVisibilityCycle(_nextCycle++);
            _cycles[grant.GrantId] = cycle;
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(
                new PlatformProviderDmaPrepareEvidence(
                    MalformedPrepare
                        ? new PlatformProviderDmaGrantId(grant.GrantId.Value + 99)
                        : grant.GrantId,
                    grant.Generation,
                    cycle,
                    grant.Direction,
                    PlatformMemoryVisibilityRequirement.PublicationFence,
                    PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
        }

        public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> AcquireDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            AcquireCalls++;
            if (!_cycles.TryGetValue(grant.GrantId, out var cycle))
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No prepared cycle.");
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(
                new PlatformProviderDmaAcquireEvidence(
                    grant.GrantId,
                    grant.Generation,
                    MalformedAcquire
                        ? new PlatformProviderDmaVisibilityCycle(cycle.Value + 1)
                        : cycle,
                    grant.Direction,
                    PlatformMemoryAcquireRequirement.AcquisitionFence,
                    PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied));
        }
    }
}
