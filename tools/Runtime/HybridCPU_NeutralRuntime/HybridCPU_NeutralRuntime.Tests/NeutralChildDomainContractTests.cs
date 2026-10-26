using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class NeutralChildDomainContractTests
{
    [Fact]
    public void ExecutableArtifactAndRetiredWorkRequireExactChildMappingAndEpochs()
    {
        NeutralDomainBindingLease parent = Parent();
        NeutralChildDomainLease child = Child(parent, Intent(All, All));
        NeutralGuestMappingLease mapping = new NeutralGuestMappingLease(child,
            Mapping(parent, NeutralMemoryAccess.Read), new(0, 4096),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute, new(50), new(2));
        NeutralExecutableArtifactRequest request = new NeutralExecutableArtifactRequest(child, mapping, new byte[128], 1000);
        Assert.True(NeutralChildExecutionContract.ValidateAdmission(request).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.WrongParent,
            NeutralChildExecutionContract.ValidateAdmission(request with
            { GuestMapping = mapping with { ChildLease = Child(Parent(9, 1), Intent(All, All)) } }).Status);

        NeutralExecutableArtifactReceipt artifact = new NeutralExecutableArtifactReceipt(new(60), new(3), child.Handle, child.Epoch,
            mapping.Handle, mapping.Epoch, parent.Handle, parent.Epoch, new string('a', 64), 1000);
        Assert.True(NeutralChildExecutionContract.ValidateAdmissionReceipt(request, artifact).IsSuccess);
        NeutralChildExecutionStartRequest start = new NeutralChildExecutionStartRequest(child, artifact, new(70), new(5));
        Assert.True(NeutralChildExecutionContract.ValidateStart(start).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.WrongParent,
            NeutralChildExecutionContract.ValidateStart(
                start with { ChildLease = Child(Parent(9, 1), Intent(All, All)) }).Status);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            NeutralChildExecutionContract.ValidateStart(
                start with { ChildLease = child with { Epoch = new(child.Epoch.Value + 1) } }).Status);
        NeutralChildExecutionReceipt execution = new NeutralChildExecutionReceipt(artifact.ArtifactHandle, artifact.ArtifactEpoch,
            child.Handle, child.Epoch, mapping.Handle, mapping.Epoch, parent.Handle, parent.Epoch,
            artifact.ContentDigest, start.OperationId, start.OperationGeneration, new(4), 12, 9, 256, true);
        Assert.True(NeutralChildExecutionContract.ValidateExecutionReceipt(start,
            execution with { RetiredWorkUnits = artifact.MaximumExecutionSteps }).IsSuccess);
        Assert.False(NeutralChildExecutionContract.ValidateExecutionReceipt(start,
            execution with { RetiredWorkUnits = artifact.MaximumExecutionSteps + 1 }).IsSuccess);
        Assert.True(NeutralChildExecutionContract.ValidateExecutionReceipt(start, execution).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            NeutralChildExecutionContract.ValidateExecutionReceipt(start,
                execution with { MappingEpoch = new(mapping.Epoch.Value + 1) }).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            NeutralChildExecutionContract.ValidateExecutionReceipt(start,
                execution with { RetiredSequence = 0 }).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            NeutralChildExecutionContract.ValidateExecutionReceipt(start,
                execution with { StartOperationGeneration = new(start.OperationGeneration.Value + 1) }).Status);
    }

    [Fact]
    public void ChildIdentityParentAndAuthorityAreFailClosed()
    {
        NeutralDomainBindingLease parent = Parent();
        NeutralChildDomainIntent intent = Intent(All, All);
        NeutralChildDomainLease child = Child(parent, intent);
        Assert.True(NeutralChildDomainContract.ValidateLease(parent, intent, child).IsSuccess);

        Assert.Equal(NeutralVirtualizationStatus.Denied,
            NeutralChildDomainContract.ValidateCreate(parent, Intent(All, All | (NeutralChildAuthorityClass)(1 << 20))).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied,
            NeutralChildDomainContract.ValidateCreate(parent, Intent(All, NeutralChildAuthorityClass.None)).Status);
        Assert.Equal(NeutralVirtualizationStatus.Faulted,
            NeutralChildDomainContract.ValidateLease(parent, intent, child with { Handle = default }).Status);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            NeutralChildDomainContract.ValidateLease(parent, intent, child with { Epoch = default }).Status);
        Assert.Equal(NeutralVirtualizationStatus.WrongParent,
            NeutralChildDomainContract.ValidateLease(parent, intent, child with { ParentLease = Parent(9, 1) }).Status);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            NeutralChildDomainContract.ValidateLease(parent, intent, child with { ParentLease = Parent(1, 2) }).Status);
    }

    [Fact]
    public void GuestMappingCannotEscapeExactParentAndStaleUnmapDoesNotClose()
    {
        NeutralDomainBindingLease parent = Parent();
        NeutralChildDomainLease child = Child(parent, Intent(All, All));
        NeutralOwnedRegionMappingLease mapping = Mapping(parent, NeutralMemoryAccess.Read | NeutralMemoryAccess.Write);
        NeutralGuestMappingRequest request = new NeutralGuestMappingRequest(child, mapping, new(128, 512), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Write);
        Assert.True(NeutralGuestMemoryContract.ValidateMap(request, NeutralChildDomainState.Created).IsSuccess);

        Assert.Equal(NeutralVirtualizationStatus.Denied,
            NeutralGuestMemoryContract.ValidateMap(request with { GuestRange = new(0, mapping.Slice.Length + 1) }, NeutralChildDomainState.Created).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied,
            NeutralGuestMemoryContract.ValidateMap(request with { ParentMapping = Mapping(parent, NeutralMemoryAccess.Read) }, NeutralChildDomainState.Created).Status);
        Assert.Equal(NeutralVirtualizationStatus.WrongOwner,
            NeutralGuestMemoryContract.ValidateMap(request with { ParentMapping = Mapping(Parent(9, 1), NeutralMemoryAccess.Read | NeutralMemoryAccess.Write) }, NeutralChildDomainState.Created).Status);

        NeutralGuestMappingLease lease = new NeutralGuestMappingLease(child, mapping, request.GuestRange, request.Access, new(31), new(4));
        NeutralGuestMappingCloseReceipt stale = new NeutralGuestMappingCloseReceipt(lease.Handle, new(5), child.Handle, child.Epoch, parent.Handle, parent.Epoch, true);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            NeutralGuestMemoryContract.ValidateClose(lease, Success(stale)).Status);
        NeutralGuestMappingCloseReceipt ambiguous = new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, child.Handle, child.Epoch, parent.Handle, parent.Epoch, false);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            NeutralGuestMemoryContract.ValidateClose(lease, Success(ambiguous)).Status);
    }

    [Fact]
    public void EventsAndIoRejectDrainAndReceiptsCarryNoReusableLease()
    {
        NeutralDomainBindingLease parent = Parent();
        NeutralChildDomainLease child = Child(parent, Intent(All, All));
        NeutralVirtualEventRequest eventRequest = new NeutralVirtualEventRequest(child, NeutralVirtualEventClass.ExternalSignal, "event:test");
        Assert.Equal(NeutralVirtualizationStatus.Denied,
            NeutralVirtualEventContract.ValidateRequest(eventRequest, NeutralChildDomainState.Draining).Status);
        NeutralVirtualEventReceipt receipt = new NeutralVirtualEventReceipt(child.Handle, child.Epoch, parent.Handle, parent.Epoch, 1, eventRequest.EventClass, eventRequest.SourceResourceId);
        Assert.True(NeutralVirtualEventContract.ValidateReceipt(eventRequest, receipt).IsSuccess);

        NeutralDeviceLease device = new NeutralDeviceLease(parent, new("device:test"), NeutralDeviceRights.Read, new(7), new(3));
        NeutralVirtualIoRequest io = new NeutralVirtualIoRequest(child, device, new(NeutralDeviceRights.Read, 4096));
        Assert.True(NeutralVirtualIoContract.ValidateBind(io, NeutralChildDomainState.Created).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Denied,
            NeutralVirtualIoContract.ValidateBind(io with { Profile = new(NeutralDeviceRights.Read | NeutralDeviceRights.Configure, 4096) }, NeutralChildDomainState.Created).Status);
        Assert.Equal(NeutralVirtualizationStatus.WrongOwner,
            NeutralVirtualIoContract.ValidateBind(io with { ParentDeviceLease = device with { DomainLease = Parent(8, 1) } }, NeutralChildDomainState.Created).Status);

        NeutralVirtualIoLease ioLease = new NeutralVirtualIoLease(child, device, io.Profile, new(41), new(2));
        Assert.True(NeutralVirtualIoContract.ValidateLease(io, ioLease).IsSuccess);
        NeutralVirtualIoCloseReceipt ioClosed = new NeutralVirtualIoCloseReceipt(ioLease.Handle, ioLease.Epoch, child.Handle, child.Epoch, parent.Handle, parent.Epoch, true);
        Assert.True(NeutralVirtualIoContract.ValidateClose(ioLease, Success(ioClosed)).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            NeutralVirtualIoContract.ValidateClose(ioLease, Success(ioClosed with { IsTerminal = false })).Status);

        Assert.DoesNotContain(typeof(NeutralVirtualEventReceipt).GetProperties(), p => p.PropertyType == typeof(NeutralChildDomainLease));
        Assert.DoesNotContain(typeof(NeutralVirtualTrapEvidence).GetProperties(), p => p.PropertyType == typeof(NeutralChildDomainLease));
        Assert.DoesNotContain(typeof(NeutralGuestMappingCloseReceipt).GetProperties(), p => p.PropertyType == typeof(NeutralGuestMappingLease));
        Assert.DoesNotContain(typeof(NeutralVirtualIoCloseReceipt).GetProperties(), p => p.PropertyType == typeof(NeutralVirtualIoLease));
    }

    [Fact]
    public void TrapVocabularyIsStrictSemanticEvidenceNotAuthority()
    {
        Assert.Equal(
            ["MemoryFault", "IllegalInstruction", "Hypercall", "ExternalEvent", "Timer", "Preemption", "DeviceOrIoFault"],
            Enum.GetNames<NeutralVirtualTrapKind>());
        NeutralChildDomainLease child = Child(Parent(), Intent(All, All));
        Assert.True(NeutralVirtualTrapContract.ValidateEvidence(child,
            new(child.Handle, child.Epoch, child.ParentLease.Handle, child.ParentLease.Epoch, 1, NeutralVirtualTrapKind.Hypercall)).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            NeutralVirtualTrapContract.ValidateEvidence(child,
                new(child.Handle, new(child.Epoch.Value + 1), child.ParentLease.Handle, child.ParentLease.Epoch, 1, NeutralVirtualTrapKind.Hypercall)).Status);
        Assert.Equal(NeutralVirtualizationStatus.Faulted,
            NeutralVirtualTrapContract.ValidateEvidence(child,
                new(child.Handle, child.Epoch, child.ParentLease.Handle, child.ParentLease.Epoch, 1, (NeutralVirtualTrapKind)999)).Status);

        IEnumerable<string> names = typeof(NeutralVirtualTrapEvidence).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name);
        foreach (string? forbidden in new[] { "Capability", "Completion", "VmExit", "Vmcs", "Vmx", "Physical", "Iommu", "Descriptor", "HybridCpu" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChildCloseRequiresExactTerminalChildAndParentEpochs()
    {
        NeutralChildDomainLease child = Child(Parent(), Intent(All, All));
        NeutralChildDomainCloseReceipt receipt = new NeutralChildDomainCloseReceipt(
            child.Handle, child.Epoch, child.ParentLease.Handle, child.ParentLease.Epoch,
            NeutralChildDomainState.Closed, true);
        Assert.True(NeutralChildDomainContract.ValidateClose(child, Success(receipt)).IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Stale,
            NeutralChildDomainContract.ValidateClose(child, Success(receipt with { ChildEpoch = new(child.Epoch.Value + 1) })).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            NeutralChildDomainContract.ValidateClose(child, Success(receipt with { IsTerminal = false })).Status);
    }

    [Theory]
    [InlineData(NeutralVirtualizationStatus.Unsupported)]
    [InlineData(NeutralVirtualizationStatus.Unavailable)]
    [InlineData(NeutralVirtualizationStatus.Denied)]
    [InlineData(NeutralVirtualizationStatus.Revoked)]
    [InlineData(NeutralVirtualizationStatus.Faulted)]
    [InlineData(NeutralVirtualizationStatus.Ambiguous)]
    [InlineData((NeutralVirtualizationStatus)999)]
    public void NonDefinitiveAndUnknownCloseResultsFailClosed(NeutralVirtualizationStatus status)
    {
        NeutralChildDomainLease child = Child(Parent(), Intent(All, All));
        NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> result = new NeutralVirtualizationResult<NeutralChildDomainCloseReceipt>(status, default, "not definitive");
        Assert.False(result.IsSuccess);
        Assert.False(NeutralChildDomainContract.ValidateClose(child, result).IsSuccess);
    }

    [Fact]
    public void ModelAndInterfacePresenceDoNotClaimChildAvailability()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        foreach (NeutralRuntimeFeatureFamily family in ChildFamilies)
            Assert.Equal(NeutralRuntimeFeatureAvailability.Unavailable, runtime.QueryNeutralFeatures().Resolve(family).Availability);
        Assert.False(typeof(INeutralChildDomainProvider).IsAssignableFrom(runtime.GetType()));
        Assert.False(typeof(INeutralVirtualTrapProvider).IsAssignableFrom(runtime.GetType()));
    }

    private static readonly NeutralRuntimeFeatureFamily[] ChildFamilies =
    [
        NeutralRuntimeFeatureFamily.ChildDomainLifecycle,
        NeutralRuntimeFeatureFamily.ChildGuestMemory,
        NeutralRuntimeFeatureFamily.ChildEventDelivery,
        NeutralRuntimeFeatureFamily.ChildTrapDelivery,
        NeutralRuntimeFeatureFamily.BoundedVirtualIo,
    ];

    private const NeutralChildAuthorityClass All = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.Execution |
        NeutralChildAuthorityClass.GuestMemory | NeutralChildAuthorityClass.Events | NeutralChildAuthorityClass.Traps | NeutralChildAuthorityClass.Io;

    private static NeutralDomainBindingLease Parent(ulong handle = 1, ulong epoch = 1) => new(new(handle), new(epoch));
    private static NeutralChildDomainIntent Intent(NeutralChildAuthorityClass parent, NeutralChildAuthorityClass child) =>
        new(new(2, 16 * 1024 * 1024), new(parent, child));
    private static NeutralChildDomainLease Child(NeutralDomainBindingLease parent, NeutralChildDomainIntent intent) => new(parent, new(11), new(3), intent);
    private static NeutralOwnedRegionMappingLease Mapping(NeutralDomainBindingLease parent, NeutralMemoryAccess access) =>
        new(parent, new(256, 1024, access), NeutralMemoryCoherenceModel.NonCoherent, new(21), new(2));
    private static NeutralVirtualizationResult<T> Success<T>(T value) => new(NeutralVirtualizationStatus.Success, value, string.Empty);
}
