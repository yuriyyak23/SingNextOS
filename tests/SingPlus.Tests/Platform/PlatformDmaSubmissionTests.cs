using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaSubmissionTests
{
    [Fact]
    public void ExactPreparedCycleSubmitsBoundedPendingOperation()
    {
        var scenario = CreateScenario(
            1501,
            1510,
            PlatformDmaDirection.DeviceWritesMemory);

        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);

        var submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!);

        Assert.True(submit.IsSuccess, submit.Message);
        Assert.NotEqual(0UL, submit.Value!.OperationId.Value);
        Assert.NotEqual(0UL, submit.Value.Generation.Value);
        Assert.Equal(scenario.Grant.GrantId, submit.Value.GrantId);
        Assert.Equal(scenario.Grant.Generation, submit.Value.GrantGeneration);
        Assert.Equal(prepare.Value.Cycle, submit.Value.PreparedCycle);
        Assert.Equal(scenario.Grant.Range, submit.Value.Range);
        Assert.Equal(scenario.Grant.Direction, submit.Value.Direction);
        Assert.Equal(1, scenario.Provider.SubmitCalls);

        var feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaSubmissionContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        var surface = new[]
        {
            typeof(PlatformDmaOperationId),
            typeof(PlatformDmaOperationGeneration),
            typeof(PlatformDmaSubmission),
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
            "Vector",
            "Controller",
            "Completion",
            "Receipt",
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
        Assert.Contains(nameof(RuntimeKernel.SubmitPlatformDma), methods);
        Assert.DoesNotContain(methods, static name =>
            name.Contains("CompleteDma", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SubmitRequiresCurrentPreparedUnacquiredCycleBeforeProvider()
    {
        var scenario = CreateScenario(
            1502,
            1520,
            PlatformDmaDirection.DeviceWritesMemory);

        var noPrepare = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            default);
        Assert.Equal(KernelError.PlatformDenied, noPrepare.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var first = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(first.IsSuccess, first.Message);
        var second = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(second.IsSuccess, second.Message);
        Assert.NotEqual(first.Value!.Cycle, second.Value!.Cycle);

        var replay = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            first.Value);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var acquire = scenario.Kernel.AcquirePlatformDmaForCpu(
            scenario.Subject,
            scenario.Grant);
        Assert.True(acquire.IsSuccess, acquire.Message);

        var consumed = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            second.Value);
        Assert.Equal(KernelError.PlatformDenied, consumed.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var fresh = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(fresh.IsSuccess, fresh.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            fresh.Value!).IsSuccess);
        Assert.Equal(1, scenario.Provider.SubmitCalls);
    }

    [Fact]
    public void StaleGrantOrForgedCycleFailsBeforeProviderSubmit()
    {
        var scenario = CreateScenario(
            1503,
            1530,
            PlatformDmaDirection.DeviceReadsMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);

        var staleGrant = scenario.Grant with
        {
            Generation = new PlatformDmaGrantGeneration(scenario.Grant.Generation.Value + 1),
        };
        var stale = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            staleGrant,
            prepare.Value!);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var forgedEvidence = prepare.Value! with
        {
            Cycle = new PlatformDmaVisibilityCycle(prepare.Value.Cycle.Value + 999),
        };
        var forged = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            forgedEvidence);
        Assert.Equal(KernelError.PlatformDenied, forged.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
    }

    [Fact]
    public void SubmittedCycleBlocksAcquireReprepareSecondSubmitAndClosure()
    {
        var scenario = CreateScenario(
            1504,
            1540,
            PlatformDmaDirection.DeviceWritesMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        Assert.Equal(
            KernelError.PlatformBindingActive,
            scenario.Kernel.SubmitPlatformDma(
                scenario.Subject,
                scenario.Grant,
                prepare.Value!).Error);
        Assert.Equal(1, scenario.Provider.SubmitCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.PreparePlatformDmaForDevice(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.PrepareCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.AcquirePlatformDmaForCpu(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.AcquireCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.Mapping).Error);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject,
                scenario.Device).Error);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void CapabilityRevokeStopsNewSubmitButKeepsExistingOperationPinned()
    {
        var scenario = CreateScenario(
            1505,
            1550,
            PlatformDmaDirection.DeviceReadsMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        var revokeCapability = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
        Assert.Equal(KernelError.PlatformBindingDraining, revokeCapability.Error);

        var submitAgain = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!);
        Assert.Equal(KernelError.CapabilityRevoked, submitAgain.Error);
        Assert.Equal(1, scenario.Provider.SubmitCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);

        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject,
            scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Active, lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Fact]
    public void ProcessTeardownDrainsWithoutReclaimWhileSubmissionIsPending()
    {
        var scenario = CreateScenario(
            1506,
            1560,
            PlatformDmaDirection.DeviceWritesMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        var terminate = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);

        var process = scenario.Kernel.Processes.Resolve(scenario.Subject);
        Assert.True(process.IsSuccess, process.Message);
        Assert.Equal(ProcessState.Exiting, process.Value!.State);

        var teardown = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, teardown.Value!.Phase);
        Assert.True(teardown.Value.ChannelsClosed);
        Assert.True(teardown.Value.LocalAuthorizationRevoked);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
        Assert.True(teardown.Value.PendingPlatformMappings > 0);

        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject,
            scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Active, lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);

        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void MalformedOrFaultedProviderSubmitPinsAuthorityFailClosed()
    {
        var malformed = CreateScenario(
            1507,
            1570,
            PlatformDmaDirection.DeviceWritesMemory);
        var malformedPrepare = malformed.Kernel.PreparePlatformDmaForDevice(
            malformed.Subject,
            malformed.Grant);
        Assert.True(malformedPrepare.IsSuccess, malformedPrepare.Message);
        malformed.Provider.MalformedSubmit = true;

        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.SubmitPlatformDma(
                malformed.Subject,
                malformed.Grant,
                malformedPrepare.Value!).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.RevokePlatformDma(
                malformed.Subject,
                malformed.Grant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.PreparePlatformDmaForDevice(
                malformed.Subject,
                malformed.Grant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.AcquirePlatformDmaForCpu(
                malformed.Subject,
                malformed.Grant).Error);
        Assert.Equal(0, malformed.Provider.DmaRevokeCalls);

        var faulted = CreateScenario(
            1508,
            1580,
            PlatformDmaDirection.DeviceReadsMemory);
        var faultedPrepare = faulted.Kernel.PreparePlatformDmaForDevice(
            faulted.Subject,
            faulted.Grant);
        Assert.True(faultedPrepare.IsSuccess, faultedPrepare.Message);
        faulted.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        Assert.Equal(
            KernelError.PlatformFaulted,
            faulted.Kernel.SubmitPlatformDma(
                faulted.Subject,
                faulted.Grant,
                faultedPrepare.Value!).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            faulted.Kernel.RevokePlatformDma(
                faulted.Subject,
                faulted.Grant).Error);

        var denied = CreateScenario(
            1509,
            1590,
            PlatformDmaDirection.DeviceReadsMemory);
        var deniedPrepare = denied.Kernel.PreparePlatformDmaForDevice(
            denied.Subject,
            denied.Grant);
        Assert.True(deniedPrepare.IsSuccess, deniedPrepare.Message);
        denied.Provider.SubmitStatus = PlatformAuthorityStatus.Denied;
        Assert.Equal(
            KernelError.PlatformDenied,
            denied.Kernel.SubmitPlatformDma(
                denied.Subject,
                denied.Grant,
                deniedPrepare.Value!).Error);
        denied.Provider.SubmitStatus = null;
        Assert.True(denied.Kernel.SubmitPlatformDma(
            denied.Subject,
            denied.Grant,
            deniedPrepare.Value!).IsSuccess);
        Assert.Equal(2, denied.Provider.SubmitCalls);
    }

    private static Scenario CreateScenario(
        ulong processId,
        ulong domainId,
        PlatformDmaDirection direction)
    {
        var provider = new SubmissionProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var deviceRights = PlatformDeviceRights.Configure;
        var mappingAccess = PlatformMemoryAccess.None;
        switch (direction)
        {
            case PlatformDmaDirection.DeviceReadsMemory:
                deviceRights |= PlatformDeviceRights.Read;
                mappingAccess |= PlatformMemoryAccess.Read;
                break;
            case PlatformDmaDirection.DeviceWritesMemory:
                deviceRights |= PlatformDeviceRights.Write;
                mappingAccess |= PlatformMemoryAccess.Write;
                break;
            case PlatformDmaDirection.Bidirectional:
                deviceRights |= PlatformDeviceRights.Read | PlatformDeviceRights.Write;
                mappingAccess |= PlatformMemoryAccess.Read | PlatformMemoryAccess.Write;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }

        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-submit0",
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

        return new Scenario(
            kernel,
            provider,
            subject,
            deviceCapability,
            device,
            mapping,
            grant);
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
        SubmissionProvider Provider,
        ProcessHandle Subject,
        CapabilityId DeviceCapability,
        PlatformDeviceLease Device,
        PlatformOwnedRegionSliceMapping Mapping,
        PlatformDmaGrant Grant);

    private sealed class SubmissionProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformDmaGrantProvider,
        IPlatformDmaVisibilityProvider,
        IPlatformDmaSubmissionProvider
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaVisibilityCycle> _cycles = [];
        private readonly HashSet<PlatformProviderDmaGrantId> _acquired = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaSubmission> _submissions = [];
        private PlatformProviderDomainLease? _domain;
        private ulong _nextDevice = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextCycle = 1;
        private ulong _nextSubmission = 1;

        public int PrepareCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public int DmaRevokeCalls { get; private set; }
        public int MappingRevokeCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public bool MalformedSubmit { get; set; }
        public PlatformAuthorityStatus? SubmitStatus { get; set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("dma-submit-model"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                1,
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
                PlatformDmaSubmissionContract.ContractVersion,
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
            _devices.Count == 0
                ? PlatformAuthorityResult.Ok()
                : PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Device authority remains live.");

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
            DeviceRevokeCalls++;
            if (_grants.Values.Any(grant => grant.DeviceLease == lease))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }

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
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    mapped.Status,
                    mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (_domain != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain.");
            }

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
            MappingRevokeCalls++;
            if (_grants.Values.Any(grant => grant.MappingLease == mapping))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }

            _mappings.Remove(mapping.MappingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
            PlatformDmaGrantRequest request)
        {
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) ||
                !_mappings.TryGetValue(request.MappingLease.MappingId, out var mapped) ||
                mapped.Slice != request.MappingSlice)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown exact device or mapping authority.");
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
            if (_submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submitted DMA operation remains pending.");
            }

            _grants.Remove(grant.GrantId);
            _cycles.Remove(grant.GrantId);
            _acquired.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            PrepareCalls++;
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant)
            {
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown grant.");
            }

            if (_submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submitted operation remains pending.");
            }

            var cycle = new PlatformProviderDmaVisibilityCycle(_nextCycle++);
            _cycles[grant.GrantId] = cycle;
            _acquired.Remove(grant.GrantId);
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(
                new PlatformProviderDmaPrepareEvidence(
                    grant.GrantId,
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
            if (_submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Completion has not been proven.");
            }

            if (grant.Direction == PlatformDmaDirection.DeviceReadsMemory)
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Read-only DMA does not require acquire.");
            }

            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant ||
                !_cycles.TryGetValue(grant.GrantId, out var cycle) ||
                _acquired.Contains(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No exact unacquired prepared cycle.");
            }

            _acquired.Add(grant.GrantId);
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(
                new PlatformProviderDmaAcquireEvidence(
                    grant.GrantId,
                    grant.Generation,
                    cycle,
                    grant.Direction,
                    PlatformMemoryAcquireRequirement.AcquisitionFence,
                    PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied));
        }

        public PlatformAuthorityResult<PlatformProviderDmaSubmission> SubmitDma(
            PlatformProviderDmaSubmitRequest request)
        {
            SubmitCalls++;
            var requestValidation = PlatformDmaSubmissionContract.ValidateRequest(request);
            if (!requestValidation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    requestValidation.Status,
                    requestValidation.Message!);
            }

            var grant = request.Grant;
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant ||
                !_cycles.TryGetValue(grant.GrantId, out var cycle) ||
                cycle != request.PreparedCycle ||
                _acquired.Contains(grant.GrantId) ||
                _submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submission does not match the exact prepared unacquired grant cycle.");
            }

            if (SubmitStatus is { } status)
            {
                if (status == PlatformAuthorityStatus.Success)
                    throw new InvalidOperationException("Use null to model successful submit.");

                if (status == PlatformAuthorityStatus.Faulted)
                    _submissions[grant.GrantId] = CreateSubmission(request);

                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    status,
                    "Injected DMA submit result.");
            }

            var submission = CreateSubmission(request);
            _submissions.Add(grant.GrantId, submission);
            if (MalformedSubmit)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(
                    submission with
                    {
                        PreparedCycle = new PlatformProviderDmaVisibilityCycle(
                            submission.PreparedCycle.Value + 1),
                    });
            }

            return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(submission);
        }

        private PlatformProviderDmaSubmission CreateSubmission(
            PlatformProviderDmaSubmitRequest request) =>
            new(
                new PlatformProviderDmaSubmissionId(_nextSubmission++),
                new PlatformProviderDmaSubmissionGeneration(1),
                request.Grant.GrantId,
                request.Grant.Generation,
                request.PreparedCycle,
                request.Grant.Range,
                request.Grant.Direction);
    }
}
