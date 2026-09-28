using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class SecureExecutionBindingTests
{
    [Fact]
    public void ExactCompositionAndTerminalCloseAreIdempotent()
    {
        var s = Create(); var binding = Bind(s);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.True(s.Kernel.RevalidateSecureExecution(binding.Value).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.False(s.Kernel.CloseSecureExecution(binding.Value with { Generation = 2 }).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BothExactCapabilitiesAndCurrentDomainGenerationsAreRequired(int fault)
    {
        var s = Create();
        var result = s.Kernel.BindSecureExecution(s.Owner,
            fault == 2 ? s.Virtual.Domain with { Generation = new(2) } : s.Virtual.Domain,
            fault == 0 ? new(999999) : fault == 4 ? s.Secure.EvidenceCapability : s.Virtual.ConfigureCapability,
            fault == 3 ? s.Secure.Domain with { Generation = new(2) } : s.Secure.Domain,
            fault == 1 ? new(999999) : fault == 5 ? s.Secure.EvidenceCapability : s.Secure.ConfigureCapability);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Provider.BindCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MissingUnsupportedOrNonProductionCompositionContractCannotAdmit(int fault)
    {
        var s = Create();
        s.Provider.SecureExecutionContractVersion = fault == 0 ? 0u : fault == 1 ? 2u : 1u;
        s.Provider.ProductionSecureExecution = fault != 2;
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(0, s.Provider.BindCalls);
    }

    [Fact]
    public void ParentMismatchCannotCompose()
    {
        var s = Create();
        var other = TestFixtures.Create(s.Kernel, 991, 992).Handle;
        var parent = s.Kernel.BindPlatformAuthorityDomain(other).Value;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var create = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.SecureCompute,
            SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        // Provider bridge checks owner equality before materializing another-parent secure authority.
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, create, parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        var composed = Bind(s);
        Assert.True(composed.IsSuccess);
        var context = s.Provider.LastRequest.Context;
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(context.Virtual, context.Secure,
            context.Child with { ParentBinding = parent }, context.SecureBinding,
            context.LocalPolicyGeneration, context.LocalProtectionGeneration).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void ProviderChildAndSecureBindingStalenessAreRejectedBeforeNewAdmission()
    {
        var s = Create(); var binding = Bind(s).Value;
        var c = s.Provider.LastRequest.Context;
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(c.Virtual, c.Secure,
            c.Child with { Generation = new(2) }, c.SecureBinding, 1, 1).IsSuccess);
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(c.Virtual, c.Secure,
            c.Child, c.SecureBinding with { Generation = new(2) }, 1, 1).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.RevokeSecureDomain(c.SecureBinding).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void ClosedProviderChildCannotBeUsedForNewAdmission()
    {
        var s = Create(); var binding = Bind(s).Value;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(s.Provider.LastRequest.Context.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.CloseChildDomain(s.Provider.LastRequest.Context.Child).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void PublicationRevalidatesAfterProviderEventAndRollsBackStaging()
    {
        var s = Create(); var binding = Bind(s).Value;
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        s.Provider.DuringEvent = () => s.Provider.RevalidationFault = 0;
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        var terminated = s.Kernel.TerminateProcess(s.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
    }

    [Fact]
    public void InFlightProviderEffectPinsCompositionAgainstCloseAndTeardown()
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.DuringChildEffect = () => {
            Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.CloseCalls);
        };
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void BackendResetQuarantinesCompositionWithoutNewProviderEffects()
    {
        var s = Create(); var binding = Bind(s).Value;
        Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(0, s.Provider.RevalidateCalls);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void MalformedOrPartialAdmissionCompensatesExactRequest(int fault)
    {
        var s = Create(); s.Provider.AdmissionFault = fault;
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseRequest);
        Assert.True(s.Kernel.CloseSecureExecution(s.Provider.LastRequest.Binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void PolicyProtectionProviderChildAndSecureLeaseChangesQuarantine(int fault)
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.RevalidationFault = fault;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        var calls = s.Provider.RevalidateCalls;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.RevalidateCalls);
        Assert.False(Bind(s).IsSuccess);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, s.Secure.Domain, s.Secure.ExecuteCapability,
            PlatformSecureDomainTransition.Start).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void AmbiguousClosurePinsProcessAndDomainsUntilExactRecovery(int fault)
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.ClosureFault = fault;
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.True(s.Kernel.Processes.Resolve(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
        s.Provider.ClosureFault = -1;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        var calls = s.Provider.CloseCalls;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.CloseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(new[] { "execution", "secure", "child" }, s.Provider.TerminalOrder);
    }

    [Fact]
    public void AmbiguousAdmissionAndLostResponseRemainRecoverableByExactRequest()
    {
        var s = Create(); s.Provider.AdmissionFault = 5; s.Provider.ClosureFault = 0;
        Assert.False(Bind(s).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.ClosureFault = -1;
        Assert.True(s.Kernel.CloseSecureExecution(s.Provider.LastRequest.Binding).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void ContainmentIsTerminalButUnavailableIsNot()
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.Contained = true;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
    }

    [Fact]
    public void ReplayedCorrelationAndCrossBindingReceiptCannotAdmit()
    {
        var s = Create(); var first = Bind(s).Value;
        Assert.True(s.Kernel.CloseSecureExecution(first).IsSuccess);
        s.Provider.ReplayCorrelation = true;
        Assert.False(Bind(s).IsSuccess);
        s.Provider.AdmissionFault = 0;
        Assert.False(Bind(s).IsSuccess);
    }

    [Fact]
    public void TeardownDuringProviderAdmissionStopsNewAdmissionsAndCompensates()
    {
        var s = Create();
        s.Provider.DuringBind = () => Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void PublicAndSipSurfacesDoNotExposeCompositionOrProviderIdentity()
    {
        Assert.False(typeof(SecureExecutionBinding).IsPublic);
        Assert.False(typeof(SecureExecutionContext).IsPublic);
        Assert.False(typeof(ISecureExecutionProvider).IsPublic);
        Assert.DoesNotContain(typeof(RuntimeKernel).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name.Contains("SecureExecution"));
        Assert.DoesNotContain(typeof(SecureDomainHandle).Assembly.GetExportedTypes(), type => type.Name.Contains("SecureExecution"));
    }

    [Fact]
    public void LocalProtectionChangeInvalidatesComposition()
    {
        var s = Create(); var binding = Bind(s).Value;
        var context = s.Provider.LastRequest.Context;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapped = s.Kernel.MapPlatformOwnedRegion(s.Owner, context.Parent, cap, region.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value,
            PlatformSecureRegionClass.Private).IsSuccess);
        var calls = s.Provider.RevalidateCalls;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.RevalidateCalls);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value,
            PlatformSecureRegionClass.Private).IsSuccess);
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, s.Secure.Domain, s.Secure.ExecuteCapability,
            PlatformSecureDomainTransition.Start).IsSuccess);
        Assert.True(s.Kernel.CloseSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RevokedEitherAuthorityBlocksEffectsBeforeProviderRevalidation(bool secure)
    {
        var s = Create(); var binding = Bind(s).Value;
        Assert.True(s.Kernel.RevokeCapability(secure ? s.Secure.ConfigureCapability : s.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(0, s.Provider.RevalidateCalls);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
    }

    [Fact]
    public void ProviderFeaturesAndDiagnosticObjectsCannotSupplyAuthorityOrBindImplicitly()
    {
        var s = Create();
        Assert.Equal(PlatformFeatureAvailability.ProductionSecure,
            s.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.SecureDomains).Availability);
        Assert.Equal(0, s.Provider.BindCalls);
        var operation = typeof(RuntimeKernel).GetMethod("BindSecureExecution", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(new[] { typeof(ProcessHandle), typeof(VirtualDomainHandle), typeof(CapabilityId),
            typeof(SecureDomainHandle), typeof(CapabilityId) }, operation.GetParameters().Select(x => x.ParameterType));
        Assert.False(typeof(ISecureExecutionProvider).IsAssignableFrom(typeof(HostPlatformAuthorityProvider)));
    }

    [Fact]
    public void SecureGuestOverlayUsesExistingGuestLineageAndClosesFirst()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read);
        Assert.True(guest.IsSuccess, guest.Message);
        var execution = Bind(s).Value;
        var overlay = s.Kernel.BindSecureGuestRegion(s.Owner, execution, guest.Value!.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(overlay.IsSuccess, overlay.Message);
        Assert.Equal(s.Provider.LastGuestParent, s.Provider.LastSecureParent);
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay.Value).IsSuccess);
        Assert.Equal(new[] { "secure" }, s.Provider.OverlayCloseOrder);
    }

    [Fact]
    public void StaleGuestMappingMakesZeroSecureOverlayProviderCalls()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read).Value!;
        var stale = guest.Mapping with { Generation = new(2) };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, Bind(s).Value, stale, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls);
    }

    [Fact]
    public void AmbiguousSecureOverlayCloseQuarantinesGuestMappingAndBlocksTeardown()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read).Value!;
        var overlay = s.Kernel.BindSecureGuestRegion(s.Owner, Bind(s).Value, guest.Mapping, PlatformSecureRegionClass.Private).Value;
        s.Provider.SecureRegionClosureFault = true;
        Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.SecureRegionClosureFault = false;
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        var terminalCloseCalls = s.Provider.OverlayCloseOrder.Count(item => item == "secure");
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.True(terminalCloseCalls >= 2);
        Assert.Equal(terminalCloseCalls, s.Provider.OverlayCloseOrder.Count(item => item == "secure"));
    }

    [Fact]
    public async Task SecureVirtualizedType3BackedType2WorkloadPublishesEventBeforeExactClosure()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        var deviceCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;

        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        var endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        var security = new CxlSecurityModelProvider();
        var securityProperties = CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;
        Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, securityProperties).IsSuccess);
        var bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, model,
            new UnsupportedCoherentProvider(), security);
        var memory = new CxlType3MemoryAuthority(s.Kernel, bridge);

        var input = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var output = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        input.Span.Fill(0x5A);
        var inputPlacement = memory.Place(new(process.DomainId, s.Owner.Generation), input.Handle,
            subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;
        var outputPlacement = memory.Place(new(process.DomainId, s.Owner.Generation), output.Handle,
            subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;

        var inputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(input.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var outputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(output.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var inputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            inputCapability, input.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        var outputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            outputCapability, output.Handle, new(16, 16), GuestMemoryAccess.Read | GuestMemoryAccess.Write).Value!;
        var secureExecution = Bind(s).Value;
        var inputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, inputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var outputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, outputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var virtualIo = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), secureExecution).Value;

        var control = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var controlUse = s.Kernel.AcquireRegionUse(s.Owner, control.Handle, RegionUseMode.DevicePrivate,
            new(0, 16)).Value!;
        var fabric = bridge.BindFabric(new(process.DomainId, s.Owner.Generation), controlUse.Handle,
            subject, device, new(endpoint.EndpointId, endpoint.DeviceGeneration, 16,
                CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var candidate = new ComputeProviderCandidate(new("secure-virtual-type2"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication |
            ComputeProviderCapabilities.SecureComputeEvidence | ComputeProviderCapabilities.VirtualizedDomain,
            4096, 1, 1, true, false);
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 16)), new(output.Handle, new(0, 16)),
            ComputePublicationPreference.StagedRequired, RequiresSecureEvidence: true,
            RequiresVirtualizedDomain: true);
        var contextResult = s.Kernel.CreateVirtualComputeContext(s.Owner, s.Virtual.Domain,
            inputGuest.Mapping, outputGuest.Mapping, virtualIo, secureExecution);
        Assert.True(contextResult.IsSuccess, contextResult.Message);
        var context = contextResult.Value;
        var planResult = s.Kernel.PlanVirtualizedCompute(s.Owner, intent, new(true, true, true),
            [candidate], context);
        Assert.True(planResult.IsSuccess, planResult.Message);
        var plan = planResult.Value!;
        var accelerator = new CxlType2ModelAccelerator();
        var manager = new CxlFabricManagerAuthority(s.Kernel, model);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        var policy = new CxlSecurityPolicy(securityProperties, new(1), RequiredForOperation: true);
        var execution = service.SubmitVirtualized(s.Owner, plan, [candidate], context, subject, device,
            endpoint, fabric, 1, securityAuthority: new(bridge, security), securityPolicy: policy).Value!;
        var guestEvent = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;

        Assert.Equal(KernelError.InvalidTransition, s.Kernel.PublishVirtualIoEvent(s.Owner, virtualIo,
            execution.Operation, s.Virtual.EventCapability, guestEvent).Error);
        Assert.Equal(0, s.Provider.EventEffects);
        var completed = service.CompleteVisiblePublishWithGuestEvent(s.Owner, execution, [candidate],
            () => input.Span.CopyTo(output.Span), virtualIo, s.Virtual.EventCapability, guestEvent);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(ExternalOperationState.Released, completed.Value!.State);
        Assert.Equal(input.Span.ToArray(), output.Span.ToArray());
        Assert.True((await s.Kernel.WaitForKernelEventAsync(s.Owner, guestEvent)).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);

        Assert.True(s.Kernel.CloseVirtualIo(virtualIo).IsSuccess);
        Assert.True(s.Kernel.CloseSecureGuestRegion(inputSecure).IsSuccess);
        Assert.True(s.Kernel.CloseSecureGuestRegion(outputSecure).IsSuccess);
        var inputGuestClose = s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain,
            s.Virtual.MemoryCapability, inputGuest.Mapping);
        Assert.True(inputGuestClose.IsSuccess, inputGuestClose.Message);
        var outputGuestClose = s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain,
            s.Virtual.MemoryCapability, outputGuest.Mapping);
        Assert.True(outputGuestClose.IsSuccess, outputGuestClose.Message);
        Assert.True(s.Kernel.CloseSecureExecution(secureExecution).IsSuccess);
        Assert.True(memory.Close(inputPlacement.PlacementId).IsSuccess);
        Assert.True(memory.Close(outputPlacement.PlacementId).IsSuccess);
        Assert.True(bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegionUse(s.Owner, controlUse.Handle).IsSuccess);
        Assert.True(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain,
            s.Secure.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Owner, device).IsSuccess);
        var virtualClose = s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain,
            s.Virtual.ConfigureCapability);
        Assert.True(virtualClose.IsSuccess, virtualClose.Message);
        var finalTeardown = s.Kernel.TerminateProcess(s.Owner);
        Assert.True(finalTeardown.IsSuccess, finalTeardown.Message);
    }

    [Fact]
    public void FabricReconfigurationDuringSecureVirtualGuestWorkClosesEffectBeforeReclaim()
    {
        var c = CreateComposedWorkload();

        var draining = c.Manager.BeginReconfiguration(c.Fabric);

        Assert.True(draining.IsSuccess, draining.Message);
        Assert.Equal(ExternalOperationState.Released,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            c.Accelerator.ObserveCompletion(c.Execution.Submission).Status);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        Assert.True(c.Base.Kernel.Regions.Validate(c.Input.Handle, c.Owner).IsSuccess);
        var replacement = c.Manager.CompleteReconfiguration(c.Fabric.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        CloseComposedWorkload(c, replacement.Value!.Binding);
    }

    [Fact]
    public void Type3HotRemoveWithLiveSecureGuestMappingsBlocksPublicationUntilExactClosure()
    {
        var c = CreateComposedWorkload();
        var published = false;

        Assert.True(c.Model.HotRemove(c.Endpoint.EndpointId).IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, c.Memory.Refresh(c.InputPlacement.PlacementId).Error);
        Assert.Equal(KernelError.PlatformUnavailable, c.Memory.Refresh(c.OutputPlacement.PlacementId).Error);
        var completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => published = true);

        Assert.False(completed.IsSuccess);
        Assert.False(published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            c.Memory.Query(c.InputPlacement.PlacementId).Value!.State);
        Assert.Equal(ExternalOperationState.Released,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.True(c.Base.Kernel.Regions.Validate(c.Input.Handle, c.Owner).IsSuccess);
        var currentFabric = c.Bridge.QueryFabric(c.Fabric.BindingId);
        Assert.True(currentFabric.IsSuccess, currentFabric.Message);
        CloseComposedWorkload(c, currentFabric.Value!);
    }

    [Fact]
    public void SecurityResetAfterDeviceCompleteBlocksVisibilityPublicationAndGuestEvent()
    {
        var c = CreateComposedWorkload(security => new CompletionHookAccelerator(
            new CxlType2ModelAccelerator(),
            () => Assert.True(security.ResetSecuritySession(new("secure-virtual-type2")).IsSuccess)));
        var published = false;
        var guestEvent = c.Base.Kernel.CreateKernelEventEndpoint(c.Base.Owner).Value!;

        var completed = c.Service.CompleteVisiblePublishWithGuestEvent(c.Base.Owner, c.Execution,
            [c.Candidate], () => published = true, c.VirtualIo,
            c.Base.Virtual.EventCapability, guestEvent);

        Assert.False(completed.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, completed.Error);
        Assert.False(published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, operation.State);
        Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
        CloseComposedWorkload(c, c.Fabric);
    }

    private static ComposedWorkload CreateComposedWorkload(
        Func<CxlSecurityModelProvider, ICxlType2AcceleratorProvider>? acceleratorFactory = null)
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var owner = new RegionOwner(process.DomainId, s.Owner.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        var deviceCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write |
            CapabilityRights.Configure).Value!;
        var device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        var endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        var security = new CxlSecurityModelProvider();
        var securityProperties = CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;
        Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, securityProperties).IsSuccess);
        var bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, model,
            new UnsupportedCoherentProvider(), security);
        var memory = new CxlType3MemoryAuthority(s.Kernel, bridge);
        var input = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var output = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var inputPlacement = memory.Place(owner, input.Handle, subject, device, endpoint,
            CxlMemoryPersistence.Volatile).Value!;
        var outputPlacement = memory.Place(owner, output.Handle, subject, device, endpoint,
            CxlMemoryPersistence.Volatile).Value!;
        var inputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(input.Handle.RegionId), CapabilityRights.Map |
            CapabilityRights.Read).Value!.CapabilityId;
        var outputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(output.Handle.RegionId), CapabilityRights.Map |
            CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var inputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            inputCapability, input.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        var outputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            outputCapability, output.Handle, new(16, 16), GuestMemoryAccess.Read |
            GuestMemoryAccess.Write).Value!;
        var secureExecution = Bind(s).Value;
        var inputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, inputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var outputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, outputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var virtualIo = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), secureExecution).Value;
        var control = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var controlUse = s.Kernel.AcquireRegionUse(s.Owner, control.Handle, RegionUseMode.DevicePrivate,
            new(0, 16)).Value!;
        var fabric = bridge.BindFabric(owner, controlUse.Handle, subject, device,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile,
                CxlMemorySharing.Exclusive)).Value!;
        var candidate = new ComputeProviderCandidate(new("secure-virtual-type2"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication |
            ComputeProviderCapabilities.SecureComputeEvidence | ComputeProviderCapabilities.VirtualizedDomain,
            4096, 1, 1, true, false);
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 16)), new(output.Handle, new(0, 16)),
            ComputePublicationPreference.StagedRequired, RequiresSecureEvidence: true,
            RequiresVirtualizedDomain: true);
        var context = s.Kernel.CreateVirtualComputeContext(s.Owner, s.Virtual.Domain,
            inputGuest.Mapping, outputGuest.Mapping, virtualIo, secureExecution).Value;
        var plan = s.Kernel.PlanVirtualizedCompute(s.Owner, intent, new(true, true, true),
            [candidate], context).Value!;
        var accelerator = acceleratorFactory?.Invoke(security) ?? new CxlType2ModelAccelerator();
        var manager = new CxlFabricManagerAuthority(s.Kernel, model);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        var policy = new CxlSecurityPolicy(securityProperties, new(1), RequiredForOperation: true);
        var execution = service.SubmitVirtualized(s.Owner, plan, [candidate], context, subject, device,
            endpoint, fabric, 1, securityAuthority: new(bridge, security), securityPolicy: policy).Value!;
        return new(s, owner, subject, device, model, endpoint, security, bridge, memory, input, output,
            inputPlacement, outputPlacement, inputGuest, outputGuest, inputSecure, outputSecure,
            secureExecution, virtualIo, controlUse, fabric, candidate, context, accelerator, manager,
            service, execution);
    }

    private static void CloseComposedWorkload(ComposedWorkload c, CxlFabricBinding currentFabric)
    {
        Assert.True(c.Base.Kernel.CloseVirtualIo(c.VirtualIo).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureGuestRegion(c.InputSecure).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureGuestRegion(c.OutputSecure).IsSuccess);
        Assert.True(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.InputGuest.Mapping).IsSuccess);
        Assert.True(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
        Assert.True(c.Memory.Close(c.InputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Bridge.ReleaseFabric(currentFabric).IsSuccess);
        Assert.True(c.Base.Kernel.ReleaseRegionUse(c.Base.Owner, c.ControlUse.Handle).IsSuccess);
        Assert.True(c.Base.Kernel.DestroySecureDomain(c.Base.Owner, c.Base.Secure.Domain,
            c.Base.Secure.ConfigureCapability).IsSuccess);
        Assert.True(c.Base.Kernel.RevokePlatformDevice(c.Base.Owner, c.Device).IsSuccess);
        Assert.True(c.Base.Kernel.DestroyVirtualDomain(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.ConfigureCapability).IsSuccess);
        var terminated = c.Base.Kernel.TerminateProcess(c.Base.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
    }

    private sealed record ComposedWorkload(Scenario Base, RegionOwner Owner,
        PlatformDomainIdentity Subject, PlatformDeviceLease Device, CxlType3ModelProvider Model,
        CxlEndpointSnapshot Endpoint, CxlSecurityModelProvider Security, CxlAuthorityBridge Bridge,
        CxlType3MemoryAuthority Memory, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output,
        CxlMemoryPlacementSnapshot InputPlacement, CxlMemoryPlacementSnapshot OutputPlacement,
        GuestRegionMapping InputGuest, GuestRegionMapping OutputGuest,
        SecureGuestRegionBinding InputSecure, SecureGuestRegionBinding OutputSecure,
        SecureExecutionBinding SecureExecution, VirtualIoBinding VirtualIo,
        RegionUseDescriptor ControlUse, CxlFabricBinding Fabric, ComputeProviderCandidate Candidate,
        VirtualComputeContext Context, ICxlType2AcceleratorProvider Accelerator,
        CxlFabricManagerAuthority Manager, CxlType2AcceleratorService Service,
        CxlType2Execution Execution);

    private sealed class CompletionHookAccelerator(ICxlType2AcceleratorProvider inner,
        Action afterCompletion) : ICxlType2AcceleratorProvider
    {
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request) =>
            inner.Submit(request);
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(
            CxlAcceleratorSubmission submission)
        {
            var result = inner.ObserveCompletion(submission);
            if (result.IsSuccess) afterCompletion();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(
            CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement) =>
            inner.AcquireVisibility(submission, requirement);
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission) => inner.Cancel(submission);
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission) => inner.Release(submission);
    }

    private static KernelResult<SecureExecutionBinding> Bind(Scenario s) => s.Kernel.BindSecureExecution(s.Owner,
        s.Virtual.Domain, s.Virtual.ConfigureCapability, s.Secure.Domain, s.Secure.ConfigureCapability);

    private static Scenario Create()
    {
        var provider = new FakeProvider(); var kernel = new RuntimeKernel(provider);
        provider.CheckLocks = () => {
            foreach (var name in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!));
        };
        var (process, owner) = TestFixtures.Create(kernel, 980, 981);
        var vc = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner, vc, new(1, 4096));
        Assert.True(vm.IsSuccess, vm.Message);
        Assert.True(kernel.ConfigureVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        // Reuse the actual VM parent; do not create a parallel domain authority.
        var parent = provider.Parent;
        var localParent = new PlatformDomainBinding(new(1), new(1), parent.Subject);
        var sc = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var secure = kernel.CreateSecureDomain(owner, sc, localParent, new([SecureProperty.PrivateMemory], 4096));
        Assert.True(secure.IsSuccess, secure.Message);
        return new(kernel, provider, owner, vm.Value, secure.Value!, localParent);
    }

    private sealed record Scenario(RuntimeKernel Kernel, FakeProvider Provider, ProcessHandle Owner,
        VirtualDomainAuthoritySet Virtual, SecureDomainAuthoritySet Secure, PlatformDomainBinding Parent);

    // Deterministic test contract only. This is not a production capability claim.
    private sealed class FakeProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformGuestMemoryProvider, IPlatformSecureComputeProvider,
        IPlatformVirtualEventProvider, IPlatformVirtualIoProvider, IPlatformDeviceLeaseProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider,
        ISecureExecutionProvider
    {
        private readonly HostPlatformAuthorityProvider _host = new();
        public PlatformProviderDescriptor Descriptor => _host.Descriptor;
        public PlatformProviderDomainLease Parent;
        public uint SecureExecutionContractVersion { get; set; } = 1;
        public bool ProductionSecureExecution { get; set; } = true;
        public int AdmissionFault = -1, RevalidationFault = -1, ClosureFault = -1;
        public int BindCalls, CloseCalls, RevalidateCalls, SecureCloses, ChildCloses, ChildEffects, SecureRegionBindCalls;
        public bool Contained, ReplayCorrelation, SecureRegionClosureFault;
        public Action? DuringBind, DuringEvent, DuringChildEffect, CheckLocks;
        public int EventEffects;
        public SecureExecutionRequest LastRequest, LastCloseRequest;
        public List<string> TerminalOrder = [];
        public List<string> OverlayCloseOrder = [];
        public PlatformProviderRegionMappingLease LastGuestParent, LastSecureParent;
        private ulong _nextGuest = 1;
        public PlatformFeatureManifest QueryFeatures() => new(_host.QueryFeatures().Features.Concat(new[] {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildEventDelivery, 1, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.BoundedVirtualIo, 1, PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.SecureDomains, 1, PlatformFeatureAvailability.ProductionSecure) }).ToArray());
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        { var result = _host.BindDomain(subject); if (result.IsSuccess) Parent = result.Value; return result; }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => _host.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease lease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            var result = _host.MapOwnedRegion(lease, region, access);
            if (result.IsSuccess) LastGuestParent = result.Value;
            return result;
        }
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => _host.RevokeRegionMapping(mapping, policy);
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            var revoked = _host.RevokeRegionMapping(mapping, policy);
            return revoked.IsSuccess
                ? PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId,
                    mapping.Generation, new(new(_nextGuest++), new(1), mapping.DomainLease)))
                : PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(revoked.Status,
                    revoked.Message ?? "mapping revocation failed");
        }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) =>
            PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId, operation.Generation,
                operation.DomainLease, PlatformCompletionState.Closed));
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domain,
            PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), domain, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(PlatformProviderDomainLease parent, PlatformChildDomainIntent intent) => PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(new(new(1), new(1), parent, intent));
        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease, PlatformChildDomainTransition transition) { if (transition != PlatformChildDomainTransition.BeginDrain) { ChildEffects++; DuringChildEffect?.Invoke(); } return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventEffects++; DuringEvent?.Invoke();
            var child = request.ChildLease;
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(new(child.LeaseId, child.Generation,
                child.ParentDomainLease.LeaseId, child.ParentDomainLease.Generation, (ulong)EventEffects, request.EventClass, request.SourceResourceId));
        }
        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(PlatformProviderChildDomainLease lease)
        { ChildCloses++; TerminalOrder.Add("child"); return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation, lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, PlatformChildDomainClosureDisposition.Closed)); }
        public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(PlatformVirtualIoRequest request) =>
            PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(new(new(_nextGuest++), new(1),
                request.ChildLease, request.ParentDeviceLease, request.Profile));
        public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(PlatformProviderVirtualIoLease lease) =>
            PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(PlatformGuestRegionMappingRequest request) =>
            PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(new(new(_nextGuest++), new(1), request.ChildLease, request.ParentMapping.Lease, request.GuestRange, request.Access));
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(PlatformProviderGuestRegionMappingLease lease)
        {
            OverlayCloseOrder.Add("guest");
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        }
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease> CreateSecureDomain(PlatformSecureDomainRequest request) => PlatformAuthorityResult<PlatformProviderSecureDomainLease>.Ok(new(new(1), new(1), request.Parent, request.Profile.RequiredProperties.ToArray()));
        public PlatformAuthorityResult<PlatformProviderSecureRegionBinding> BindSecureRegion(PlatformProviderSecureDomainLease domain, PlatformProviderRegionMappingLease mapping, PlatformSecureRegionClass regionClass)
        { SecureRegionBindCalls++; LastSecureParent = mapping; return PlatformAuthorityResult<PlatformProviderSecureRegionBinding>.Ok(new(new(1), new(1), domain, mapping, regionClass)); }
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> UnbindSecureRegion(PlatformProviderSecureRegionBinding binding)
        {
            OverlayCloseOrder.Add("secure");
            return SecureRegionClosureFault
                ? PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Fail(PlatformAuthorityStatus.Unavailable, "secure overlay close outcome is ambiguous")
                : PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Ok(new(binding, true));
        }
        public PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> TransitionSecureDomain(PlatformProviderSecureDomainLease domain, PlatformSecureDomainTransition transition) => PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>.Ok(new(domain, transition, true));
        public PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> RevokeSecureDomain(PlatformProviderSecureDomainLease domain)
        { SecureCloses++; TerminalOrder.Add("secure"); return PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>.Ok(new(domain, true)); }
        public PlatformAuthorityResult<SecureExecutionReceipt> BindSecureExecution(SecureExecutionRequest request)
        {
            CheckLocks?.Invoke(); BindCalls++; LastRequest = request; DuringBind?.Invoke();
            if (AdmissionFault == 5) return PlatformAuthorityResult<SecureExecutionReceipt>.Fail(PlatformAuthorityStatus.Unavailable, "lost response after materialization");
            if (AdmissionFault == 6) throw new OperationCanceledException("lost admission response");
            var receipt = new SecureExecutionReceipt(request, ReplayCorrelation ? 1 : request.Binding.Id, 1, 1, 1, 1, true);
            receipt = AdmissionFault switch {
                0 => receipt with { Request = request with { Binding = new(999, 1) } },
                1 => receipt with { PolicyGeneration = 0 }, 2 => receipt with { ProtectionGeneration = 0 },
                3 => receipt with { ProductionSecure = false }, 4 => receipt with { ContractVersion = 2 }, _ => receipt };
            return PlatformAuthorityResult<SecureExecutionReceipt>.Ok(receipt);
        }
        public PlatformAuthorityResult<SecureExecutionReceipt> RevalidateSecureExecution(SecureExecutionReceipt receipt)
        {
            CheckLocks?.Invoke(); RevalidateCalls++;
            if (RevalidationFault == 4) return PlatformAuthorityResult<SecureExecutionReceipt>.Fail(PlatformAuthorityStatus.Revoked, "secure lease missing");
            return PlatformAuthorityResult<SecureExecutionReceipt>.Ok(RevalidationFault switch {
                0 => receipt with { PolicyGeneration = 2 }, 1 => receipt with { ProtectionGeneration = 2 },
                2 => receipt with { ProviderGeneration = 2 }, 3 => receipt with { Request = receipt.Request with {
                    Context = receipt.Request.Context with { ChildLease = receipt.Request.Context.ChildLease with { Generation = new(2) } } } }, _ => receipt });
        }
        public PlatformAuthorityResult<SecureExecutionClosure> CloseSecureExecution(SecureExecutionRequest request, SecureExecutionReceipt? receipt)
        {
            CheckLocks?.Invoke(); CloseCalls++; LastCloseRequest = request;
            if (ClosureFault == 0) return PlatformAuthorityResult<SecureExecutionClosure>.Fail(PlatformAuthorityStatus.Unavailable, "disconnect");
            if (ClosureFault == 1) return PlatformAuthorityResult<SecureExecutionClosure>.Fail(PlatformAuthorityStatus.Revoked, "not found");
            if (ClosureFault == 2) throw new OperationCanceledException();
            if (ClosureFault == 3) return PlatformAuthorityResult<SecureExecutionClosure>.Ok(new(request, receipt, false, false));
            if (ClosureFault == 4) return PlatformAuthorityResult<SecureExecutionClosure>.Ok(new(request with { Binding = new(999, 1) }, receipt, true, false));
            if (ClosureFault == 5) throw new TimeoutException();
            if (ClosureFault == 6) return PlatformAuthorityResult<SecureExecutionClosure>.Fail((PlatformAuthorityStatus)999, "unknown outcome");
            TerminalOrder.Add("execution");
            return PlatformAuthorityResult<SecureExecutionClosure>.Ok(new(request, receipt, !Contained, Contained));
        }
    }

    private sealed class UnsupportedCoherentProvider : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding binding,
            RegionUseDescriptor use) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) =>
            Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding) =>
            PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() =>
            PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
}
