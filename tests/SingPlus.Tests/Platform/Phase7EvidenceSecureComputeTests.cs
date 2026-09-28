using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class Phase7EvidenceSecureComputeTests
{
    [Fact]
    public void MissingEvidenceCapabilityMakesZeroProviderEvidenceCalls()
    {
        var scenario = Create();
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, new CapabilityId(999), scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.PublicDiagnostic, new(7, 1));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
        Assert.Equal(0, scenario.Provider.CatalogCalls);
    }

    [Fact]
    public void ForgedGenerationOrRevokedEvidenceAuthorityMakesZeroProviderEvidenceCalls()
    {
        var scenario = Create(withEvidenceCapability: true);
        var forgedParent = scenario.Parent with
        {
            Generation = new PlatformDomainBindingGeneration(scenario.Parent.Generation.Value + 1)
        };

        var forged = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, forgedParent,
            new("runtime.health", 1), EvidenceVisibilityClass.PublicDiagnostic, new(7, 1));
        Assert.False(forged.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);

        Assert.True(scenario.Kernel.RevokeCapability(scenario.EvidenceCapability).IsSuccess);
        var revoked = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.PublicDiagnostic, new(7, 1));
        Assert.False(revoked.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Fact]
    public void HostInternalIsRejectedBeforeProviderCall()
    {
        var scenario = Create(withEvidenceCapability: true);
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.HostInternal, new(7, 1));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Theory]
    [InlineData(PlatformFeatureAvailability.ModelOnly)]
    [InlineData(PlatformFeatureAvailability.ProjectionOnly)]
    [InlineData(PlatformFeatureAvailability.RuntimeAdmission)]
    [InlineData(PlatformFeatureAvailability.ExecutableButNotProductionSecure)]
    [InlineData(PlatformFeatureAvailability.Executable)]
    public void WeakerReadinessCannotAdmitSecureDomain(PlatformFeatureAvailability availability)
    {
        var scenario = Create(secureAvailability: availability, withSecureCapability: true);
        var result = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.SecureCreateCalls);
    }

    [Fact]
    public void UnsupportedSecurePropertyFailsWithoutOrdinaryFallback()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.MeasuredLaunch];
        var result = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(0, scenario.Provider.OrdinaryVirtualDomainCalls);
    }

    [Theory]
    [InlineData(SecureLeaseMode.ZeroIdentity)]
    [InlineData(SecureLeaseMode.WrongParent)]
    [InlineData(SecureLeaseMode.NullProperties)]
    [InlineData(SecureLeaseMode.UnknownProperty)]
    public void MalformedSecureAdmissionFailsClosedAndAttemptsExactCleanup(SecureLeaseMode mode)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Provider.SecureLeaseMode = mode;
        var result = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
    }

    [Fact]
    public void SecureRegionBlocksMappingReclaimUntilExactUnbind()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var regionCapability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, regionCapability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(secure.IsSuccess, secure.Message);
        Assert.True(scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Value!.Domain, secure.Value.MemoryCapability,
            mapping.Value!, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping.Value).Error);
        Assert.True(scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Value.Domain, secure.Value.MemoryCapability, mapping.Value).IsSuccess);
        var revoked = scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping.Value);
        Assert.True(revoked.IsSuccess, revoked.Message);
    }

    [Fact]
    public void StaleWrongSubjectAndMalformedEvidenceFailClosed()
    {
        var scenario = Create(withEvidenceCapability: true);
        foreach (var mode in new[] { EvidenceMode.Stale, EvidenceMode.WrongOwner, EvidenceMode.WrongDomain, EvidenceMode.Malformed, EvidenceMode.Revoked, EvidenceMode.Faulted, EvidenceMode.Unknown })
        {
            scenario.Provider.EvidenceMode = mode;
            var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
                new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new(7, 2));
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public void HostInternalCatalogEntriesAreFilteredAtBridgeBoundary()
    {
        var scenario = Create(withEvidenceCapability: true);
        var catalog = scenario.Kernel.QueryPlatformEvidenceCatalog(scenario.Owner, scenario.EvidenceCapability, scenario.Parent);
        Assert.True(catalog.IsSuccess, catalog.Message);
        Assert.DoesNotContain(catalog.Value!.Entries, entry => entry.Visibility == EvidenceVisibilityClass.HostInternal);
    }

    [Fact]
    public void ModelEvidenceCannotClaimHardwareRootedAttestation()
    {
        var scenario = Create(withEvidenceCapability: true);
        scenario.Provider.EvidenceMode = EvidenceMode.Valid;
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new(7, 1));
        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.Value!.HardwareRooted);
    }

    [Fact]
    public void ModelProviderCannotPublishHardwareRootedEvidence()
    {
        var scenario = Create(withEvidenceCapability: true);
        scenario.Provider.EvidenceMode = EvidenceMode.HardwareRooted;
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new(7, 1));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void UnknownVisibilityFailsBeforeProviderCall()
    {
        var scenario = Create(withEvidenceCapability: true);
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), (EvidenceVisibilityClass)999, new(7, 1));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Fact]
    public void ProcessTeardownDrainsAndClosesSecureDomainBeforeParent()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var active = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(active.IsSuccess, active.Message);
        var terminated = scenario.Kernel.TerminateProcess(scenario.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(1, scenario.Provider.DrainCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        Assert.Equal(1, scenario.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void BackendResetQuarantinesSecureDomainAndStaleEvidenceContext()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var active = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(active.IsSuccess, active.Message);
        var reset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(reset.IsSuccess, reset.Message);
        var read = scenario.Kernel.ReadSecureDomainEvidence(scenario.Owner, active.Value!.Domain,
            active.Value.EvidenceCapability, scenario.Parent, new("runtime.health", 1),
            EvidenceVisibilityClass.SecurityMeasurement, new(7, 1));
        Assert.False(read.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Fact]
    public void DowngradeBlocksFutureAdmissionButActiveDomainUsesExactDrainAndClose()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var active = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(active.IsSuccess, active.Message);
        scenario.Provider.SecureAvailability = PlatformFeatureAvailability.Unavailable;
        scenario.Kernel.RefreshPlatformFeatures();
        var future = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(future.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        var closed = scenario.Kernel.DestroySecureDomain(scenario.Owner, active.Value!.Domain, active.Value.ConfigureCapability);
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, scenario.Provider.DrainCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
    }

    [Fact]
    public void EvidenceAndManifestTypesAreNotAuthorityOrReclaimProof()
    {
        Type[] evidenceTypes = [typeof(EvidenceRecord), typeof(EvidenceCatalogEntry), typeof(PlatformEvidenceCatalog), typeof(PlatformFeatureManifest)];
        foreach (var type in evidenceTypes)
        {
            Assert.False(typeof(CapabilityId).IsAssignableFrom(type));
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
                property.PropertyType == typeof(PlatformProviderDomainLease) ||
                property.PropertyType == typeof(PlatformProviderRegionMappingLease) ||
                property.PropertyType == typeof(VirtualDomainHandle) ||
                property.PropertyType == typeof(SecureDomainHandle) ||
                property.PropertyType == typeof(CapabilityId));
            Assert.DoesNotContain(type.GetMembers().Select(member => member.Name), name =>
                name.Contains("Reclaim", StringComparison.OrdinalIgnoreCase) || name.Contains("CompletionAuthority", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ExternalGateTaxonomyHasEveryRequirementAndNoSilentUnknownState()
    {
        Assert.Equal(Enum.GetValues<PlatformExternalRequirement>().Length, PlatformExternalGateTable.Current.Count);
        Assert.All(Enum.GetValues<PlatformExternalRequirement>(), requirement =>
            Assert.Single(PlatformExternalGateTable.Current, gate => gate.Requirement == requirement));
        Assert.All(PlatformExternalGateTable.Current, gate => Assert.True(Enum.IsDefined(gate.State)));
        Assert.Contains(PlatformExternalGateTable.FeatureGates, gate => gate.Requirement == PlatformExternalRequirement.ExtHcpu006 &&
            gate.Family == PlatformFeatureFamily.PlatformEvidence && gate.State == PlatformExternalGateState.Unavailable);
        Assert.Contains(PlatformExternalGateTable.FeatureGates, gate => gate.Requirement == PlatformExternalRequirement.ExtHcpu006 &&
            gate.Family == PlatformFeatureFamily.SecureDomains && gate.State == PlatformExternalGateState.Unavailable);
        Assert.Contains(PlatformExternalGateTable.FeatureGates, gate => gate.Requirement == PlatformExternalRequirement.ExtHcpu006 &&
            gate.Family == PlatformFeatureFamily.NestedDomains && gate.State == PlatformExternalGateState.Unavailable);
    }

    private static Scenario Create(PlatformFeatureAvailability secureAvailability = PlatformFeatureAvailability.Unavailable,
        bool withEvidenceCapability = false, bool withSecureCapability = false)
    {
        var provider = new Phase7Provider { SecureAvailability = secureAvailability };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 700, 701);
        var parent = kernel.BindPlatformAuthorityDomain(owner);
        Assert.True(parent.IsSuccess, parent.Message);
        CapabilityId evidence = default, secure = default;
        if (withEvidenceCapability) evidence = kernel.MintCapability(process.DomainId, owner, ResourceKind.Evidence, EvidenceResourceIds.Read, CapabilityRights.Read).Value!.CapabilityId;
        if (withSecureCapability) secure = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute, SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        return new(kernel, provider, owner, parent.Value!, evidence, secure);
    }

    private readonly record struct Scenario(RuntimeKernel Kernel, Phase7Provider Provider, ProcessHandle Owner,
        PlatformDomainBinding Parent, CapabilityId EvidenceCapability, CapabilityId SecureCapability);
    public enum SecureLeaseMode { Valid, ZeroIdentity, WrongParent, NullProperties, UnknownProperty }
    private enum EvidenceMode { Valid, Stale, WrongOwner, WrongDomain, Malformed, Revoked, Faulted, Unknown, HardwareRooted }

    private sealed class Phase7Provider : IPlatformAuthorityProvider, IPlatformFeatureProvider, IPlatformEvidenceProvider, IPlatformSecureComputeProvider, IPlatformRegionRevocationProvider
    {
        private readonly HostPlatformAuthorityProvider _host = new();
        public PlatformProviderDescriptor Descriptor => _host.Descriptor;
        public PlatformFeatureAvailability SecureAvailability { get; set; }
        public IReadOnlyList<SecureProperty> ProvenProperties { get; set; } = [];
        public EvidenceMode EvidenceMode { get; set; }
        public SecureLeaseMode SecureLeaseMode { get; set; }
        public int ReadCalls { get; private set; }
        public int CatalogCalls { get; private set; }
        public int SecureCreateCalls { get; private set; }
        public int SecureRevokeCalls { get; private set; }
        public int DrainCalls { get; private set; }
        public int OrdinaryVirtualDomainCalls { get; private set; }
        public int ParentRevokeCalls { get; private set; }
        private ulong _nextSecure = 1;
        private ulong _nextSecureRegion = 1;

        public PlatformFeatureManifest QueryFeatures()
        {
            var entries = _host.QueryFeatures().Features.ToList();
            entries.Add(new(PlatformFeatureFamily.PlatformEvidence, 1, PlatformFeatureAvailability.RuntimeAdmission));
            if (SecureAvailability != PlatformFeatureAvailability.Unavailable) entries.Add(new(PlatformFeatureFamily.SecureDomains, 1, SecureAvailability));
            return new(entries);
        }
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) => _host.BindDomain(subject);
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) { ParentRevokeCalls++; return _host.RevokeDomain(lease); }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access) => _host.MapOwnedRegion(domainLease, region, access);
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => _host.RevokeRegionMapping(mapping, policy);
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => _host.BeginRegionMappingRevocation(mapping, policy);
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) => _host.ObserveCompletion(operation);
        public PlatformAuthorityResult<PlatformEvidenceCatalog> QueryEvidenceCatalog(PlatformEvidenceCatalogRequest request) { CatalogCalls++; return PlatformAuthorityResult<PlatformEvidenceCatalog>.Ok(new([
            new(new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new("host-model"), false),
            new(new("host.secret", 1), EvidenceVisibilityClass.HostInternal, new("host-model"), false)], 7)); }
        public PlatformAuthorityResult<EvidenceRecord> ReadEvidence(PlatformEvidenceReadRequest request)
        {
            ReadCalls++;
            if (EvidenceMode == EvidenceMode.Revoked) return PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Revoked, "revoked");
            if (EvidenceMode == EvidenceMode.Faulted) return PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Faulted, "faulted");
            if (EvidenceMode == EvidenceMode.Unknown) return new PlatformAuthorityResult<EvidenceRecord>((PlatformAuthorityStatus)999, default, "unknown");
            var subject = request.Subject.Subject;
            var freshness = EvidenceMode == EvidenceMode.Stale ? new EvidenceFreshness(6, 1) : new EvidenceFreshness(7, 2);
            var process = EvidenceMode == EvidenceMode.WrongOwner ? new ProcessId(999) : subject.ProcessId;
            var domain = EvidenceMode == EvidenceMode.WrongDomain ? new DomainId(999) : subject.DomainId;
            var producer = EvidenceMode == EvidenceMode.Malformed ? new EvidenceProducerIdentity("") : new EvidenceProducerIdentity("host-model");
            var evidenceSubject = request.EvidenceSubject with { SubjectDomain = domain, SubjectProcess = process };
            return PlatformAuthorityResult<EvidenceRecord>.Ok(new(request.Identity, evidenceSubject, producer,
                request.Visibility, "Healthy", "model-only", freshness, EvidenceMode == EvidenceMode.HardwareRooted));
        }
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease> CreateSecureDomain(PlatformSecureDomainRequest request)
        {
            SecureCreateCalls++;
            var id = SecureLeaseMode == SecureLeaseMode.ZeroIdentity ? new PlatformProviderSecureDomainId(0) : new(_nextSecure++);
            var parent = SecureLeaseMode == SecureLeaseMode.WrongParent
                ? request.Parent with { Generation = new(request.Parent.Generation.Value + 1) }
                : request.Parent;
            IReadOnlyList<SecureProperty> properties = SecureLeaseMode switch
            {
                SecureLeaseMode.NullProperties => null!,
                SecureLeaseMode.UnknownProperty => [(SecureProperty)999],
                _ => ProvenProperties,
            };
            return PlatformAuthorityResult<PlatformProviderSecureDomainLease>.Ok(new(id, new(1), parent, properties));
        }
        public PlatformAuthorityResult<PlatformProviderSecureRegionBinding> BindSecureRegion(PlatformProviderSecureDomainLease domain, PlatformProviderRegionMappingLease mapping, PlatformSecureRegionClass regionClass) =>
            PlatformAuthorityResult<PlatformProviderSecureRegionBinding>.Ok(new(new(_nextSecureRegion++), new(1), domain, mapping, regionClass));
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> UnbindSecureRegion(PlatformProviderSecureRegionBinding binding) =>
            PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Ok(new(binding, true));
        public PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> TransitionSecureDomain(PlatformProviderSecureDomainLease domain, PlatformSecureDomainTransition transition)
        {
            if (transition == PlatformSecureDomainTransition.BeginDrain) DrainCalls++;
            return PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>.Ok(new(domain, transition, true));
        }
        public PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> RevokeSecureDomain(PlatformProviderSecureDomainLease domain)
        {
            SecureRevokeCalls++;
            return PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>.Ok(new(domain, true));
        }
    }
}
