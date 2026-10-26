using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaVisibilityTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void LegacyAcquireOutsideOwnersConsumesExactKnownCycleOrPinsUnknown(int fault)
    {
        Scenario scenario = CreateScenario(1401, 1410, PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.BeforeAcquireReturn = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel)!));
            foreach (string? name in new[] { "_secureDomainLifecycleGate", "_dmaCompletionGate" })
                Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!));
            if (fault == 0) scenario.Provider.ThrowIncarnation = true;
            if (fault == 1) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Grant.Mapping.Region,
                new RegionOwner(scenario.Grant.DeviceLease.DomainBinding.Subject.DomainId, scenario.Subject.Generation),
                new(1, new("legacy-acquire", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).IsSuccess);
            if (fault == 2) Assert.True(scenario.Kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Acquire reply lost after effect.");
            if (fault == 4) Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            if (fault == 5)
            {
                Assert.False(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
                Assert.False(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).IsSuccess);
                Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            }
        };
        KernelResult<PlatformDmaAcquireEvidence> result = scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant);
        Assert.Equal(fault == 5, result.IsSuccess);
        scenario.Provider.ThrowIncarnation = false;
        scenario.Provider.BeforeAcquireReturn = null;
        Assert.False(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(1, scenario.Provider.AcquireCalls);
        if (fault is 2 or 3)
        {
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            Assert.Equal(0, scenario.Provider.RevokeCalls);
        }
        else
        {
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.RevokeCalls);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ExactPrepareReceiptSurvivesLateRefusalWithoutBecomingCyclePermission(int fault)
    {
        Scenario scenario = CreateScenario(1401, 1410, PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write, PlatformDmaDirection.DeviceWritesMemory);
        scenario.Provider.MalformedPrepare = fault == 4;
        scenario.Provider.BeforePrepareReturn = () =>
        {
            if (fault is 0 or 4) scenario.Provider.ThrowIncarnation = true;
            if (fault == 1) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Grant.Mapping.Region,
                new RegionOwner(scenario.Grant.DeviceLease.DomainBinding.Subject.DomainId, scenario.Subject.Generation),
                new(1, new("known-prepare", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).IsSuccess);
            if (fault == 2) Assert.True(scenario.Kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Prepare reply lost after effect.");
        };
        Assert.False(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).IsSuccess);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_dmaGrants",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        object record = records[scenario.Grant.GrantId]!;
        object? receipt = record.GetType().GetProperty("KnownPreparationEvidence")!.GetValue(record);
        if (fault < 3) Assert.NotEqual(0UL, Assert.IsType<PlatformProviderDmaPrepareEvidence>(receipt).Cycle.Value);
        else Assert.Null(receipt);
        scenario.Provider.ThrowIncarnation = false;
        scenario.Provider.BeforePrepareReturn = null;
        Assert.False(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(0, scenario.Provider.AcquireCalls);
        if (fault < 2)
        {
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.RevokeCalls);
            Assert.False(scenario.Kernel.PlatformAuthority.HasActiveDmaGrants(scenario.Grant.Mapping.Mapping));
        }
        else
        {
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(0, scenario.Provider.RevokeCalls);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        }
        Assert.Equal(1, scenario.Provider.PrepareCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void PrepareCallbackOutsideOwnersRefusesLateWritersAndOldCycle(int writer)
    {
        Scenario scenario = CreateScenario(1401, 1410, PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.BeforePrepareReturn = () =>
        {
            object kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel)!;
            foreach (string? name in new[] { "_secureDomainLifecycleGate", "_dmaCompletionGate" })
                Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!));
            Assert.False(Monitor.IsEntered(kernelGate));
            if (writer == 0) Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            if (writer == 1) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Grant.Mapping.Region,
                new RegionOwner(scenario.Grant.DeviceLease.DomainBinding.Subject.DomainId, scenario.Subject.Generation),
                new(1, new("dma-prepare", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).IsSuccess);
            if (writer == 2) Assert.True(scenario.Kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (writer == 3) throw new InvalidOperationException("Prepare response lost after cycle effect.");
            if (writer == 4)
            {
                Assert.False(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).IsSuccess);
                Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            }
        };
        KernelResult<PlatformDmaPrepareEvidence> result = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.Equal(writer == 4, result.IsSuccess);
        Assert.Equal(2, scenario.Provider.PrepareCalls);
        scenario.Provider.BeforePrepareReturn = null;
        if (writer != 4) Assert.False(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        if (writer is 0 or 1 or 4)
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        else
        {
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        }
    }

    [Fact]
    public void ExactGrantPrepareAndPostWriteAcquireReturnLocalCycleEvidence()
    {
        Scenario scenario = CreateScenario(
            1401,
            1410,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);

        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
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

        KernelResult<PlatformDmaAcquireEvidence> acquire = scenario.Kernel.AcquirePlatformDmaForCpu(
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
        Scenario writer = CreateScenario(
            1402,
            1420,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaAcquireEvidence> early = writer.Kernel.AcquirePlatformDmaForCpu(writer.Subject, writer.Grant);
        Assert.Equal(KernelError.PlatformDenied, early.Error);
        Assert.Equal(0, writer.Provider.AcquireCalls);

        Scenario reader = CreateScenario(
            1403,
            1430,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(reader.Kernel.PreparePlatformDmaForDevice(reader.Subject, reader.Grant).IsSuccess);
        KernelResult<PlatformDmaAcquireEvidence> unnecessary = reader.Kernel.AcquirePlatformDmaForCpu(reader.Subject, reader.Grant);
        Assert.Equal(KernelError.PlatformDenied, unnecessary.Error);
        Assert.Equal(0, reader.Provider.AcquireCalls);
    }

    [Fact]
    public void AcquiredCycleIsConsumedAndReprepareCreatesFreshLocalAndProviderCycles()
    {
        Scenario scenario = CreateScenario(
            1404,
            1440,
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);

        KernelResult<PlatformDmaPrepareEvidence> first = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(first.IsSuccess);
        Assert.True(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).Error);

        KernelResult<PlatformDmaPrepareEvidence> second = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value!.Cycle, second.Value!.Cycle);
        Assert.True(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(2, scenario.Provider.PrepareCalls);
        Assert.Equal(2, scenario.Provider.AcquireCalls);
    }

    [Fact]
    public void StaleOrForgedGrantFailsBeforeProviderVisibilityCall()
    {
        Scenario scenario = CreateScenario(
            1405,
            1450,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceReadsMemory);

        PlatformDmaGrant stale = scenario.Grant with
        {
            Generation = new PlatformDmaGrantGeneration(scenario.Grant.Generation.Value + 1),
        };
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, stale).Error);
        Assert.Equal(0, scenario.Provider.PrepareCalls);

        PlatformDmaGrant forged = scenario.Grant with
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
        Scenario prepareScenario = CreateScenario(
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

        Scenario acquireScenario = CreateScenario(
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
        Scenario scenario = CreateScenario(
            1408,
            1480,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceReadsMemory);
        PlatformFeatureDescriptor feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaVisibilityContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        Type[] surface = new[]
        {
            typeof(PlatformDmaVisibilityCycle),
            typeof(PlatformDmaPrepareEvidence),
            typeof(PlatformDmaAcquireEvidence),
        };
        string[] forbidden = new[]
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
        foreach (Type? type in surface)
        foreach (MemberInfo member in type.GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
                string signature = member.ToString() ?? member.Name;
            foreach (string? term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }

        string[] methods = typeof(RuntimeKernel)
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
        VisibilityProvider provider = new VisibilityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, processId, domainId);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-visibility0",
            ToCapabilityRights(deviceRights));
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;

        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 512).Value!;
        CapabilityRights memoryRights = CapabilityRights.Map;
        if ((mappingAccess & PlatformMemoryAccess.Read) != 0) memoryRights |= CapabilityRights.Read;
        if ((mappingAccess & PlatformMemoryAccess.Write) != 0) memoryRights |= CapabilityRights.Write;
        CapabilityId memoryCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            memoryRights);
        PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            memoryCapability,
            buffer.Handle,
            64,
            256,
            mappingAccess).Value!;
        PlatformDmaGrant grant = kernel.BindPlatformDma(
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

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        CapabilityRights result = CapabilityRights.None;
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
        IPlatformDmaVisibilityProvider,
        IPlatformProviderIncarnationSource
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
        public int RevokeCalls { get; private set; }
        public bool ThrowIncarnation { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation => ThrowIncarnation
            ? throw new InvalidOperationException("Late incarnation query unavailable.") : new(1);
        public Action? BeforePrepareReturn { get; set; }
        public Action? BeforeAcquireReturn { get; set; }
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
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
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
            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> mapped = MapOwnedRegionSlice(
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
            PlatformProviderRegionMappingLease lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            PlatformProviderOwnedRegionMapping mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
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
            PlatformAuthorityResult validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(validation.Status, validation.Message!);
            PlatformProviderDmaGrant grant = new PlatformProviderDmaGrant(
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
            RevokeCalls++;
            _grants.Remove(grant.GrantId);
            _cycles.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            PrepareCalls++;
            if (!_grants.TryGetValue(grant.GrantId, out PlatformProviderDmaGrant exact) || exact != grant)
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown grant.");
            PlatformProviderDmaVisibilityCycle cycle = new PlatformProviderDmaVisibilityCycle(_nextCycle++);
            _cycles[grant.GrantId] = cycle;
            BeforePrepareReturn?.Invoke();
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
            if (!_cycles.TryGetValue(grant.GrantId, out PlatformProviderDmaVisibilityCycle cycle))
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No prepared cycle.");
            BeforeAcquireReturn?.Invoke();
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
