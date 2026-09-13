using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip.Drivers;

namespace SingPlus.Tests.Drivers;

public sealed class DriverComponentVerticalSliceTests
{
    [Fact]
    public async Task AdmittedDriverOwnsBoundedResourcesAndServesInterruptWithoutDelegatingAuthority()
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1800, 18000);
        var setup = DriverSetup.Create(1810, 18100);
        var admitted = kernel.AdmitComponent(setup.Plan(new DomainId(18000)));

        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.Equal(ComponentLifecycleState.Running, admitted.Value!.State);
        var resources = Assert.IsType<DeviceResourceSetSnapshot>(admitted.Value.DeviceResources);
        Assert.Equal(setup.DeviceId, resources.DeviceResourceId);
        Assert.Equal(PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure, resources.DeviceRights);
        Assert.Single(resources.Mmio);
        Assert.Single(resources.Irqs);
        Assert.NotNull(resources.DmaPolicy);
        Assert.Equal(128, resources.DmaPolicy!.MaximumTransferBytes);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.MmioBindCalls);
        Assert.Equal(1, provider.IrqBindCalls);

        var caller = TestFixtures.Create(kernel, 1820, 18200).Handle;
        var service = Assert.Single(kernel.ResolveByContract(setup.Contract).Value!);
        var session = kernel.OpenSession(caller, service).Value;
        var host = RuntimeDriverInterruptServiceHost.CreateForComponent(kernel, setup.Identity, session, setup.IrqResourceId).Value!;
        var client = IDriverInterruptServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        provider.QueueInterrupt();

        var poll = client.PollAsync().AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var delivery = await poll.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DriverInterruptPollStatus.Delivered, delivery.Status);
        Assert.Equal(KernelEventClass.ExternalSignal, delivery.Event.EventClass);
        Assert.Equal("rx0", delivery.Event.SourceResourceId);
        Assert.DoesNotContain(
            kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(18200)),
            capability => capability.ResourceKind is ResourceKind.Device or ResourceKind.MmioRegion or ResourceKind.Irq or ResourceKind.Dma);

        var buffer = kernel.AllocateBuffer<byte>(admitted.Value.Process, 256).Value!;
        var memory = kernel.MintCapability(
            admitted.Value.Domain,
            admitted.Value.Process,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = kernel.MapComponentDriverOwnedRegion(
            setup.Identity,
            memory,
            buffer.Handle,
            0,
            256,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var dma = kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            128,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(dma.IsSuccess, dma.Message);
        Assert.Equal(1, provider.DmaBindCalls);
        Assert.Equal(1, kernel.QueryComponentDeviceResources(setup.Identity).Value!.ActiveDmaGrantCount);

        var oversized = kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            129,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(KernelError.PlatformDenied, oversized.Error);
        var wrongDirection = kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            64,
            PlatformDmaDirection.DeviceWritesMemory);
        Assert.Equal(KernelError.PlatformDenied, wrongDirection.Error);
        Assert.Equal(1, provider.DmaBindCalls);

        var crashed = kernel.FaultComponent(setup.Identity);
        Assert.True(crashed.IsSuccess, crashed.Message);
        Assert.Equal(ComponentLifecycleState.Reclaimable, crashed.Value!.State);
        Assert.Null(crashed.Value.DeviceResources);
        Assert.False(crashed.Value.PlatformAuthorityLive);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(admitted.Value.Process).Error);
        AssertOrdered(provider.Log, "revoke-dma", "revoke-irq", "revoke-mmio", "revoke-device", "revoke-mapping", "revoke-domain");
    }

    [Fact]
    public void DriverHardwareCapabilityOutsideResourceSetFailsBeforeProcessOrProviderAuthorityCreation()
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1830, 18300);
        var setup = DriverSetup.Create(1840, 18400, includeAmbientDevice: true);

        var result = kernel.AdmitComponent(setup.Plan(new DomainId(18300)));

        Assert.Equal(KernelError.InvalidManifest, result.Error);
        Assert.Equal(0, provider.DeviceBindCalls);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(1840), 1)).Error);
    }

    [Fact]
    public void CrashWithAmbiguousDmaRevokePinsDriverResourcesAndForbidsReclaim()
    {
        var provider = new DriverProvider { DmaRevokeStatus = PlatformAuthorityStatus.Faulted };
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1850, 18500);
        var setup = DriverSetup.Create(1860, 18600);
        var admitted = kernel.AdmitComponent(setup.Plan(new DomainId(18500))).Value!;
        var buffer = kernel.AllocateBuffer<byte>(admitted.Process, 256).Value!;
        var memory = kernel.MintCapability(
            admitted.Domain,
            admitted.Process,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = kernel.MapComponentDriverOwnedRegion(
            setup.Identity,
            memory,
            buffer.Handle,
            0,
            256,
            PlatformMemoryAccess.Read).Value!;
        Assert.True(kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);

        var fault = kernel.FaultComponent(setup.Identity);
        var snapshot = kernel.QueryComponent(setup.Identity).Value!;

        Assert.Equal(KernelError.PlatformFaulted, fault.Error);
        Assert.Equal(ComponentLifecycleState.Faulted, snapshot.State);
        Assert.False(snapshot.Reclaimable);
        Assert.True(snapshot.PlatformAuthorityLive);
        Assert.Equal(1, snapshot.DeviceResources!.ActiveDmaGrantCount);
        Assert.True(kernel.Processes.Resolve(admitted.Process).IsSuccess);
        Assert.False(kernel.ReleaseRegion(admitted.Process, buffer).IsSuccess);
        Assert.DoesNotContain("revoke-device", provider.Log);
        Assert.DoesNotContain("revoke-mapping", provider.Log);
        Assert.DoesNotContain("revoke-domain", provider.Log);
    }

    [Fact]
    public void PublicDriverSessionContractCannotExposePlatformOrProviderAuthority()
    {
        var publicTypes = typeof(IDriverInterruptService).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "SingPlus.Sip.Drivers")
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods().Select(method => method.ReturnType)))
            .ToArray();
        Assert.DoesNotContain(publicTypes, type => type.Assembly.GetName().Name?.StartsWith("SingPlus.Platform", StringComparison.Ordinal) == true);
        foreach (var forbidden in new[] { "ProviderLease", "PhysicalAddress", "Iommu", "Vmcs", "Lane", "Opcode", "DeviceResourceSet", "PlatformDeviceLease", "PlatformDmaGrant" })
            Assert.DoesNotContain(typeof(IDriverInterruptService).Assembly.GetExportedTypes().Where(type => type.Namespace == "SingPlus.Sip.Drivers"), type => type.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertOrdered(IReadOnlyList<string> log, params string[] expected)
    {
        var position = -1;
        foreach (var item in expected)
        {
            var next = -1;
            for (var index = position + 1; index < log.Count; index++)
            {
                if (string.Equals(log[index], item, StringComparison.Ordinal))
                {
                    next = index;
                    break;
                }
            }
            Assert.True(next > position, $"Expected '{item}' after position {position}. Log: {string.Join(", ", log)}");
            position = next;
        }
    }

    private sealed record DriverSetup(
        ComponentIdentity Identity,
        string DeviceId,
        string MmioResourceId,
        string IrqResourceId,
        ServiceContractIdentity Contract,
        SingProcessManifestV1 Process,
        byte[] Image,
        CapabilityRequirementV1[] Requirements,
        ComponentDriverResourcePlan Resources)
    {
        public static DriverSetup Create(ulong processId, ulong domainId, bool includeAmbientDevice = false)
        {
            const string device = "device/mock-net0";
            var mmio = CapabilityResourceIds.MmioRegion(device, "bar0", 4096);
            var irq = CapabilityResourceIds.Irq(device, "rx0", IrqTriggerMode.Edge);
            var requirements = new List<CapabilityRequirementV1>
            {
                new(ResourceKind.Device, device, CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure),
                new(ResourceKind.MmioRegion, mmio, CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write),
                new(ResourceKind.Irq, irq, CapabilityRights.Signal),
            };
            if (includeAmbientDevice)
                requirements.Add(new CapabilityRequirementV1(ResourceKind.Device, "device/ambient", CapabilityRights.Read));

            var process = new SingProcessManifestV1(
                new ProcessId(processId),
                new DomainId(domainId),
                1,
                $"driver-entry-{processId}",
                ExecutionRole.Driver,
                MemoryProfile.SipRegion,
                requirements);
            var protocol = IDriverInterruptServiceProtocol.CreateDefinition();
            var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
            var resources = new ComponentDriverResourcePlan(
                device,
                PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
                [new DriverMmioResourcePlan(mmio, 256, 512, PlatformMmioAccess.Read | PlatformMmioAccess.Write)],
                [new DriverIrqResourcePlan(irq)],
                new DriverDmaPolicy(128, [PlatformDmaDirection.DeviceReadsMemory]));
            return new DriverSetup(
                new ComponentIdentity($"driver-{processId}"),
                device,
                mmio,
                irq,
                contract,
                process,
                [0x44, 0x52, 0x56, (byte)(processId & 0xff)],
                requirements.ToArray(),
                resources);
        }

        public ComponentAdmissionPlan Plan(DomainId issuer)
        {
            var provided = new ProvidedServiceManifestV1("driver-interrupt", Contract);
            var manifest = new ServiceManifestV1(
                Identity,
                new ComponentVersion("1"),
                Digest(Image),
                Process,
                [provided],
                requiresPlatformDomain: true,
                platformRequirements:
                [
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.MmioMapping, PlatformMmioLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.IrqBinding, PlatformIrqBindingContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.DmaMapping, PlatformDmaGrantContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                ]);
            return new ComponentAdmissionPlan(
                manifest,
                Image,
                Requirements.Select(requirement => new ComponentCapabilityGrant(issuer, requirement)),
                [new ComponentProvidedServiceRegistration(provided, IDriverInterruptServiceProtocol.CreateDefinition(), IDriverInterruptServiceResponseProtocol.Definition)],
                Resources);
        }
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class DriverProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformMmioLeaseProvider,
        IPlatformIrqBindingProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionRevocationProvider,
        IPlatformDmaGrantProvider
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderMmioLeaseId, PlatformProviderMmioLease> _mmio = [];
        private readonly Dictionary<PlatformProviderIrqBindingId, PlatformProviderIrqBinding> _irqs = [];
        private readonly HashSet<PlatformProviderIrqBindingId> _pendingIrqs = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformOperationId, PlatformCompletionReceipt> _operations = [];
        private PlatformProviderDomainLease? _domain;
        private ulong _nextDevice = 1;
        private ulong _nextMmio = 1;
        private ulong _nextIrq = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextOperation = 1;
        private ulong _nextIrqSequence = 1;

        public int DeviceBindCalls { get; private set; }
        public int MmioBindCalls { get; private set; }
        public int IrqBindCalls { get; private set; }
        public int DmaBindCalls { get; private set; }
        public PlatformAuthorityStatus? DmaRevokeStatus { get; set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("driver-vertical-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.MmioMapping, PlatformMmioLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IrqBinding, PlatformIrqBindingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.DmaMapping, PlatformDmaGrantContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
        });

        public void QueueInterrupt()
        {
            foreach (var id in _irqs.Keys) _pendingIrqs.Add(id);
        }

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(new PlatformProviderDomainLeaseId(1), new PlatformProviderLeaseGeneration(1), subject);
            _domain = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_devices.Count != 0 || _mappings.Count != 0)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Child driver resources remain live.");
            Log.Add("revoke-domain");
            _domain = null;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease, PlatformDeviceIdentity device, PlatformDeviceRights rights)
        {
            DeviceBindCalls++;
            if (_domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Wrong domain.");
            var lease = new PlatformProviderDeviceLease(new PlatformProviderDeviceLeaseId(_nextDevice++), new PlatformProviderLeaseGeneration(1), domainLease, device, rights);
            _devices.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            if (_mmio.Values.Any(item => item.DeviceLease == lease) || _irqs.Values.Any(item => item.DeviceLease == lease) || _grants.Values.Any(item => item.DeviceLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Child device resources remain live.");
            Log.Add("revoke-device");
            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(PlatformProviderDeviceLease deviceLease, PlatformMmioRegionIdentity region, PlatformMmioRange range, PlatformMmioAccess access)
        {
            MmioBindCalls++;
            var validation = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(validation.Status, validation.Message!);
            if (!_devices.ContainsKey(deviceLease.LeaseId)) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Revoked, "Device is not live.");
            var lease = new PlatformProviderMmioLease(new PlatformProviderMmioLeaseId(_nextMmio++), new PlatformProviderLeaseGeneration(1), deviceLease, region, range, access);
            _mmio.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease)
        {
            Log.Add("revoke-mmio");
            _mmio.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(PlatformProviderDeviceLease deviceLease, PlatformInterruptSourceIdentity source)
        {
            IrqBindCalls++;
            if (!_devices.ContainsKey(deviceLease.LeaseId)) return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Revoked, "Device is not live.");
            var binding = new PlatformProviderIrqBinding(new PlatformProviderIrqBindingId(_nextIrq++), new PlatformProviderLeaseGeneration(1), deviceLease, source);
            _irqs.Add(binding.BindingId, binding);
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Ok(binding);
        }

        public PlatformAuthorityResult<PlatformInterruptDeliveryObservation> PollInterrupt(PlatformProviderIrqBinding binding)
        {
            if (!_irqs.ContainsKey(binding.BindingId)) return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Revoked, "IRQ route is not live.");
            if (!_pendingIrqs.Contains(binding.BindingId)) return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(new PlatformInterruptDeliveryObservation(binding, false, default));
            return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(new PlatformInterruptDeliveryObservation(binding, true, new PlatformProviderInterruptDeliverySequence(_nextIrqSequence++)));
        }

        public PlatformAuthorityResult CompleteInterruptDelivery(PlatformProviderIrqBinding binding, PlatformProviderInterruptDeliverySequence sequence)
        {
            if (sequence.Value == 0 || !_pendingIrqs.Remove(binding.BindingId)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "IRQ delivery is stale.");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult RevokeInterrupt(PlatformProviderIrqBinding binding)
        {
            Log.Add("revoke-irq");
            _pendingIrqs.Remove(binding.BindingId);
            _irqs.Remove(binding.BindingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            var mapped = MapOwnedRegionSlice(domainLease, new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return mapped.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(mapped.Status, mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(PlatformProviderDomainLease domainLease, PlatformRegionSlice slice)
        {
            if (_domain != domainLease) return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.WrongDomain, "Wrong domain.");
            var validation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(validation.Status, validation.Message!);
            var lease = new PlatformProviderRegionMappingLease(new PlatformProviderRegionMappingId(_nextMapping++), new PlatformProviderLeaseGeneration(1), domainLease, slice.Region, slice.Access);
            var mapping = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, mapping);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapping);
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(PlatformDmaGrantRequest request)
        {
            DmaBindCalls++;
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(validation.Status, validation.Message!);
            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) || !_mappings.TryGetValue(request.MappingLease.MappingId, out var mapping) || mapping.Slice != request.MappingSlice)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Denied, "Exact device or mapping is not live.");
            var grant = new PlatformProviderDmaGrant(new PlatformProviderDmaGrantId(_nextGrant++), new PlatformProviderLeaseGeneration(1), request.DeviceLease, request.MappingLease, request.Range, request.Direction);
            _grants.Add(grant.GrantId, grant);
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
        }

        public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
        {
            if (DmaRevokeStatus is { } status) return PlatformAuthorityResult.Fail(status, "Injected ambiguous DMA revoke.");
            Log.Add("revoke-dma");
            _grants.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            if (_grants.Values.Any(grant => grant.MappingLease == mapping)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "DMA grant remains live.");
            Log.Add("revoke-mapping");
            _mappings.Remove(mapping.MappingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            if (_grants.Values.Any(grant => grant.MappingLease == mapping)) return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Denied, "DMA grant remains live.");
            Log.Add("revoke-mapping");
            _mappings.Remove(mapping.MappingId);
            var operation = new PlatformOperationIdentity(new PlatformOperationId(_nextOperation++), new PlatformOperationGeneration(1), mapping.DomainLease);
            var receipt = new PlatformCompletionReceipt(operation.OperationId, operation.Generation, operation.DomainLease, PlatformCompletionState.Closed);
            _operations.Add(operation.OperationId, receipt);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new PlatformRegionRevocationTicket(mapping.MappingId, mapping.Generation, operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) =>
            _operations.TryGetValue(operation.OperationId, out var receipt)
                ? PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(receipt)
                : PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(PlatformAuthorityStatus.Denied, "Unknown operation.");
    }
}
