using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlAuthorityBridgeTests
{
    [Fact]
    public void NarrowProviderRolesRemainIndependentAndPhysicalIdentityIsNotPublic()
    {
        var assembly = typeof(ICxlDiscoveryProvider).Assembly;
        Assert.Null(assembly.GetType("SingPlus.Platform.ICxlProvider"));
        foreach (var role in new[] { typeof(ICxlDiscoveryProvider), typeof(ICxlIoProvider), typeof(ICxlMemoryProvider),
                     typeof(ICxlCoherentAccessProvider), typeof(ICxlFabricProvider), typeof(ICxlSecurityEvidenceProvider) })
            Assert.True(role.IsInterface);

        var publicSurface = assembly.GetExportedTypes()
            .Where(type => type.Namespace == "SingPlus.Platform" && type.Name.Contains("Cxl", StringComparison.Ordinal))
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(member => member.Name);
        foreach (var forbidden in new[] { "Hdm", "Dpa", "Hpa", "Decoder", "Pasid", "Requester", "RegisterOffset", "Mailbox" })
            Assert.DoesNotContain(publicSurface, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CxlIoUsesExistingPlatformDeviceLeasePath()
    {
        var s = CreateScenario();
        var resolved = s.Bridge.ResolveIoDevice(s.Cxl.Endpoint);
        Assert.True(resolved.IsSuccess, resolved.Message);
        Assert.Equal(s.Lease.Device, resolved.Value);
        Assert.Equal("device:cxl-semantic-0", s.Lease.Device.ResourceId);
    }

    [Fact]
    public void StaleDeviceGenerationRejectsBeforeFabricEffect()
    {
        var s = CreateScenario();
        var selected = s.Cxl.Endpoint;
        s.Cxl.DeviceGeneration++;
        var result = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(selected));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, s.Cxl.FabricBindCalls);
    }

    [Fact]
    public void StaleFabricAndMemoryGenerationsRejectRevalidation()
    {
        var s = CreateScenario();
        var fabric = s.Bridge.BindFabric(s.Owner, s.Use.Handle, s.Subject, s.Lease, Request(s.Cxl.Endpoint)).Value!;
        var memory = s.Bridge.BindMemory(s.Owner, s.Backing.Handle, fabric).Value!;
        Assert.True(s.Bridge.RevalidateBacking(s.Owner, s.Backing.Handle, s.Cxl.Endpoint, fabric, memory).IsSuccess);

        s.Cxl.FabricGeneration++;
        var staleFabric = s.Bridge.RevalidateBacking(s.Owner, s.Backing.Handle, s.Cxl.Endpoint, fabric, memory);
        Assert.False(staleFabric.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleFabric.Error);

        s.Cxl.FabricGeneration--;
        s.Cxl.MemoryGeneration++;
        var staleMemory = s.Bridge.RevalidateBacking(s.Owner, s.Backing.Handle, s.Cxl.Endpoint, fabric, memory);
        Assert.False(staleMemory.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleMemory.Error);
    }

    [Fact]
    public void DirectCoherentWriteIsFutureGatedUntilCpuAliasExclusionExists()
    {
        var platform = new AuthorityProvider();
        var kernel = new RuntimeKernel(platform);
        var (_, handle) = TestFixtures.Create(kernel, 905, 906);
        var buffer = kernel.AllocateBuffer<byte>(handle, 64).Value!;
        var result = kernel.AcquireRegionUse(handle, buffer.Handle, RegionUseMode.DirectCoherentWrite, new(0, 64));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
    }

    [Fact]
    public void TrustedSecurityEvidenceDoesNotCreateRegionAuthority()
    {
        var s = CreateScenario();
        Assert.True(s.Bridge.QuerySecurityEvidence(s.Cxl.Endpoint).IsSuccess);
        var fabricatedUse = s.Use.Handle with { UseId = new RegionUseId(9999) };

        var result = s.Bridge.BindFabric(s.Owner, fabricatedUse, s.Subject, s.Lease, Request(s.Cxl.Endpoint));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Cxl.FabricBindCalls);
    }

    private static CxlFabricBindingRequest Request(CxlEndpointSnapshot endpoint) =>
        new(endpoint.EndpointId, endpoint.DeviceGeneration, 64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive);

    private static Scenario CreateScenario(RegionUseMode mode = RegionUseMode.StagedOutput)
    {
        var platform = new AuthorityProvider();
        var kernel = new RuntimeKernel(platform);
        var (_, handle) = TestFixtures.Create(kernel, 901, 902);
        var process = kernel.Processes.Resolve(handle).Value!;
        var owner = new RegionOwner(process.DomainId, handle.Generation);
        var buffer = kernel.AllocateBuffer<byte>(handle, 64).Value!;
        var backing = kernel.Regions.ReserveBacking(buffer.Handle, owner).Value!;
        var use = kernel.AcquireRegionUse(handle, buffer.Handle, mode, new(0, 64)).Value!;
        var domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        var capability = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, "device:cxl-semantic-0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var lease = kernel.BindPlatformDevice(handle, domain, capability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var cxl = new CxlProviders(process.DomainId, handle);
        var bridge = new CxlAuthorityBridge(kernel, cxl, cxl, cxl, cxl, cxl, cxl);
        return new(kernel, owner, new PlatformDomainIdentity(process.DomainId, handle), backing, use, lease, cxl, bridge);
    }

    private sealed record Scenario(RuntimeKernel Kernel, RegionOwner Owner, PlatformDomainIdentity Subject,
        RegionBackingLeaseDescriptor Backing, RegionUseDescriptor Use, PlatformDeviceLease Lease, CxlProviders Cxl, CxlAuthorityBridge Bridge);

    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(new("cxl-test-platform"), 1, PlatformAuthorityFeatures.NeutralDomainBinding);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), subject));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), domainLease, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(PlatformAuthorityStatus.Unsupported, "Not used.");
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();
    }

    private sealed class CxlProviders : ICxlDiscoveryProvider, ICxlIoProvider, ICxlFabricProvider,
        ICxlMemoryProvider, ICxlCoherentAccessProvider, ICxlSecurityEvidenceProvider
    {
        private readonly EvidenceSubjectIdentity _subject;
        public CxlProviders(DomainId domain, ProcessHandle process) =>
            _subject = new(domain, process.ProcessId, process.Generation, null, null);
        public ulong DeviceGeneration { get; set; } = 1;
        public ulong FabricGeneration { get; set; } = 1;
        public ulong MemoryGeneration { get; set; } = 1;
        public ulong CoherentGeneration { get; set; } = 1;
        public CxlEndpointFeatures Features { get; set; } = CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory | CxlEndpointFeatures.CoherentAccess;
        public int FabricBindCalls { get; private set; }
        public CxlEndpointSnapshot Endpoint => new(new("endpoint-0"), new(DeviceGeneration), Features, true);
        private CxlFabricBinding? _fabric;
        private CxlMemoryBinding? _memory;
        private CxlCoherentBinding? _coherent;

        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpointId) =>
            endpointId == Endpoint.EndpointId ? PlatformAuthorityResult<CxlEndpointSnapshot>.Ok(Endpoint) : Fail<CxlEndpointSnapshot>();
        public PlatformAuthorityResult<PlatformDeviceIdentity> ResolveDevice(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration) =>
            expectedGeneration.Value == DeviceGeneration ? PlatformAuthorityResult<PlatformDeviceIdentity>.Ok(new("device:cxl-semantic-0")) : Stale<PlatformDeviceIdentity>();
        public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId endpointId) =>
            PlatformAuthorityResult<CxlMemoryCapacitySnapshot>.Ok(new(endpointId, new(DeviceGeneration), 4096, 4096,
                CxlMemoryPersistence.Volatile, 0, 1, 1));
        public PlatformAuthorityResult<CxlFabricBinding> Bind(CxlFabricBindingRequest request)
        {
            FabricBindCalls++;
            _fabric = new(new(1), new(FabricGeneration), request.EndpointId, request.DeviceGeneration, request.CapacityBytes);
            return PlatformAuthorityResult<CxlFabricBinding>.Ok(_fabric);
        }
        public PlatformAuthorityResult<CxlFabricBinding> Query(CxlFabricBindingId bindingId) => _fabric is null ? Fail<CxlFabricBinding>() :
            PlatformAuthorityResult<CxlFabricBinding>.Ok(_fabric with { Generation = new(FabricGeneration) });
        public PlatformAuthorityResult Unbind(CxlFabricBinding binding) { _fabric = null; return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabricBinding, RegionBackingLeaseDescriptor backingLease)
        {
            _memory = new(new(1), new(MemoryGeneration), fabricBinding, backingLease.Handle);
            return PlatformAuthorityResult<CxlMemoryBinding>.Ok(_memory);
        }
        public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId bindingId) => _memory is null ? Fail<CxlMemoryBinding>() :
            PlatformAuthorityResult<CxlMemoryBinding>.Ok(_memory with { Generation = new(MemoryGeneration) });
        public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding) { _memory = null; return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding fabricBinding, RegionUseDescriptor regionUse)
        {
            _coherent = new(new(1), new(CoherentGeneration), fabricBinding, regionUse.Handle);
            return PlatformAuthorityResult<CxlCoherentBinding>.Ok(_coherent);
        }
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId bindingId) => _coherent is null ? Fail<CxlCoherentBinding>() :
            PlatformAuthorityResult<CxlCoherentBinding>.Ok(_coherent with { Generation = new(CoherentGeneration) });
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding) { _coherent = null; return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration) =>
            PlatformAuthorityResult<EvidenceRecord>.Ok(new(new("cxl-security", 1), _subject, new("fake-security"),
                EvidenceVisibilityClass.SecurityMeasurement, "trusted", "opaque", new(1, 1), false));
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Denied, "Unknown.");
        private static PlatformAuthorityResult<T> Stale<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Stale, "Stale.");
    }
}
