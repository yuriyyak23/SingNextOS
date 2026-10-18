using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class HybridBootAuthorityAdmissionTests
{
    [Fact]
    public void P15_11_CurrentGenerationIsRequiredAndAdmissionCreatesNoProviderBinding()
    {
        var provider = new Provider();
        var kernel = new RuntimeKernel();
        var bridge = new CxlAuthorityBridge(kernel, provider, provider, provider, provider, provider, provider);
        var evidence = Takeover(provider.Snapshot());

        Assert.True(HybridBootAuthorityAdmission.RevalidateWithAuthorityOwner(evidence, bridge).IsSuccess);
        Assert.Equal(0, provider.BindCalls);

        provider.Generation++;
        var stale = HybridBootAuthorityAdmission.RevalidateWithAuthorityOwner(evidence, bridge);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, provider.BindCalls);
    }

    [Fact]
    public void P15_11_SameNumericEndpointAfterRefusalDoesNotInheritAuthority()
    {
        var provider = new Provider { Available = false };
        var kernel = new RuntimeKernel();
        var bridge = new CxlAuthorityBridge(kernel, provider, provider, provider, provider, provider, provider);
        var rejected = HybridBootAuthorityAdmission.RevalidateWithAuthorityOwner(Takeover(provider.Snapshot() with { Available = true }), bridge);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(0, provider.BindCalls);
    }

    [Fact]
    public void P15_12_QuarantinedFirmwareApertureBlocksRuntimeAdmission()
    {
        var provider = new Provider();
        var bridge = new CxlAuthorityBridge(new RuntimeKernel(), provider, provider, provider, provider, provider, provider);
        var takeover = Takeover(provider.Snapshot());
        takeover = HybridBootTakeoverResult.Success(takeover.Value! with
        {
            FirmwareAperture = FirmwareApertureDisposition.Quarantined,
        });

        var result = HybridBootAuthorityAdmission.RevalidateWithAuthorityOwner(takeover, bridge);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(0, provider.BindCalls);
    }

    private static HybridBootTakeoverResult Takeover(CxlEndpointSnapshot endpoint) =>
        HybridBootTakeoverResult.Success(new(
            new(Guid.NewGuid(), YAKSys_Hybrid_CPU.Boot.Contracts.ResetReason.ColdPowerOn, 1,
                new(YAKSys_Hybrid_CPU.Boot.Contracts.BootSecurityStatus.ManifestVerified, 1, 1, 1), false, []),
            endpoint, FirmwareApertureDisposition.Absent, 1));

    private sealed class Provider : ICxlDiscoveryProvider, ICxlIoProvider, ICxlFabricProvider, ICxlMemoryProvider,
        ICxlCoherentAccessProvider, ICxlSecurityEvidenceProvider
    {
        public CxlEndpointId Id { get; } = new("provider-current");
        public ulong Generation { get; set; } = 1;
        public bool Available { get; set; } = true;
        public int BindCalls { get; private set; }
        public CxlEndpointSnapshot Snapshot() => new(Id, new(Generation), CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, Available);
        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpointId) => PlatformAuthorityResult<CxlEndpointSnapshot>.Ok(Snapshot());
        public PlatformAuthorityResult<PlatformDeviceIdentity> ResolveDevice(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration) => Denied<PlatformDeviceIdentity>();
        public PlatformAuthorityResult<CxlFabricBinding> Bind(CxlFabricBindingRequest request) { BindCalls++; return Denied<CxlFabricBinding>(); }
        public PlatformAuthorityResult<CxlFabricBinding> Query(CxlFabricBindingId bindingId) => Denied<CxlFabricBinding>();
        public PlatformAuthorityResult Unbind(CxlFabricBinding binding) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "not bound");
        public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId endpointId) => Denied<CxlMemoryCapacitySnapshot>();
        public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabricBinding, RegionBackingLeaseDescriptor backingLease) { BindCalls++; return Denied<CxlMemoryBinding>(); }
        public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId bindingId) => Denied<CxlMemoryBinding>();
        public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "not bound");
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding fabricBinding, RegionUseDescriptor regionUse) { BindCalls++; return Denied<CxlCoherentBinding>(); }
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId bindingId) => Denied<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "not bound");
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration) => Denied<EvidenceRecord>();
        private static PlatformAuthorityResult<T> Denied<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.NotAccepted, "not used");
    }
}
