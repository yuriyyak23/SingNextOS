using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformAuthorityBridgeChildDomainTests
{
    [Fact]
    public void RuntimeKernelUsesChildLedgerWithoutPublishingProviderLeaseAuthority()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 4, 40).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(40), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;

        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        Assert.True(kernel.StartVirtualDomain(owner, authority.Domain, authority.ExecuteCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        Assert.True(kernel.InjectVirtualEvent(owner, authority.Domain, authority.EventCapability, endpoint).IsSuccess);
        Assert.True(kernel.ParkVirtualDomain(owner, authority.Domain, authority.ExecuteCapability).IsSuccess);
        Assert.True(kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        Assert.Equal(3, provider.TransitionCalls);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(KernelError.VirtualDomainNotFound, kernel.QueryVirtualDomain(owner, authority.Domain).Error);
    }

    [Fact]
    public void RuntimeKernelPinsVirtualAuthorityAfterAmbiguousChildClose()
    {
        var provider = new ChildProvider { MalformedClose = true };
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 5, 50).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(50), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).Error);
        Assert.Equal(VirtualDomainState.Quarantined, kernel.QueryVirtualDomain(owner, authority.Domain).Value);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
    }

    [Fact]
    public void ChildLedgerKeepsLocalGenerationDistinctAndRejectsWrongOwnerBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;

        var stale = child with { Generation = new(child.Generation.Value + 1) };
        var wrongOwner = child with { Owner = new ProcessHandle(new ProcessId(99), 1) };

        Assert.NotEqual(provider.LastChildLease.Generation.Value, child.Generation.Value);
        Assert.Equal(KernelError.StaleGeneration,
            bridge.TransitionChildDomain(stale, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(KernelError.WrongPlatformDomain,
            bridge.TransitionChildDomain(wrongOwner, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(0, provider.TransitionCalls);
    }

    [Fact]
    public void EventAndTrapEvidenceAreSequencedAndNeverCloseChildAuthority()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);

        Assert.True(bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").IsSuccess);
        Assert.True(bridge.ObserveChildTrap(child).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
    }

    [Fact]
    public void AmbiguousChildCloseQuarantinesAndDoesNotPermitRetryOrReclaim()
    {
        var provider = new ChildProvider { MalformedClose = true };
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);

        KernelResult close = bridge.CloseChildDomain(child);
        KernelResult retry = bridge.CloseChildDomain(child);

        Assert.Equal(KernelError.PlatformFaulted, close.Error);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(1, provider.CloseCalls);
    }

    [Fact]
    public void ChildClosureRequiresTerminalGuestAndIoReceipts()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        PlatformRegionMapping parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        PlatformDeviceLease device = bridge.BindDevice(parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        PlatformVirtualIoBinding io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);

        Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
        Assert.True(bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
    }

    [Fact]
    public void StaleRuntimeVmGenerationMakesZeroChildProviderCalls()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 6, 60).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(60), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var stale = authority.Domain with { Generation = new(authority.Domain.Generation.Value + 1) };

        KernelResult start = kernel.StartVirtualDomain(owner, stale, authority.ExecuteCapability);

        Assert.Equal(KernelError.StaleGeneration, start.Error);
        Assert.Equal(0, provider.TransitionCalls);
    }

    [Fact]
    public void RuntimeKernelClosesChildAndParentGuestMappingBeforeLocalReclaim()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 7, 70);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        GuestRegionMapping mapping = kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Write).Value!;

        Assert.True(kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        Assert.True(kernel.ReleaseRegion(owner, region).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.Equal(1, provider.RegionRevocationBeginCalls);
        Assert.Equal(1, provider.CompletionCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ProcessTeardownUsesDeterministicChildGuestParentClosureOrder()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 8, 80);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability, regionCapability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);

        provider.CallLog.Clear();
        KernelResult terminate = kernel.TerminateProcess(owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(new[] { "guest-unmap", "parent-map-begin", "parent-map-observe", "child-drain", "child-close", "parent-close" },
            provider.CallLog);
    }

    [Fact]
    public void BackendResetFaultPinsChildAndGuestLedgersBeforeAnyFurtherProviderCall()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(9, 90);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(9), new RegionGeneration(1)),
            new(new DomainId(9), owner.ProcessGeneration), 4096);
        PlatformRegionMapping parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(99), region,
            PlatformMemoryAccess.Read).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        int transitions = provider.TransitionCalls;
        int unmaps = provider.GuestUnmapCalls;

        var reset = bridge.ObserveBackendReset();
        KernelResult transition = bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start);
        KernelResult unmap = bridge.UnmapChildGuestRegion(guest);

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(1, reset.Value!.InvalidatedVirtualDomains);
        Assert.Equal(KernelError.PlatformFaulted, transition.Error);
        Assert.Equal(KernelError.PlatformFaulted, unmap.Error);
        Assert.Equal(transitions, provider.TransitionCalls);
        Assert.Equal(unmaps, provider.GuestUnmapCalls);
    }

    [Fact]
    public async Task VirtualIoGuestEventRequiresExactPublishedOperation()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        var parent = new PlatformDomainBinding(new(1), new(1),
            new(process.DomainId, owner));
        var deviceCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:virtual-io-event", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = kernel.BindPlatformDevice(owner, parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        var virtualIoResult = kernel.BindVirtualIo(owner, authority.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096));
        Assert.True(virtualIoResult.IsSuccess, virtualIoResult.Message);
        var virtualIo = virtualIoResult.Value;
        var currentIo = kernel.RevalidateVirtualIo(owner, virtualIo);
        Assert.True(currentIo.IsSuccess, currentIo.Message);

        var input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1, 1, 1);
        var prepared = kernel.PrepareExternalOperation(owner,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
             new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        Assert.True(kernel.AdmitExternalOperation(owner, prepared.Operation, dependencies).IsSuccess);
        var submitted = kernel.RecordExternalOperationSubmission(owner, prepared.Operation, dependencies).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;

        Assert.Equal(KernelError.InvalidTransition,
            kernel.PublishVirtualIoEvent(owner, virtualIo, prepared.Operation,
                authority.EventCapability, endpoint).Error);
        Assert.Equal(0, provider.EventCalls);

        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(submitted, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(submitted, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(kernel.PublishExternalOperation(owner, prepared.Operation, dependencies,
            new(ExternalPublicationPolicy.Staged), () => input.Span.CopyTo(output.Span)).IsSuccess);
        Assert.True(kernel.PublishVirtualIoEvent(owner, virtualIo, prepared.Operation,
            authority.EventCapability, endpoint).IsSuccess);

        var delivered = await kernel.WaitForKernelEventAsync(owner, endpoint);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(1, provider.EventCalls);
        Assert.Equal(input.Span.ToArray(), output.Span.ToArray());
    }

    private static PlatformDomainIdentity Subject(ulong domain, ulong process) =>
        new(new DomainId(domain), new(new ProcessId(process), 1));

    private static PlatformChildDomainIntent Intent() => new(
        new(1, 4096),
        new(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events |
                PlatformChildAuthorityClass.Traps | PlatformChildAuthorityClass.Io,
            PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events |
                PlatformChildAuthorityClass.Traps | PlatformChildAuthorityClass.Io));

    private sealed class ChildProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformGuestMemoryProvider, IPlatformVirtualEventProvider,
        IPlatformVirtualTrapProvider, IPlatformVirtualIoProvider, IPlatformDeviceLeaseProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider
    {
        private ulong next = 20;
        public bool MalformedClose { get; init; }
        public int TransitionCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int GuestUnmapCalls { get; private set; }
        public int RegionRevocationBeginCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public int RevokeDomainCalls { get; private set; }
        public int EventCalls { get; private set; }
        public List<string> CallLog { get; } = [];
        public PlatformProviderChildDomainLease LastChildLease { get; private set; }
        public PlatformProviderDescriptor Descriptor { get; } = new(new("test.child"), 2,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        public PlatformFeatureManifest QueryFeatures() => new(Enum.GetValues<PlatformFeatureFamily>()
            .Where(f => f is PlatformFeatureFamily.NeutralDomains or PlatformFeatureFamily.OwnedRegionMapping or
                PlatformFeatureFamily.ChildDomainLifecycle or
                PlatformFeatureFamily.ChildGuestMemory or PlatformFeatureFamily.ChildEventDelivery or
                PlatformFeatureFamily.ChildTrapDelivery or PlatformFeatureFamily.BoundedVirtualIo)
            .Select(f => new PlatformFeatureDescriptor(f,
                f is PlatformFeatureFamily.NeutralDomains or PlatformFeatureFamily.ChildDomainLifecycle or
                    PlatformFeatureFamily.OwnedRegionMapping ? 2u : 1u,
                f == PlatformFeatureFamily.BoundedVirtualIo
                    ? PlatformFeatureAvailability.Executable
                    : PlatformFeatureAvailability.RuntimeAdmission)));
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(next++), new(7), subject));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            RevokeDomainCalls++;
            CallLog.Add("parent-close");
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(new(new(next++), new(11), domainLease, region, access));
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            RegionRevocationBeginCalls++;
            CallLog.Add("parent-map-begin");
            var operation = new PlatformOperationIdentity(new(next++), new(1), mapping.DomainLease);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId, mapping.Generation, operation));
        }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation)
        {
            CompletionCalls++;
            CallLog.Add("parent-map-observe");
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId, operation.Generation,
                operation.DomainLease, PlatformCompletionState.Closed));
        }
        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
            PlatformProviderDomainLease parentLease, PlatformChildDomainIntent intent)
        {
            LastChildLease = new(new(next++), new(23), parentLease, intent);
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(LastChildLease);
        }
        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease,
            PlatformChildDomainTransition transition)
        {
            TransitionCalls++;
            if (transition == PlatformChildDomainTransition.BeginDrain) CallLog.Add("child-drain");
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(PlatformProviderChildDomainLease lease)
        {
            CloseCalls++;
            CallLog.Add("child-close");
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId,
                MalformedClose ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                PlatformChildDomainClosureDisposition.Closed));
        }
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(PlatformGuestRegionMappingRequest request) =>
            PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(new(new(next++), new(31), request.ChildLease,
                request.ParentMapping.Lease, request.GuestRange, request.Access));
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(PlatformProviderGuestRegionMappingLease lease)
        {
            GuestUnmapCalls++;
            CallLog.Add("guest-unmap");
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventCalls++;
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(new(request.ChildLease.LeaseId,
                request.ChildLease.Generation, request.ChildLease.ParentDomainLease.LeaseId,
                request.ChildLease.ParentDomainLease.Generation, (ulong)EventCalls, request.EventClass, request.SourceResourceId));
        }
        public PlatformAuthorityResult<PlatformVirtualTrapEvidence> ObserveVirtualTrap(PlatformProviderChildDomainLease lease) =>
            PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, 1, PlatformVirtualTrapKind.Timer));
        public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(PlatformVirtualIoRequest request) =>
            PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(new(new(next++), new(41), request.ChildLease,
                request.ParentDeviceLease, request.Profile));
        public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(PlatformProviderVirtualIoLease lease) =>
            PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(next++), new(37), domainLease, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
    }
}
