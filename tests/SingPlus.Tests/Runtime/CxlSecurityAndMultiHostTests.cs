using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class CxlSecurityAndMultiHostTests
{
    private static readonly CxlSecurityProperties SecureProperties =
        CxlSecurityProperties.LinkEncryptionAvailable | CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;

    [Fact]
    public void AuthenticatedEvidenceCannotReplaceRegionAuthority()
    {
        var s = CreateSecurityScenario(SecureProperties);
        var forged = s.Use.Handle with { UseId = new RegionUseId(9999) };
        var result = s.SecurityAuthority.Evaluate(s.Owner, forged, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.RegionUseNotFound, result.Error);
    }

    [Fact]
    public void ValidAuthorityStillRejectsMissingRequiredIdeEvidence()
    {
        var s = CreateSecurityScenario(CxlSecurityProperties.DeviceAuthenticated);
        var result = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
    }

    [Fact]
    public void SecuritySessionResetInvalidatesCapturedEvidenceGenerationOnly()
    {
        var s = CreateSecurityScenario(SecureProperties);
        Assert.True(s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true)).IsSuccess);
        Assert.True(s.SecurityProvider.ResetSecuritySession(s.Endpoint.EndpointId).IsSuccess);
        var stale = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true));
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.True(s.Kernel.Regions.ValidateUse(s.Use.Handle, s.Owner).IsSuccess);
    }

    [Fact]
    public void UnavailableSecurityDoesNotBreakNonSecureStagedEligibility()
    {
        var s = CreateSecurityScenario(null);
        var result = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(CxlSecurityProperties.None, new(1), false));
        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.Value!.Ready);
        Assert.Null(result.Value.SecurityState);
    }

    [Fact]
    public void ModelBackendCannotAdvertiseHardwareAttestedSecurity()
    {
        var s = CreateSecurityScenario(SecureProperties);
        var result = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), RequiredForOperation: true, RequireHardwareAttestation: true));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(CxlSecurityEvidenceAssurance.ModelOnly,
            s.SecurityProvider.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!.Assurance);
    }

    [Fact]
    public void WritableSecondHostIsRejectedUntilFenceAndReclaimComplete()
    {
        var kernel = new RuntimeKernel();
        var (_, ownerHandle) = TestFixtures.Create(kernel, 1001, 1002);
        var owner = new RegionOwner(new DomainId(1002), ownerHandle.Generation);
        var buffer = kernel.AllocateBuffer<byte>(ownerHandle, 32).Value!;
        var gate = new CxlMultiHostGate(kernel);
        var first = gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.Writable, false).Value!;
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.True(gate.Fence(first.Binding).IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.True(gate.BeginReclaim(first.Binding).IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.True(gate.CompleteReclaim(first.Binding).IsSuccess);
        Assert.True(gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).IsSuccess);
    }

    [Fact]
    public void DistributedWritableFlagCannotEnableUnimplementedProtocol()
    {
        var kernel = new RuntimeKernel();
        var (_, handle) = TestFixtures.Create(kernel, 1011, 1012);
        var buffer = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        var gate = new CxlMultiHostGate(kernel);
        var result = gate.Bind(buffer.Handle, new(new(1012), handle.Generation), new("host-a"),
            MultiHostAccessMode.Writable, false, distributedWritableAuthorityAvailable: true);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
    }

    [Fact]
    public void ReadOnlySharingRequiresExplicitProviderSupport()
    {
        var kernel = new RuntimeKernel();
        var (_, handle) = TestFixtures.Create(kernel, 1021, 1022);
        var owner = new RegionOwner(new(1022), handle.Generation);
        var buffer = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        var gate = new CxlMultiHostGate(kernel);
        Assert.Equal(KernelError.PlatformUnsupported,
            gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.ReadOnly, false).Error);
        Assert.True(gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.ReadOnly, true).IsSuccess);
        Assert.True(gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.ReadOnly, true).IsSuccess);
    }

    [Fact]
    public void HostDisappearanceFencesBeforeReclaimAndNeverReassignsSilently()
    {
        var kernel = new RuntimeKernel();
        var (_, handle) = TestFixtures.Create(kernel, 1025, 1026);
        var owner = new RegionOwner(new(1026), handle.Generation);
        var buffer = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        var gate = new CxlMultiHostGate(kernel);
        var first = gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.Writable, false).Value!;

        var disappeared = gate.RecordHostDisappearance(first.Binding);

        Assert.True(disappeared.IsSuccess, disappeared.Message);
        Assert.Equal(MultiHostBindingState.Fenced, disappeared.Value!.State);
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.True(gate.BeginReclaim(first.Binding).IsSuccess);
        Assert.True(gate.CompleteReclaim(first.Binding).IsSuccess);
        Assert.True(gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).IsSuccess);
    }

    [Fact]
    public void SecurityEvidenceSurfaceContainsNoAuthorityToken()
    {
        var properties = typeof(CxlSecurityStateSnapshot).GetProperties();
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(CapabilityId) ||
            property.PropertyType == typeof(RegionHandle) || property.Name.Contains("Authority", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(IPlatformAuthorityProvider), typeof(CxlSecurityModelProvider).GetInterfaces());
    }

    private static SecurityScenario CreateSecurityScenario(CxlSecurityProperties? properties)
    {
        var platform = new AuthorityProvider();
        var kernel = new RuntimeKernel(platform);
        var (_, handle) = TestFixtures.Create(kernel, 1031, 1032);
        var process = kernel.Processes.Resolve(handle).Value!;
        var owner = new RegionOwner(process.DomainId, handle.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, handle);
        var domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        var capability = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, "device:secure-cxl",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var lease = kernel.BindPlatformDevice(handle, domain, capability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-cxl"), lease.Device, 1024).IsSuccess);
        var endpoint = model.QueryEndpoint(new("secure-cxl")).Value!;
        var security = new CxlSecurityModelProvider();
        if (properties is not null) Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, properties.Value).IsSuccess);
        var bridge = new CxlAuthorityBridge(kernel, model, model, model, model,
            new CoherentStub(), security);
        var control = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        var use = kernel.AcquireRegionUse(handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 16)).Value!;
        var fabric = bridge.BindFabric(owner, use.Handle, subject, lease,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        return new(kernel, owner, subject, lease, endpoint, fabric, use, security, new(bridge, security));
    }

    private sealed record SecurityScenario(RuntimeKernel Kernel, RegionOwner Owner, PlatformDomainIdentity Subject,
        PlatformDeviceLease Lease, CxlEndpointSnapshot Endpoint, CxlFabricBinding Fabric, RegionUseDescriptor Use,
        CxlSecurityModelProvider SecurityProvider, CxlSecurityAuthority SecurityAuthority);

    private sealed class CoherentStub : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding f, RegionUseDescriptor u) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding b) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(new("security-test"), 1, PlatformAuthorityFeatures.NeutralDomainBinding);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity s) => PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), s));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease d, PlatformDeviceIdentity i, PlatformDeviceRights r) => PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), d, i, r));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) => PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(PlatformAuthorityStatus.Unsupported, "unused");
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => PlatformAuthorityResult.Ok();
    }
}
