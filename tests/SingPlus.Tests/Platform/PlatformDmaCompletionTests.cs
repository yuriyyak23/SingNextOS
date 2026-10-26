using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionPublicationRouteLossClearsOriginalLocalReservationWithoutRepeatingProvider(bool foreignRoute)
    {
        Scenario scenario = CreateScenario(1691, 1791, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        KernelEventEndpoint endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(RuntimeKernel).GetField("_kernelEvents", flags)!.GetValue(scenario.Kernel)!;
        IDictionary routes = (global::System.Collections.IDictionary)registry.GetType().GetField("_endpoints", flags)!.GetValue(registry)!;
        object original = routes[endpoint.EndpointId]!;
        ProcessHandle foreign = TestFixtures.Create(scenario.Kernel, 1692, 1792).Handle;
        KernelEventEndpoint foreignEndpoint = scenario.Kernel.CreateKernelEventEndpoint(foreign).Value!;
        object foreignRecord = routes[foreignEndpoint.EndpointId]!;
        scenario.Provider.BeforeCompletionReply = () =>
        {
            if (foreignRoute) routes[endpoint.EndpointId] = foreignRecord;
            else routes.Remove(endpoint.EndpointId);
        };
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        KernelResult<PlatformDmaCompletionEvidence> published = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission, endpoint);
        Assert.False(published.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, published.Error);
        Assert.Null(original.GetType().GetProperty("Staged")!.GetValue(original));
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        scenario.Provider.BeforeCompletionReply = null;
        Assert.True(scenario.Kernel.GetOrObservePlatformDmaCompletion(scenario.Subject, submission).IsSuccess);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        KernelResult revoke = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
        Assert.False(revoke.IsSuccess); // Completion is not post-completion visibility or closure.
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.False(lifecycle.Value!.LocalReservationReleased);
        routes[endpoint.EndpointId] = original; // Same authoritative record survived; no copy/ABA resurrection.
        Assert.True(scenario.Kernel.CloseKernelEventEndpoint(scenario.Subject, endpoint).IsSuccess);
        Assert.Equal("Closed", original.GetType().GetProperty("State")!.GetValue(original)!.ToString());
        Assert.Equal("Active", foreignRecord.GetType().GetProperty("State")!.GetValue(foreignRecord)!.ToString());
        Assert.Equal(KernelError.ResponseNotAvailable, scenario.Kernel.ConsumeKernelEvent(foreign, foreignEndpoint).Error);
    }
    [Fact]
    public void ExactPendingThenCompletedProofBindsOperationGrantAndPreparedCycle()
    {
        Scenario scenario = CreateScenario(1601, 1610, PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        KernelResult<PlatformDmaSubmission> submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!);
        Assert.True(submit.IsSuccess, submit.Message);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Pending;
        KernelResult<PlatformDmaCompletionEvidence> pending = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submit.Value!);
        Assert.False(pending.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, pending.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        KernelResult<PlatformDmaCompletionEvidence> completed = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submit.Value!);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.True(completed.Value!.IsSatisfied);
        Assert.Equal(submit.Value.OperationId, completed.Value.OperationId);
        Assert.Equal(submit.Value.Generation, completed.Value.OperationGeneration);
        Assert.Equal(submit.Value.GrantId, completed.Value.GrantId);
        Assert.Equal(submit.Value.GrantGeneration, completed.Value.GrantGeneration);
        Assert.Equal(submit.Value.PreparedCycle, completed.Value.PreparedCycle);
        Assert.Equal(submit.Value.Range, completed.Value.Range);
        Assert.Equal(submit.Value.Direction, completed.Value.Direction);
        Assert.Equal(2, scenario.Provider.CompletionCalls);

        KernelResult<PlatformDmaCompletionEvidence> replay = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submit.Value!);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(2, scenario.Provider.CompletionCalls);

        KernelResult<PlatformDmaAcquireEvidence> acquire = scenario.Kernel.AcquirePlatformDmaForCpu(
            scenario.Subject,
            scenario.Grant);
        Assert.False(acquire.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, acquire.Error);
        Assert.Equal(0, scenario.Provider.AcquireCalls);

        KernelResult revoke = scenario.Kernel.RevokePlatformDma(
            scenario.Subject,
            scenario.Grant);
        Assert.False(revoke.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, revoke.Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);

        PlatformFeatureDescriptor feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaCompletionContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

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
            "Receipt",
            "Vmcs",
            "Vmx",
            "Lane",
            "Opcode",
        };
        foreach (MemberInfo member in typeof(PlatformDmaCompletionEvidence).GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            string signature = member.ToString() ?? member.Name;
            foreach (string? term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProviderResetDuringCompletionObservationCannotBecomeReusableProof()
    {
        Scenario scenario = CreateScenario(1603, 1613, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        scenario.Provider.ResetDuringNextCompletion = true;

        KernelResult<PlatformDmaCompletionEvidence> stale = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);

        Assert.Equal(KernelError.PlatformFaulted, stale.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
    }

    [Fact]
    public void StaleForgedWrongCycleAndReplayRequestsFailBeforeProvider()
    {
        Scenario scenario = CreateScenario(1602, 1620, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);

        PlatformDmaSubmission staleOperation = submission with
        {
            Generation = new PlatformDmaOperationGeneration(submission.Generation.Value + 1),
        };
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, staleOperation).Error);

        PlatformDmaSubmission staleGrant = submission with
        {
            GrantGeneration = new PlatformDmaGrantGeneration(submission.GrantGeneration.Value + 1),
        };
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, staleGrant).Error);

        PlatformDmaSubmission wrongOperation = submission with
        {
            OperationId = new PlatformDmaOperationId(submission.OperationId.Value + 99),
        };
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, wrongOperation).Error);

        PlatformDmaSubmission wrongCycle = submission with
        {
            PreparedCycle = new PlatformDmaVisibilityCycle(submission.PreparedCycle.Value + 99),
        };
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, wrongCycle).Error);

        PlatformDmaSubmission wrongRange = submission with
        {
            Range = new PlatformDmaRange(submission.Range.Offset, submission.Range.Length + 1),
        };
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, wrongRange).Error);

        Assert.Equal(0, scenario.Provider.CompletionCalls);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        KernelResult<PlatformDmaCompletionEvidence> valid = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.Equal(1, scenario.Provider.CompletionCalls);

        KernelResult<PlatformDmaCompletionEvidence> replay = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
    }

    [Theory]
    [InlineData(CompletionMutation.WrongSubmissionId)]
    [InlineData(CompletionMutation.WrongSubmissionGeneration)]
    [InlineData(CompletionMutation.WrongGrantId)]
    [InlineData(CompletionMutation.WrongGrantGeneration)]
    [InlineData(CompletionMutation.WrongCycle)]
    [InlineData(CompletionMutation.WrongRange)]
    [InlineData(CompletionMutation.WrongDirection)]
    public void MalformedProviderCompletionEvidenceFaultPinsAuthority(CompletionMutation mutation)
    {
        Scenario scenario = CreateScenario(
            1610 + (ulong)mutation,
            1710 + (ulong)mutation,
            PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        KernelEventEndpoint endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        scenario.Provider.CompletionMutation = mutation;

        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.False(completion.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, completion.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);

        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Faulted)]
    [InlineData(PlatformAuthorityStatus.Stale)]
    [InlineData(PlatformAuthorityStatus.Revoked)]
    [InlineData(PlatformAuthorityStatus.WrongDomain)]
    public void InvalidProviderCompletionLifetimeFailsClosed(PlatformAuthorityStatus status)
    {
        Scenario scenario = CreateScenario(
            1630 + (ulong)status,
            1730 + (ulong)status,
            PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        KernelEventEndpoint endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        scenario.Provider.CompletionFailure = status;

        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.False(completion.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, completion.Error);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
    }

    [Fact]
    public void CompletionRemainsObservableAfterCapabilityRevokeAndDuringProcessExit()
    {
        Scenario scenario = CreateScenario(1650, 1750, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);

        KernelResult capabilityRevoke = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
        Assert.False(capabilityRevoke.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, capabilityRevoke.Error);

        KernelResult terminate = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);

        KernelResult<SingProcess> process = scenario.Kernel.Processes.Resolve(scenario.Subject);
        Assert.True(process.IsSuccess, process.Message);
        Assert.Equal(ProcessState.Exiting, process.Value!.State);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission);
        Assert.True(completion.IsSuccess, completion.Message);

        KernelResult<ProcessTeardownSnapshot> teardown = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, teardown.Value!.Phase);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
        Assert.True(teardown.Value.PendingPlatformMappings > 0);

        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject,
            scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Active, lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    private static PlatformDmaSubmission PrepareAndSubmit(Scenario scenario)
    {
        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        KernelResult<PlatformDmaSubmission> submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!);
        Assert.True(submit.IsSuccess, submit.Message);
        return submit.Value!;
    }

    private static Scenario CreateScenario(
        ulong processId,
        ulong domainId,
        PlatformDmaDirection direction)
    {
        CompletionProvider provider = new CompletionProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, processId, domainId);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        PlatformDeviceRights deviceRights = PlatformDeviceRights.Configure;
        PlatformMemoryAccess mappingAccess = PlatformMemoryAccess.None;
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

        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-completion0",
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

    public enum CompletionMutation
    {
        None = 0,
        WrongSubmissionId,
        WrongSubmissionGeneration,
        WrongGrantId,
        WrongGrantGeneration,
        WrongCycle,
        WrongRange,
        WrongDirection,
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        CompletionProvider Provider,
        ProcessHandle Subject,
        CapabilityId DeviceCapability,
        PlatformDeviceLease Device,
        PlatformOwnedRegionSliceMapping Mapping,
        PlatformDmaGrant Grant);

    private sealed class CompletionProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformDmaGrantProvider,
        IPlatformDmaVisibilityProvider,
        IPlatformDmaSubmissionProvider,
        IPlatformDmaCompletionProvider,
        IPlatformProviderIncarnationSource
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

        public int AcquireCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public Action? BeforeCompletionReply { get; set; }
        public int DmaRevokeCalls { get; private set; }
        public int MappingRevokeCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public PlatformProviderDmaCompletionState CompletionState { get; set; } =
            PlatformProviderDmaCompletionState.Pending;
        public PlatformAuthorityStatus? CompletionFailure { get; set; }
        public CompletionMutation CompletionMutation { get; set; }
        public bool ResetDuringNextCompletion { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation { get; private set; } = new(1);

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("dma-completion-model"),
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
                PlatformDmaCompletionContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
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
            PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> mapped = MapOwnedRegionSlice(
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
            PlatformAuthorityResult validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            }

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
            DmaRevokeCalls++;
            if (_submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA post-submit lifetime remains pinned.");
            }

            _grants.Remove(grant.GrantId);
            _cycles.Remove(grant.GrantId);
            _acquired.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            if (!_grants.TryGetValue(grant.GrantId, out PlatformProviderDmaGrant exact) || exact != grant ||
                _submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Grant is not eligible for prepare.");
            }

            PlatformProviderDmaVisibilityCycle cycle = new PlatformProviderDmaVisibilityCycle(_nextCycle++);
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
            if (_submissions.ContainsKey(grant.GrantId) ||
                !_cycles.TryGetValue(grant.GrantId, out PlatformProviderDmaVisibilityCycle cycle) ||
                _acquired.Contains(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No acquirable exact DMA cycle.");
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
            PlatformAuthorityResult validation = PlatformDmaSubmissionContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            if (!_grants.TryGetValue(request.Grant.GrantId, out PlatformProviderDmaGrant exact) ||
                exact != request.Grant ||
                !_cycles.TryGetValue(request.Grant.GrantId, out PlatformProviderDmaVisibilityCycle cycle) ||
                cycle != request.PreparedCycle ||
                _acquired.Contains(request.Grant.GrantId) ||
                _submissions.ContainsKey(request.Grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submission does not match the exact prepared grant cycle.");
            }

            PlatformProviderDmaSubmission submission = new PlatformProviderDmaSubmission(
                new PlatformProviderDmaSubmissionId(_nextSubmission++),
                new PlatformProviderDmaSubmissionGeneration(1),
                request.Grant.GrantId,
                request.Grant.Generation,
                request.PreparedCycle,
                request.Grant.Range,
                request.Grant.Direction);
            _submissions.Add(request.Grant.GrantId, submission);
            return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(submission);
        }

        public PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> ObserveDmaCompletion(
            PlatformProviderDmaSubmission submission)
        {
            CompletionCalls++;
            BeforeCompletionReply?.Invoke();
            if (ResetDuringNextCompletion)
            {
                ResetDuringNextCompletion = false;
                CurrentIncarnation = new(CurrentIncarnation.Value + 1);
            }
            if (!_submissions.TryGetValue(submission.GrantId, out PlatformProviderDmaSubmission exact) || exact != submission)
            {
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Unknown exact provider DMA submission.");
            }

            if (CompletionFailure is { } failure)
            {
                if (failure == PlatformAuthorityStatus.Success)
                    throw new InvalidOperationException("Use CompletionState for successful observations.");
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    failure,
                    "Injected completion observation failure.");
            }

            PlatformProviderDmaCompletionEvidence evidence = new PlatformProviderDmaCompletionEvidence(
                submission.SubmissionId,
                submission.Generation,
                submission.GrantId,
                submission.GrantGeneration,
                submission.PreparedCycle,
                submission.Range,
                submission.Direction,
                CompletionState);

            evidence = CompletionMutation switch
            {
                CompletionMutation.WrongSubmissionId => evidence with
                {
                    SubmissionId = new PlatformProviderDmaSubmissionId(evidence.SubmissionId.Value + 1),
                },
                CompletionMutation.WrongSubmissionGeneration => evidence with
                {
                    SubmissionGeneration = new PlatformProviderDmaSubmissionGeneration(
                        evidence.SubmissionGeneration.Value + 1),
                },
                CompletionMutation.WrongGrantId => evidence with
                {
                    GrantId = new PlatformProviderDmaGrantId(evidence.GrantId.Value + 1),
                },
                CompletionMutation.WrongGrantGeneration => evidence with
                {
                    GrantGeneration = new PlatformProviderLeaseGeneration(
                        evidence.GrantGeneration.Value + 1),
                },
                CompletionMutation.WrongCycle => evidence with
                {
                    PreparedCycle = new PlatformProviderDmaVisibilityCycle(
                        evidence.PreparedCycle.Value + 1),
                },
                CompletionMutation.WrongRange => evidence with
                {
                    Range = new PlatformDmaRange(evidence.Range.Offset, evidence.Range.Length + 1),
                },
                CompletionMutation.WrongDirection => evidence with
                {
                    Direction = evidence.Direction == PlatformDmaDirection.DeviceReadsMemory
                        ? PlatformDmaDirection.DeviceWritesMemory
                        : PlatformDmaDirection.DeviceReadsMemory,
                },
                _ => evidence,
            };

            return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Ok(evidence);
        }
    }
}
