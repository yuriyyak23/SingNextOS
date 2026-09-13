using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class Phase501PlatformContractTests
{
    [Fact]
    public void EmptyForgedStaleWrongParentAndWrongOwnerAreRejected()
    {
        var parent = Parent();
        var intent = Intent();
        var child = Child(parent, intent);

        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformChildDomainContract.ValidateLease(parent, intent, child with { LeaseId = default }).Status);
        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformChildDomainContract.ValidateLease(parent, intent, child with { Generation = default }).Status);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain,
            PlatformChildDomainContract.ValidateLease(parent, intent, child with { ParentDomainLease = Parent(9, 1) }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformChildDomainContract.ValidateLease(parent, intent, child with { ParentDomainLease = Parent(1, 2) }).Status);

        var mapping = ParentMapping(parent, PlatformMemoryAccess.Read, ownerDomain: 99);
        var request = new PlatformGuestRegionMappingRequest(child, mapping, new(0, 128), PlatformGuestMemoryAccess.Read);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, PlatformGuestMemoryContract.ValidateRequest(request).Status);
    }

    [Fact]
    public void ExactRangeAndAccessCannotBeAmplifiedAndStaleUnmapIsNotClosure()
    {
        var parent = Parent();
        var child = Child(parent, Intent());
        var mapping = ParentMapping(parent, PlatformMemoryAccess.Read);
        var request = new PlatformGuestRegionMappingRequest(child, mapping, new(0, 512), PlatformGuestMemoryAccess.Read);
        Assert.True(PlatformGuestMemoryContract.ValidateRequest(request).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformGuestMemoryContract.ValidateRequest(request with { GuestRange = new(0, mapping.Slice.Length + 1) }).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformGuestMemoryContract.ValidateRequest(request with { Access = PlatformGuestMemoryAccess.Write }).Status);

        var lease = new PlatformProviderGuestRegionMappingLease(new(31), new(4), child, mapping.Lease, request.GuestRange, request.Access);
        var stale = new PlatformGuestRegionMappingClosureReceipt(lease.LeaseId, new(5), child.LeaseId, child.Generation, parent.LeaseId, parent.Generation, true);
        Assert.Equal(PlatformAuthorityStatus.Stale, PlatformGuestMemoryContract.ValidateClosureReceipt(lease, stale).Status);
        var ambiguous = new PlatformGuestRegionMappingClosureReceipt(lease.LeaseId, lease.Generation, child.LeaseId, child.Generation, parent.LeaseId, parent.Generation, false);
        Assert.Equal(PlatformAuthorityStatus.Faulted, PlatformGuestMemoryContract.ValidateClosureReceipt(lease, ambiguous).Status);
    }

    [Fact]
    public void DrainRejectsEventsAndTrapVocabularyIsStrictlySemantic()
    {
        var child = Child(Parent(), Intent());
        var request = new PlatformVirtualEventRequest(child, PlatformVirtualEventClass.ExternalSignal, "event:test");
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformVirtualEventContract.ValidateRequest(request, PlatformChildDomainState.Draining).Status);

        Assert.Equal(
            ["MemoryFault", "IllegalInstruction", "Hypercall", "ExternalEvent", "Timer", "Preemption", "DeviceOrIoFault"],
            Enum.GetNames<PlatformVirtualTrapKind>());
        Assert.True(PlatformVirtualTrapContract.ValidateEvidence(child,
            new(child.LeaseId, child.Generation, child.ParentDomainLease.LeaseId, child.ParentDomainLease.Generation, 1, PlatformVirtualTrapKind.MemoryFault)).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformVirtualTrapContract.ValidateEvidence(child,
                new(child.LeaseId, new(child.Generation.Value + 1), child.ParentDomainLease.LeaseId, child.ParentDomainLease.Generation, 1, PlatformVirtualTrapKind.MemoryFault)).Status);
        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformVirtualTrapContract.ValidateEvidence(child,
                new(child.LeaseId, child.Generation, child.ParentDomainLease.LeaseId, child.ParentDomainLease.Generation, 1, (PlatformVirtualTrapKind)999)).Status);
    }

    [Fact]
    public void ReceiptsAndTrapEvidenceCannotBeReusedAsAuthorityOrCompletion()
    {
        AssertNoLeaseProperty(typeof(PlatformVirtualEventReceipt));
        AssertNoLeaseProperty(typeof(PlatformVirtualTrapEvidence));
        AssertNoLeaseProperty(typeof(PlatformGuestRegionMappingClosureReceipt));
        AssertNoLeaseProperty(typeof(PlatformVirtualIoClosureReceipt));

        foreach (var type in new[] { typeof(PlatformVirtualEventReceipt), typeof(PlatformVirtualTrapEvidence) })
        {
            var names = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name);
            Assert.DoesNotContain(names, name => name.Contains("Capability", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("Completion", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void VirtualIoClosureIsExactParentBoundAndTerminal()
    {
        var parent = Parent();
        var child = Child(parent, Intent());
        var device = new PlatformProviderDeviceLease(new(51), new(2), parent, new("device:test"), PlatformDeviceRights.Read);
        var request = new PlatformVirtualIoRequest(child, device, new(PlatformDeviceRights.Read, 4096));
        var lease = new PlatformProviderVirtualIoLease(new(61), new(3), child, device, request.Profile);
        Assert.True(PlatformVirtualIoContract.ValidateRequest(request).IsSuccess);
        Assert.True(PlatformVirtualIoContract.ValidateLease(request, lease).IsSuccess);
        var receipt = new PlatformVirtualIoClosureReceipt(
            lease.LeaseId, lease.Generation, child.LeaseId, child.Generation,
            parent.LeaseId, parent.Generation, true);
        Assert.True(PlatformVirtualIoContract.ValidateClosureReceipt(lease, receipt).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformVirtualIoContract.ValidateClosureReceipt(lease, receipt with { IsTerminal = false }).Status);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain,
            PlatformVirtualIoContract.ValidateClosureReceipt(lease, receipt with { ParentLeaseId = new(99) }).Status);
    }

    [Fact]
    public void ExecutableAdmissionAndStartRequireExactChildMappingAndGenerations()
    {
        var parent = Parent();
        var child = Child(parent, Intent());
        var mapping = new PlatformProviderGuestRegionMappingLease(new(31), new(4), child,
            ParentMapping(parent, PlatformMemoryAccess.Read).Lease, new(0, 4096),
            PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Execute);
        var request = new PlatformExecutableArtifactRequest(child, mapping, new byte[128], 1000);
        var receipt = new PlatformExecutableArtifactReceipt(new(41), new(2), child.LeaseId,
            child.Generation, mapping.LeaseId, mapping.Generation, parent.LeaseId, parent.Generation,
            new string('a', 64), 1000);

        Assert.True(PlatformChildExecutionContract.ValidateAdmissionReceipt(request, receipt).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain,
            PlatformChildExecutionContract.ValidateAdmissionReceipt(request,
                receipt with { MappingLeaseId = new(99) }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformChildExecutionContract.ValidateAdmissionReceipt(request,
                receipt with { MappingGeneration = new(mapping.Generation.Value + 1) }).Status);
        var start = new PlatformChildExecutionStartRequest(child, receipt, new(51), new(3));
        Assert.True(PlatformChildExecutionContract.ValidateStart(start).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain,
            PlatformChildExecutionContract.ValidateStart(
                start with { ChildLease = child with { LeaseId = new(99) } }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformChildExecutionContract.ValidateStart(
                start with { ChildLease = child with { Generation = new(child.Generation.Value + 1) } }).Status);
        var execution = new PlatformChildExecutionReceipt(receipt.ArtifactId, receipt.ArtifactGeneration,
            receipt.ChildLeaseId, receipt.ChildGeneration, receipt.MappingLeaseId, receipt.MappingGeneration,
            receipt.ParentLeaseId, receipt.ParentGeneration, receipt.ContentDigest, start.OperationId,
            start.OperationGeneration, new(7), 12, 9, 256, true);
        Assert.True(PlatformChildExecutionContract.ValidateExecution(start, execution).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformChildExecutionContract.ValidateExecution(start,
                execution with { StartOperationGeneration = new(start.OperationGeneration.Value + 1) }).Status);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Unsupported)]
    [InlineData(PlatformAuthorityStatus.Unavailable)]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Revoked)]
    [InlineData(PlatformAuthorityStatus.Faulted)]
    [InlineData((PlatformAuthorityStatus)999)]
    public void UnsupportedRevokedFaultedAndUnknownResultsFailClosed(PlatformAuthorityStatus status)
    {
        Assert.False(new PlatformAuthorityResult(status, "failure").IsSuccess);
        Assert.False(new PlatformAuthorityResult<PlatformChildDomainClosureReceipt>(status, default, "failure").IsSuccess);
    }

    [Fact]
    public void InterfacePresenceAndAdmissionShapeDoNotCreateBackendAvailability()
    {
        var provider = new HybridCpuPlatformAuthorityProvider();
        Assert.IsNotAssignableFrom<IPlatformVirtualTrapProvider>(provider);
        foreach (var family in new[]
                 {
                     PlatformFeatureFamily.ChildDomainLifecycle,
                     PlatformFeatureFamily.ChildGuestMemory,
                     PlatformFeatureFamily.ChildEventDelivery,
                     PlatformFeatureFamily.ChildTrapDelivery,
                     PlatformFeatureFamily.BoundedVirtualIo,
                     PlatformFeatureFamily.VirtualizationDomains,
                     PlatformFeatureFamily.NestedDomains,
                 })
            Assert.Equal(PlatformFeatureAvailability.Unavailable, provider.QueryFeatures().Resolve(family).Availability);
    }

    private static void AssertNoLeaseProperty(Type type) =>
        Assert.DoesNotContain(type.GetProperties(), p =>
            p.PropertyType == typeof(PlatformProviderChildDomainLease) ||
            p.PropertyType == typeof(PlatformProviderGuestRegionMappingLease) ||
            p.PropertyType == typeof(PlatformProviderVirtualIoLease));

    private const PlatformChildAuthorityClass All = PlatformChildAuthorityClass.Lifecycle |
        PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory |
        PlatformChildAuthorityClass.Events | PlatformChildAuthorityClass.Traps | PlatformChildAuthorityClass.Io;

    private static PlatformProviderDomainLease Parent(ulong id = 1, ulong generation = 1) =>
        new(new(id), new(generation), new(new DomainId(10), new ProcessHandle(new ProcessId(20), 3)));
    private static PlatformChildDomainIntent Intent() =>
        new(new(2, 16 * 1024 * 1024), new(All, All));
    private static PlatformProviderChildDomainLease Child(PlatformProviderDomainLease parent, PlatformChildDomainIntent intent) =>
        new(new(11), new(3), parent, intent);

    private static PlatformProviderOwnedRegionMapping ParentMapping(
        PlatformProviderDomainLease parent,
        PlatformMemoryAccess access,
        ulong? ownerDomain = null)
    {
        var region = new PlatformRegionIdentity(
            new RegionHandle(new RegionId(7), new RegionGeneration(2)),
            new RegionOwner(new DomainId(ownerDomain ?? parent.Subject.DomainId.Value), parent.Subject.ProcessGeneration),
            4096);
        var slice = new PlatformRegionSlice(region, 256, 1024, access);
        return new(
            new(new PlatformProviderRegionMappingId(21), new PlatformProviderLeaseGeneration(2), parent, region, access),
            slice);
    }
}
