using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Drivers;

namespace SingPlus.Tests.Runtime;

public sealed class ReclaimObservabilityTests
{
    [Fact]
    public void BlockerCauseHasOnlyPendingOrErrorVariantAndPreservesDiagnosticShape()
    {
        ReclaimBlockerSnapshot pending = ReclaimBlockerSnapshot.Create(
            ReclaimDependencyKind.EndpointSession, "session:1", ReclaimExternalState.Active,
            null, "Still draining.");
        Assert.IsType<ReclaimPendingCause>(pending.Cause);
        Assert.Null(pending.Error);

        ReclaimBlockerSnapshot error = ReclaimBlockerSnapshot.Create(
            ReclaimDependencyKind.PlatformDomain, "process:1", ReclaimExternalState.Quarantined,
            KernelError.PlatformFaulted, "Closure failed.");
        Assert.Equal(KernelError.PlatformFaulted, Assert.IsType<ReclaimErrorCause>(error.Cause).Error);
        Assert.Equal(KernelError.PlatformFaulted, error.Error);
        Assert.Equal("process:1", error.ResourceId);
    }

    [Fact]
    public void ComponentFaultShowsQuarantinedDomainAndClosedSessions()
    {
        DiagnosticProvider provider = new DiagnosticProvider { DomainRevokeStatus = PlatformAuthorityStatus.Faulted };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (ComponentAdmissionPlan? plan, ComponentIdentity identity, ServiceContractIdentity contract) = CreateServiceComponent(1900, 19000);
        KernelResult<ComponentLifecycleSnapshot> admitted = kernel.AdmitComponent(plan);
        Assert.True(admitted.IsSuccess, admitted.Message);

        ProcessHandle caller = TestFixtures.Create(kernel, 1901, 19010).Handle;
        ServiceEndpointDescriptor service = Assert.Single(kernel.ResolveByContract(contract).Value!);
        EndpointSessionHandle session = kernel.OpenSession(caller, service).Value;

        KernelResult<EndpointSessionDiagnosticSnapshot[]> before = kernel.QueryEndpointSessionDiagnostics(admitted.Value!.Process);
        Assert.True(before.IsSuccess, before.Message);
        Assert.Contains(before.Value!, item => item.Session == session && item.State == EndpointSessionState.Active);
        PlatformAuthorityDiagnosticSnapshot platformBefore = kernel.QueryPlatformAuthorityDiagnostics(admitted.Value.Process).Value!;
        Assert.Equal(ReclaimExternalState.Active, platformBefore.DomainState);

        KernelResult<ComponentLifecycleSnapshot> fault = kernel.FaultComponent(identity);
        Assert.Equal(KernelError.PlatformFaulted, fault.Error);

        KernelResult<ComponentReclaimDiagnosticSnapshot> diagnostic = kernel.QueryComponentReclaimDiagnostics(identity);
        Assert.True(diagnostic.IsSuccess, diagnostic.Message);
        Assert.Equal(ComponentLifecycleState.Faulted, diagnostic.Value!.Component.State);
        Assert.False(diagnostic.Value.Process.Reclaimable);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, diagnostic.Value.Process.TeardownPhase);
        Assert.Equal(ReclaimExternalState.Quarantined, diagnostic.Value.Process.Platform.DomainState);
        Assert.NotNull(diagnostic.Value.BlockingDependency);
        Assert.Equal(ReclaimDependencyKind.PlatformDomain, diagnostic.Value.BlockingDependency!.Dependency);
        Assert.Equal(ReclaimExternalState.Quarantined, diagnostic.Value.BlockingDependency.ExternalState);
        Assert.Equal(KernelError.PlatformFaulted, diagnostic.Value.BlockingDependency.Error);
        Assert.Contains(diagnostic.Value.Process.Sessions, item =>
            item.Session == session &&
            item.State == EndpointSessionState.Closed &&
            item.PendingInvocations == 0 &&
            !item.ReclaimBlocking);
        Assert.True(kernel.Processes.Resolve(admitted.Value.Process).IsSuccess);
    }

    [Fact]
    public void MappingClosureFaultShowsExternalStateUnknownAndKeepsRegionPinned()
    {
        DiagnosticProvider provider = new DiagnosticProvider { MappingRevokeStatus = PlatformAuthorityStatus.Faulted };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        ProcessHandle owner = TestFixtures.Create(kernel, 1910, 19100).Handle;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 256).Value!;
        CapabilityId capability = kernel.MintCapability(
            new DomainId(19100),
            owner,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            buffer.Handle,
            PlatformMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);

        KernelResult terminate = kernel.TerminateProcess(owner);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);

        KernelResult<ProcessReclaimDiagnosticSnapshot> diagnostic = kernel.QueryProcessReclaimDiagnostics(owner);
        Assert.True(diagnostic.IsSuccess, diagnostic.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, diagnostic.Value!.TeardownPhase);
        Assert.False(diagnostic.Value.Reclaimable);
        Assert.NotNull(diagnostic.Value.BlockingDependency);
        Assert.Equal(ReclaimDependencyKind.PlatformRegionMapping, diagnostic.Value.BlockingDependency!.Dependency);
        Assert.Equal(ReclaimExternalState.ExternalStateUnknown, diagnostic.Value.BlockingDependency.ExternalState);
        PlatformResourceDiagnosticSnapshot mappingState = Assert.Single(
            diagnostic.Value.Platform.Resources,
            resource => resource.Kind == ReclaimDependencyKind.PlatformRegionMapping);
        Assert.Equal(CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), mappingState.ResourceId);
        Assert.Equal(ReclaimExternalState.ExternalStateUnknown, mappingState.ExternalState);
        Assert.True(mappingState.ReclaimBlocking);
        Assert.False(kernel.ReleaseRegion(owner, buffer).IsSuccess);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void DiagnosticsRemainPrivilegedAndContainNoProviderAuthorityTypes()
    {
        string[] publicQueries = typeof(RuntimeKernel).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name.Contains("Diagnostics", StringComparison.Ordinal))
            .Select(method => method.Name)
            .ToArray();
        Assert.DoesNotContain(nameof(RuntimeKernel.QueryComponentReclaimDiagnostics), publicQueries);
        Assert.DoesNotContain(nameof(RuntimeKernel.QueryProcessReclaimDiagnostics), publicQueries);
        Assert.DoesNotContain(nameof(RuntimeKernel.QueryPlatformAuthorityDiagnostics), publicQueries);
        Assert.DoesNotContain(nameof(RuntimeKernel.QueryEndpointSessionDiagnostics), publicQueries);

        Type[] diagnosticTypes = new[]
        {
            typeof(ReclaimBlockerSnapshot),
            typeof(EndpointSessionDiagnosticSnapshot),
            typeof(PlatformResourceDiagnosticSnapshot),
            typeof(PlatformAuthorityDiagnosticSnapshot),
            typeof(ProcessReclaimDiagnosticSnapshot),
            typeof(ComponentReclaimDiagnosticSnapshot),
        };
        foreach (Type? type in diagnosticTypes)
        {
            Assert.False(type.IsPublic);
            foreach (PropertyInfo property in type.GetProperties())
            {
                string shape = property.PropertyType.ToString();
                Assert.DoesNotContain("PlatformProvider", shape, StringComparison.Ordinal);
                Assert.DoesNotContain("PlatformOperation", shape, StringComparison.Ordinal);
                Assert.DoesNotContain("PhysicalAddress", shape, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Iommu", shape, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Vmcs", shape, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static (ComponentAdmissionPlan Plan, ComponentIdentity Identity, ServiceContractIdentity Contract) CreateServiceComponent(
        ulong processId,
        ulong domainId)
    {
        byte[] image = new byte[] { 0x51, 0x55, 0x41, (byte)(processId & 0xff) };
        SingProcessManifestV1 process = new SingProcessManifestV1(
            new ProcessId(processId),
            new DomainId(domainId),
            1,
            $"diagnostic-component-{processId}",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        ProtocolDefinitionV1 protocol = IDriverInterruptServiceProtocol.CreateDefinition();
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        ProvidedServiceManifestV1 provided = new ProvidedServiceManifestV1("diagnostic-service", contract);
        ComponentIdentity identity = new ComponentIdentity($"diagnostic-{processId}");
        ServiceManifestV1 manifest = new ServiceManifestV1(
            identity,
            new ComponentVersion("1"),
            Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            process,
            [provided],
            requiresPlatformDomain: true,
            platformRequirements:
            [
                new PlatformRequirementV1(
                    ComponentPlatformFeatureFamily.NeutralDomains,
                    PlatformDomainContract.ContractVersion,
                    ComponentPlatformFeatureAvailability.RuntimeAdmission),
            ]);
        return (
            new ComponentAdmissionPlan(
                manifest,
                image,
                providedServices:
                [
                    new ComponentProvidedServiceRegistration(
                        provided,
                        protocol,
                        IDriverInterruptServiceResponseProtocol.Definition),
                ]),
            identity,
            contract);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class DiagnosticProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider
    {
        private PlatformProviderDomainLease? _domain;
        private ulong _nextMapping = 1;

        public PlatformAuthorityStatus? DomainRevokeStatus { get; set; }
        public PlatformAuthorityStatus? MappingRevokeStatus { get; set; }
        public int DomainRevokeCalls { get; private set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("reclaim-observability-test"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
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

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            DomainRevokeCalls++;
            if (DomainRevokeStatus is { } status)
                return PlatformAuthorityResult.Fail(status, "Injected ambiguous domain closure.");
            _domain = null;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            if (_domain != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain.");
            }

            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(
                new PlatformProviderRegionMappingLease(
                    new PlatformProviderRegionMappingId(_nextMapping++),
                    new PlatformProviderLeaseGeneration(1),
                    domainLease,
                    region,
                    access));
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (MappingRevokeStatus is { } status)
                return PlatformAuthorityResult.Fail(status, "Injected ambiguous mapping closure.");
            return PlatformAuthorityResult.Ok();
        }
    }
}
