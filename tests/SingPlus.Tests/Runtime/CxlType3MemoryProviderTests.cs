using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlType3MemoryProviderTests
{
    [Fact]
    public void PlacementPolicyExplicitlyFallsBackOrFailsWhenCapacityIsMissing()
    {
        var model = new CxlType3ModelProvider();
        var planner = new CxlType3PlacementPlanner(model, model);
        var preferred = Intent(CxlMemoryPlacementPreference.CxlPreferred);
        var required = Intent(CxlMemoryPlacementPreference.CxlRequired);

        var fallback = planner.Plan(preferred, [new("missing")]);
        var failure = planner.Plan(required, [new("missing")]);

        Assert.True(fallback.IsSuccess);
        Assert.Equal(CxlMemoryPlacementKind.Local, fallback.Value!.Kind);
        Assert.False(failure.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, failure.Error);
    }

    [Fact]
    public void ModelPlacesAnOrdinaryOwnedBufferAndCloseRestoresNormalReclaim()
    {
        var s = CreateScenario();
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.DoesNotContain(typeof(CxlMemoryPlacementSnapshot).Assembly.GetExportedTypes(), type => type.Name == "CxlRegion");

        var blocked = s.Kernel.ReleaseRegion(s.Handle, s.Buffer);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, blocked.Error);
        Assert.True(s.Memory.Close(placed.Value!.PlacementId).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Handle, s.Buffer).IsSuccess);
    }

    [Fact]
    public void HotRemoveMarksControlledMigrationWithoutDanglingOrRevokingOwnership()
    {
        var s = CreateScenario();
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.HotRemove(s.Endpoint.EndpointId).IsSuccess);

        var refresh = s.Memory.Refresh(placed.PlacementId);
        Assert.False(refresh.IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired, s.Memory.Query(placed.PlacementId).Value!.State);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.True(s.Memory.Close(placed.PlacementId).IsSuccess);
    }

    [Fact]
    public void HotRemoveInvalidatesOnlyAffectedEndpointBindings()
    {
        var s = CreateScenario(twoEndpoints: true);
        var first = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var secondBuffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 32).Value!;
        var endpoint2 = s.Model.QueryEndpoint(new("type3-1")).Value!;
        var second = s.Memory.Place(s.Owner, secondBuffer.Handle, s.Subject, s.Lease2!.Value, endpoint2, CxlMemoryPersistence.Volatile).Value!;

        Assert.True(s.Model.HotRemove(s.Endpoint.EndpointId).IsSuccess);
        Assert.False(s.Memory.Refresh(first.PlacementId).IsSuccess);
        Assert.True(s.Memory.Refresh(second.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Active, s.Memory.Query(second.PlacementId).Value!.State);
    }

    [Fact]
    public void CapacityGenerationAndCoherenceClaimsAreExplicit()
    {
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("e"), new("device:e"), 128).IsSuccess);
        var before = model.QueryEndpoint(new("e")).Value!;
        Assert.Equal(CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, before.Features);
        Assert.DoesNotContain(typeof(ICxlCoherentAccessProvider), model.GetType().GetInterfaces());
        Assert.True(model.Rebind(new("e")).IsSuccess);
        var after = model.QueryEndpoint(new("e")).Value!;
        Assert.NotEqual(before.DeviceGeneration, after.DeviceGeneration);
    }

    [Fact]
    public void CxlBackedRegionStillUsesExistingPlatformMappingForDeviceAccess()
    {
        var s = CreateScenario();
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);

        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability,
            s.Buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.DoesNotContain(typeof(IPlatformDmaGrantProvider), s.Model.GetType().GetInterfaces());
    }

    [Fact]
    public void StaleDestructiveHandlesCannotDeleteCurrentFabricOrMemoryGeneration()
    {
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("exact"), new("device:exact"), 1024).IsSuccess);
        var endpoint = model.QueryEndpoint(new("exact")).Value!;
        var first = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var second = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var firstBacking = new RegionBackingLeaseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), 64);
        var secondBacking = new RegionBackingLeaseDescriptor(new(new(2), 1), new(new(2), new(1)), new(new(1), 1), 64);
        var firstMemory = model.BindMemory(first, firstBacking).Value!;
        var secondMemory = model.BindMemory(second, secondBacking).Value!;
        var ticket = model.BeginReconfiguration(first).Value!;
        var replacement = model.CompleteReconfiguration(ticket).Value!;

        Assert.Equal(PlatformAuthorityStatus.Stale, model.Unbind(first).Status);
        Assert.Equal(replacement, model.Query(first.BindingId).Value);
        Assert.Equal(new CxlFabricBindingGeneration(1), model.Query(second.BindingId).Value!.Generation);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            model.ReleaseMemory(firstMemory).Status);
        Assert.Equal(new CxlMemoryBindingGeneration(2), model.QueryMemory(firstMemory.BindingId).Value!.Generation);
        Assert.Equal(new CxlMemoryBindingGeneration(1), model.QueryMemory(secondMemory.BindingId).Value!.Generation);
        Assert.True(model.ReleaseMemory(model.QueryMemory(firstMemory.BindingId).Value!).IsSuccess);
        Assert.True(model.ReleaseMemory(secondMemory).IsSuccess);
    }

    [Fact]
    public void EffectCreatingModelRejectionsUseNotAcceptedOnlyBeforeEffect()
    {
        var model = new CxlType3ModelProvider();
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            model.Bind(new(new("missing"), new(1), 64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Status);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            model.AssignPoolCapacity(new("missing"), new("missing"), 64).Status);

        Assert.True(model.RegisterEndpoint(new("pre-effect"), new("device:pre-effect"), 128).IsSuccess);
        var endpoint = model.QueryEndpoint(new("pre-effect")).Value!;
        var fabric = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var staleFabric = fabric with { Generation = new(fabric.Generation.Value + 1) };
        var backing = new RegionBackingLeaseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), 64);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, model.BindMemory(staleFabric, backing).Status);
        Assert.Equal(fabric, model.Query(fabric.BindingId).Value);
    }

    [Fact]
    public void ProcessTeardownClosesLiveType3PlacementBeforeRegionReclaim()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;

        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);

        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.InvalidRegionState, s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).Error);
    }

    [Fact]
    public void Type3BackedInputAndOutputComposeWithPlannerAndType2StagedExecution()
    {
        var s = CreateScenario();
        var output = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        var inputPlacement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var outputPlacement = s.Memory.Place(s.Owner, output.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var candidate = new ComputeProviderCandidate(new("type2-over-type3"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
            1024, 1, 1, true, false);
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(s.Buffer.Handle, new(0, 64)), new(output.Handle, new(0, 64)),
            ComputePublicationPreference.StagedRequired, false, false);
        var plan = s.Kernel.PlanCompute(s.Handle, intent, new(true, true, true), [candidate]);
        Assert.True(plan.IsSuccess, plan.Message);

        var control = s.Kernel.AllocateBuffer<byte>(s.Handle, 8).Value!;
        var controlUse = s.Kernel.AcquireRegionUse(s.Handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 8)).Value!;
        var fabric = new CxlAuthorityBridge(s.Kernel, s.Model, s.Model, s.Model, s.Model,
            new UnsupportedCoherent(), new EvidenceOnly()).BindFabric(s.Owner, controlUse.Handle, s.Subject, s.Lease,
            new(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, 8, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var accelerator = new CxlType2ModelAccelerator();
        var bridge = new CxlAuthorityBridge(s.Kernel, s.Model, s.Model, s.Model, s.Model,
            new UnsupportedCoherent(), new EvidenceOnly());
        var manager = new CxlFabricManagerAuthority(s.Kernel, s.Model);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        var execution = service.Submit(s.Handle, plan.Value!, [candidate], s.Subject, s.Lease, s.Endpoint, fabric, 1);
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.True(service.CompleteVisiblePublish(s.Handle, execution.Value!, [candidate], () => s.Buffer.Span.CopyTo(output.Span)).IsSuccess);

        Assert.True(s.Memory.Close(inputPlacement.PlacementId).IsSuccess);
        Assert.True(s.Memory.Close(outputPlacement.PlacementId).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MalformedFabricOrMemoryBinding_CompensationFailure_RemainsTrackedForTeardown(bool fabricFailure)
    {
        var s = CreateScenario();
        if (fabricFailure)
        {
            s.Model.ReturnMalformedFabricBinding = true;
            s.Model.FabricUnbindFails = true;
        }
        else
        {
            s.Model.ReturnMalformedMemoryBinding = true;
            s.Model.MemoryReleaseFails = true;
        }

        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmbiguousFabricOrMemoryAcceptance_QuarantinesBackingAndBlocksReclaim(bool fabricAcceptance)
    {
        var s = CreateScenario();
        if (fabricAcceptance)
            s.Model.FabricAcceptanceAmbiguous = true;
        else
            s.Model.MemoryAcceptanceAmbiguous = true;

        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderExceptionAfterFabricOrMemoryAcceptance_QuarantinesBacking(bool fabricAcceptance)
    {
        var s = CreateScenario();
        if (fabricAcceptance)
            s.Model.FabricThrowsAfterAcceptance = true;
        else
            s.Model.MemoryThrowsAfterAcceptance = true;

        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    private static CxlMemoryPlacementIntent Intent(CxlMemoryPlacementPreference preference) =>
        new(64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive, preference, 10, 0);

    private static Scenario CreateScenario(bool twoEndpoints = false)
    {
        var platform = new AuthorityProvider();
        var kernel = new RuntimeKernel(platform);
        var (_, handle) = TestFixtures.Create(kernel, 951, 952);
        var process = kernel.Processes.Resolve(handle).Value!;
        var owner = new RegionOwner(process.DomainId, handle.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, handle);
        var domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        PlatformDeviceLease Lease(string id)
        {
            var cap = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, id,
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
            return kernel.BindPlatformDevice(handle, domain, cap.CapabilityId,
                PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        }
        var lease = Lease("device:type3-0");
        PlatformDeviceLease? lease2 = null;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("type3-0"), lease.Device, 1024, latencyClass: 2, bandwidthClass: 5).IsSuccess);
        if (twoEndpoints)
        {
            lease2 = Lease("device:type3-1");
            Assert.True(model.RegisterEndpoint(new("type3-1"), lease2.Value.Device, 1024, latencyClass: 3, bandwidthClass: 4).IsSuccess);
        }
        var bridge = new CxlAuthorityBridge(kernel, model, model, model, model,
            new UnsupportedCoherent(), new EvidenceOnly());
        var memory = new CxlType3MemoryAuthority(kernel, bridge);
        var buffer = kernel.AllocateBuffer<byte>(handle, 64).Value!;
        var regionCapability = kernel.MintCapability(process.DomainId, handle, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Map).Value!.CapabilityId;
        return new(kernel, handle, owner, subject, domain, buffer, regionCapability, lease, lease2, model,
            model.QueryEndpoint(new("type3-0")).Value!, memory);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle, RegionOwner Owner,
        PlatformDomainIdentity Subject, PlatformDomainBinding Domain, OwnedBuffer<byte> Buffer, CapabilityId RegionCapability, PlatformDeviceLease Lease,
        PlatformDeviceLease? Lease2, CxlType3ModelProvider Model, CxlEndpointSnapshot Endpoint,
        CxlType3MemoryAuthority Memory);

    private sealed class UnsupportedCoherent : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding f, RegionUseDescriptor u) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding b) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class EvidenceOnly : ICxlSecurityEvidenceProvider
    {
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId id, CxlDeviceGeneration generation) =>
            PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        private ulong _nextDevice = 1;
        public PlatformProviderDescriptor Descriptor { get; } = new(new("type3-test"), 1,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), subject));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease d, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(_nextDevice++), new(1), d, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(new(new(1), new(1), d, r, a));
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => PlatformAuthorityResult.Ok();
    }
}
