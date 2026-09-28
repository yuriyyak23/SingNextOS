using System.Security.Cryptography;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class HybridCpuCxlDeploymentConformanceTests
{
    private static readonly byte[] Image = [0x48, 0x43, 0x50, 0x55, 0x2D, 0x49, 0x4D, 0x47];

    [Fact]
    public void SameImmutableImageExecutesAcrossLocalType3Type2AndGenericProviderContours()
    {
        byte[] digest = SHA256.HashData(Image);

        var local = CreateScenario(1801, 1802, "local");
        var localOutput = local.Kernel.AllocateBuffer<byte>(local.Process, Image.Length).Value!;
        Image.CopyTo(local.Input.Span);
        local.Input.Span.CopyTo(localOutput.Span);

        var type3 = CreateScenario(1811, 1812, "type3");
        var type3Output = type3.Kernel.AllocateBuffer<byte>(type3.Process, Image.Length).Value!;
        Image.CopyTo(type3.Input.Span);
        var inputPlacement = type3.Memory.Place(type3.Owner, type3.Input.Handle, type3.Subject,
            type3.Device, type3.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var outputPlacement = type3.Memory.Place(type3.Owner, type3Output.Handle, type3.Subject,
            type3.Device, type3.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        type3.Input.Span.CopyTo(type3Output.Span);
        Assert.True(type3.Memory.Close(inputPlacement.PlacementId).IsSuccess);
        Assert.True(type3.Memory.Close(outputPlacement.PlacementId).IsSuccess);

        var type2 = CreateScenario(1821, 1822, "type2");
        Image.CopyTo(type2.Input.Span);
        var type2Output = type2.Kernel.AllocateBuffer<byte>(type2.Process, Image.Length).Value!;
        var type2Execution = SubmitType2(type2, type2.Input, type2Output);
        Assert.True(type2Execution.Service.CompleteVisiblePublish(type2.Process, type2Execution.Execution,
            [type2Execution.Candidate], () => type2.Input.Span.CopyTo(type2Output.Span)).IsSuccess);

        var generic = CreateScenario(1831, 1832, "generic");
        Image.CopyTo(generic.Input.Span);
        var genericOutput = generic.Kernel.AllocateBuffer<byte>(generic.Process, Image.Length).Value!;
        RunGenericProvider(generic, generic.Input, genericOutput);

        Assert.Equal(Image, localOutput.Span.ToArray());
        Assert.Equal(Image, type3Output.Span.ToArray());
        Assert.Equal(Image, type2Output.Span.ToArray());
        Assert.Equal(Image, genericOutput.Span.ToArray());
        Assert.Equal(digest, SHA256.HashData(Image));
    }

    [Fact]
    public void DeploymentNegativeMatrixRejectsHotRemoveVisibilityFailureAndGenerationDrift()
    {
        var type3 = CreateScenario(1841, 1842, "type3-negative");
        var placement = type3.Memory.Place(type3.Owner, type3.Input.Handle, type3.Subject,
            type3.Device, type3.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(type3.Model.HotRemove(type3.Endpoint.EndpointId).IsSuccess);
        var refresh = type3.Memory.Refresh(placement.PlacementId);
        Assert.False(refresh.IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            type3.Memory.Query(placement.PlacementId).Value!.State);
        Assert.True(type3.Kernel.Regions.Validate(type3.Input.Handle, type3.Owner).IsSuccess);

        var type2 = CreateScenario(1851, 1852, "type2-negative");
        var output = type2.Kernel.AllocateBuffer<byte>(type2.Process, Image.Length).Value!;
        var execution = SubmitType2(type2, type2.Input, output);
        execution.Accelerator.VisibilitySatisfied = false;
        var publicationRan = false;
        var failedVisibility = execution.Service.CompleteVisiblePublish(type2.Process, execution.Execution,
            [execution.Candidate], () => publicationRan = true);
        Assert.False(failedVisibility.IsSuccess);
        Assert.False(publicationRan);
        Assert.Equal(ExternalOperationState.Released,
            type2.Kernel.ExternalOperations.Query(execution.Execution.Operation).Value!.State);

        var generic = CreateScenario(1861, 1862, "generic-negative");
        var genericOutput = generic.Kernel.AllocateBuffer<byte>(generic.Process, Image.Length).Value!;
        var generations = Generations(Guid.Parse("21f96592-aad9-440a-aed1-78b1c1c66b52"));
        var provider = GenericProvider(generic, generic.Input, genericOutput, generations);
        var semantic = Semantic(Guid.Parse("e54ea396-c198-4baa-a620-38f0df6506cf"));
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(provider.Admit(semantic).Receipt).Request;
        provider.Reconfigure(Generations(Guid.NewGuid()));
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, provider.Submit(request).Status);
        Assert.All(genericOutput.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void DeploymentAbiContainsNoHybridCpuIseOrProviderPrivateCxlIdentity()
    {
        var references = typeof(HybridCpuExternalOperationProvider).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, reference => reference.Name == "HybridCPU_ExternalRuntime.Contracts");
        Assert.DoesNotContain(references, reference =>
            reference.Name?.Contains("HybridCPU_ISE", StringComparison.OrdinalIgnoreCase) == true);

        string[] forbidden = ["Hdm", "Dpa", "Bdf", "PhysicalAddress", "FabricRoute", "SwitchPort"];
        var publicNames = typeof(HybridCpuExternalOperationProvider).GetMethods()
            .SelectMany(method => new[] { method.Name, method.ReturnType.Name }
                .Concat(method.GetParameters().Select(parameter => parameter.ParameterType.Name)));
        Assert.DoesNotContain(publicNames, name =>
            forbidden.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static void RunGenericProvider(Scenario scenario, OwnedBuffer<byte> input, OwnedBuffer<byte> output)
    {
        var provider = GenericProvider(scenario, input, output,
            Generations(Guid.Parse("74cfa0a9-35a7-4147-88ae-bf37656c036d")));
        var semantic = Semantic(Guid.Parse("2bbda506-529d-469f-90fe-91ac474f8096"));
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, provider.Submit(request).Receipt!.Stage);
        Assert.True(provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.Equal(Hc.ExternalOperationStage.DeviceComplete, provider.Poll(request).Receipt!.Stage);
        Assert.True(provider.RecordVisibility(request).IsSuccess);
        Assert.Equal(Hc.ExternalOperationStage.Visible, provider.Poll(request).Receipt!.Stage);
        Assert.True(provider.Publish(request, () => input.Span.CopyTo(output.Span)).IsSuccess);
        Assert.Equal(Hc.ExternalOperationStage.Published, provider.Poll(request).Receipt!.Stage);
        Assert.True(provider.Release(request, true).IsSuccess);
        Assert.Equal(Hc.ExternalOperationStage.Released, provider.Poll(request).Receipt!.Stage);
    }

    private static HybridCpuExternalOperationProvider GenericProvider(Scenario scenario,
        OwnedBuffer<byte> input, OwnedBuffer<byte> output, Hc.ExternalGenerationSet generations) => new(
        scenario.Kernel, scenario.Process,
        [new(input.Handle, RegionUseMode.ReadOnly, new(0, input.Length)),
         new(output.Handle, RegionUseMode.StagedOutput, new(0, output.Length))],
        new(5, 7, 11, 13), new("singnextos-generic"),
        new(new(Guid.Parse("ec936a28-9cee-4e29-b1d0-082847d9af30")), new(1)), generations);

    private static Type2Run SubmitType2(Scenario scenario, OwnedBuffer<byte> input, OwnedBuffer<byte> output)
    {
        var control = scenario.Kernel.AllocateBuffer<byte>(scenario.Process, 8).Value!;
        var controlUse = scenario.Kernel.AcquireRegionUse(scenario.Process, control.Handle,
            RegionUseMode.DevicePrivate, new(0, control.Length)).Value!;
        var bridge = new CxlAuthorityBridge(scenario.Kernel, scenario.Model, scenario.Model,
            scenario.Model, scenario.Model, new UnsupportedCoherent(), new UnsupportedEvidence());
        var fabric = bridge.BindFabric(scenario.Owner, controlUse.Handle, scenario.Subject, scenario.Device,
            new(scenario.Endpoint.EndpointId, scenario.Endpoint.DeviceGeneration, control.Length,
                CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var candidate = new ComputeProviderCandidate(new("type2-deployment"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
            1024, 1, 1, true, false);
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, input.Length)), new(output.Handle, new(0, output.Length)),
            ComputePublicationPreference.StagedRequired, false, false);
        var plan = scenario.Kernel.PlanCompute(scenario.Process, intent, new(true, true, true), [candidate]).Value!;
        var accelerator = new CxlType2ModelAccelerator();
        var manager = new CxlFabricManagerAuthority(scenario.Kernel, scenario.Model);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(scenario.Kernel, bridge, accelerator, manager);
        var execution = service.Submit(scenario.Process, plan, [candidate], scenario.Subject,
            scenario.Device, scenario.Endpoint, fabric, 1).Value!;
        return new(service, accelerator, candidate, execution);
    }

    private static Scenario CreateScenario(ulong processId, ulong domainId, string endpointName)
    {
        var platform = new AuthorityProvider(endpointName);
        var kernel = new RuntimeKernel(platform);
        var (_, process) = TestFixtures.Create(kernel, processId, domainId);
        var resolved = kernel.Processes.Resolve(process).Value!;
        var owner = new RegionOwner(resolved.DomainId, process.Generation);
        var subject = new PlatformDomainIdentity(resolved.DomainId, process);
        var domain = kernel.BindPlatformAuthorityDomain(process).Value!;
        var deviceCapability = kernel.MintCapability(resolved.DomainId, process, ResourceKind.Device,
            $"device:{endpointName}", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = kernel.BindPlatformDevice(process, domain, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new(endpointName), device.Device, 4096).IsSuccess);
        var endpoint = model.QueryEndpoint(new(endpointName)).Value!;
        var bridge = new CxlAuthorityBridge(kernel, model, model, model, model,
            new UnsupportedCoherent(), new UnsupportedEvidence());
        var memory = new CxlType3MemoryAuthority(kernel, bridge);
        var input = kernel.AllocateBuffer<byte>(process, Image.Length).Value!;
        return new(kernel, process, owner, subject, device, model, endpoint, memory, input);
    }

    private static Hc.ExternalOperationSemanticRequest Semantic(Guid correlation) => new(
        Hc.ExternalOperationContract.Version, new(correlation), Hc.ExternalEffectClass.NonIdempotent,
        Hc.ExternalVisibilityRequirement.StagedOutput, Hc.ExternalCancellationMode.ExactAcknowledgement,
        Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);

    private static Hc.ExternalGenerationSet Generations(Guid token) =>
        new(Hc.ExternalOperationContract.Version, [token]);

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Process, RegionOwner Owner,
        PlatformDomainIdentity Subject, PlatformDeviceLease Device, CxlType3ModelProvider Model,
        CxlEndpointSnapshot Endpoint, CxlType3MemoryAuthority Memory, OwnedBuffer<byte> Input);

    private sealed record Type2Run(CxlType2AcceleratorService Service,
        CxlType2ModelAccelerator Accelerator, ComputeProviderCandidate Candidate, CxlType2Execution Execution);

    private sealed class UnsupportedCoherent : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding binding, RegionUseDescriptor use) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class UnsupportedEvidence : ICxlSecurityEvidenceProvider
    {
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId endpoint, CxlDeviceGeneration generation) =>
            PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class AuthorityProvider(string name) : IPlatformAuthorityProvider,
        IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(new($"deployment-{name}"), 1,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        public PlatformFeatureManifest QueryFeatures() => new([
            new(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)]);
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), subject));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domain,
            PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), domain, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domain,
            PlatformRegionIdentity region, PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(new(new(1), new(1), domain, region, access));
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();
    }
}
