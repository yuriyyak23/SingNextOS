using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuChildDomainContourTests
{
    [Fact]
    public void ChildAuthorityMustRemainAProperParentSubset()
    {
        var parent = ParentLease();
        var valid = Intent(
            PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution |
            PlatformChildAuthorityClass.GuestMemory |
            PlatformChildAuthorityClass.Events |
            PlatformChildAuthorityClass.Io,
            PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution |
            PlatformChildAuthorityClass.GuestMemory);
        Assert.True(PlatformChildDomainContract.ValidateCreateRequest(parent, valid).IsSuccess);

        var amplified = Intent(
            PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution,
            PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution |
            PlatformChildAuthorityClass.Io);
        var denied = PlatformChildDomainContract.ValidateCreateRequest(parent, amplified);
        Assert.Equal(PlatformAuthorityStatus.Denied, denied.Status);

        var noLifecycle = Intent(
            PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution,
            PlatformChildAuthorityClass.Execution);
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformChildDomainContract.ValidateCreateRequest(parent, noLifecycle).Status);
    }

    [Fact]
    public void GuestMappingIsBoundToExactChildParentMappingAndBounds()
    {
        var parent = ParentLease();
        var intent = Intent(AllAuthority, AllAuthority);
        var child = ChildLease(parent, intent);
        var mapping = ParentMapping(parent, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var request = new PlatformGuestRegionMappingRequest(
            child,
            mapping,
            new PlatformGuestAddressRange(0x1000, 512),
            PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Write);
        Assert.True(PlatformGuestMemoryContract.ValidateRequest(request).IsSuccess);

        var widened = request with
        {
            ParentMapping = ParentMapping(parent, PlatformMemoryAccess.Read),
            Access = PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Write,
        };
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformGuestMemoryContract.ValidateRequest(widened).Status);

        var oversized = request with
        {
            GuestRange = new PlatformGuestAddressRange(0x1000, mapping.Slice.Length + 1),
        };
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformGuestMemoryContract.ValidateRequest(oversized).Status);

        var foreignParent = ParentLease(leaseId: 99);
        var foreignMapping = ParentMapping(foreignParent, PlatformMemoryAccess.Read);
        var foreign = request with { ParentMapping = foreignMapping, Access = PlatformGuestMemoryAccess.Read };
        Assert.False(PlatformGuestMemoryContract.ValidateRequest(foreign).IsSuccess);
    }

    [Fact]
    public void EventAndIoContoursCannotEscapeAdmittedChildOrParentAuthority()
    {
        var parent = ParentLease();
        var child = ChildLease(parent, Intent(AllAuthority, AllAuthority));
        var eventRequest = new PlatformVirtualEventRequest(
            child,
            PlatformVirtualEventClass.ExternalSignal,
            "irq:virt:test");
        Assert.True(PlatformVirtualEventContract.ValidateRequest(eventRequest).IsSuccess);
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformVirtualEventContract.ValidateRequest(eventRequest with { SourceResourceId = " " }).Status);

        var parentDevice = new PlatformProviderDeviceLease(
            new PlatformProviderDeviceLeaseId(41),
            new PlatformProviderLeaseGeneration(3),
            parent,
            new PlatformDeviceIdentity("device:test"),
            PlatformDeviceRights.Read | PlatformDeviceRights.Write);
        var io = new PlatformVirtualIoRequest(
            child,
            parentDevice,
            new PlatformVirtualIoProfile(PlatformDeviceRights.Read, 4096));
        Assert.True(PlatformVirtualIoContract.ValidateRequest(io).IsSuccess);

        var widened = io with
        {
            Profile = new PlatformVirtualIoProfile(
                PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
                4096),
        };
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformVirtualIoContract.ValidateRequest(widened).Status);

        var noIoChild = ChildLease(
            parent,
            Intent(
                AllAuthority,
                PlatformChildAuthorityClass.Lifecycle |
                PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory |
                PlatformChildAuthorityClass.Events));
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformVirtualIoContract.ValidateRequest(io with { ChildLease = noIoChild }).Status);
    }

    [Fact]
    public void ClosureReceiptMustMatchExactChildGenerationAndParent()
    {
        var parent = ParentLease();
        var child = ChildLease(parent, Intent(AllAuthority, AllAuthority));
        var receipt = new PlatformChildDomainClosureReceipt(
            child.LeaseId,
            child.Generation,
            parent.LeaseId,
            parent.Generation,
            PlatformChildDomainClosureDisposition.Closed);
        Assert.True(PlatformChildDomainContract.ValidateClosureReceipt(child, receipt).IsSuccess);

        var stale = receipt with
        {
            Generation = new PlatformProviderLeaseGeneration(child.Generation.Value + 1),
        };
        Assert.Equal(
            PlatformAuthorityStatus.Stale,
            PlatformChildDomainContract.ValidateClosureReceipt(child, stale).Status);

        var wrongParent = receipt with { ParentLeaseId = ParentLease(leaseId: 88).LeaseId };
        Assert.Equal(
            PlatformAuthorityStatus.WrongDomain,
            PlatformChildDomainContract.ValidateClosureReceipt(child, wrongParent).Status);
    }

    [Fact]
    public void HybridCpuChildAdapterIsExternalBlockedAndNeverFallsBackToOrdinaryDomainRuntime()
    {
        var (runtime, proxy) = ProbeRuntimeProxy.Create();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var status = provider.QueryChildDomainAdapterStatus();

        Assert.Equal(HybridCpuChildDomainAdapterReadiness.ExternalBlocked, status.Readiness);
        Assert.Equal("EXT-HCPU-006", status.ExternalRequirement);
        Assert.Equal(PlatformChildDomainContract.ContractVersion, status.PlatformContractVersion);
        Assert.True(status.HasVersionedExternalChildContract);
        Assert.True(status.HasParentChildSubset);
        Assert.True(status.HasGuestMemory);
        Assert.True(status.HasVirtualEvents);
        Assert.True(status.HasVirtualTraps);
        Assert.False(status.HasVirtualIo);
        Assert.True(status.HasDefinitiveClose);
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            provider.QueryFeatures().Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            provider.QueryFeatures().Resolve(PlatformFeatureFamily.NestedDomains).Availability);
        Assert.IsNotAssignableFrom<IPlatformVirtualizationProvider>(provider);
        Assert.IsAssignableFrom<IPlatformChildDomainProvider>(provider);
        Assert.IsNotAssignableFrom<IPlatformVirtualTrapProvider>(provider);

        var parent = ParentLease();
        var intent = Intent(AllAuthority, AllAuthority);
        var child = ChildLease(parent, intent);
        var childProvider = (IPlatformChildDomainProvider)provider;
        AssertBlocked(childProvider.CreateChildDomain(parent, intent));
        AssertBlocked(childProvider.TransitionChildDomain(child, PlatformChildDomainTransition.Start));
        AssertBlocked(childProvider.CloseChildDomain(child));

        var mappingRequest = new PlatformGuestRegionMappingRequest(
            child,
            ParentMapping(parent, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write),
            new PlatformGuestAddressRange(0, 512),
            PlatformGuestMemoryAccess.Read);
        AssertBlocked(((IPlatformGuestMemoryProvider)provider).MapGuestRegion(mappingRequest));
        AssertBlocked(((IPlatformVirtualEventProvider)provider).InjectVirtualEvent(
            new PlatformVirtualEventRequest(child, PlatformVirtualEventClass.ExternalSignal, "event:test")));

        var parentDevice = new PlatformProviderDeviceLease(
            new PlatformProviderDeviceLeaseId(51),
            new PlatformProviderLeaseGeneration(1),
            parent,
            new PlatformDeviceIdentity("device:child-test"),
            PlatformDeviceRights.Read | PlatformDeviceRights.Write);
        AssertBlocked(((IPlatformVirtualIoProvider)provider).BindVirtualIo(
            new PlatformVirtualIoRequest(
                child,
                parentDevice,
                new PlatformVirtualIoProfile(PlatformDeviceRights.Read, 1024))));

        var kernel = new RuntimeKernel(provider);
        var manifest = new SingProcessManifestV1(
            new ProcessId(9201),
            new DomainId(92010),
            1,
            "child-contour-owner",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        var process = kernel.CreateProcess(manifest).Value!;
        var owner = new ProcessHandle(process.ProcessId, process.Generation);
        var createCapability = kernel.MintCapability(
            process.DomainId,
            owner,
            ResourceKind.Virtualization,
            VirtualizationResourceIds.Create,
            CapabilityRights.Configure).Value!.CapabilityId;
        var create = kernel.CreateVirtualDomain(
            owner,
            createCapability,
            new VirtualDomainProfile(1, 4096));
        Assert.Equal(KernelError.PlatformUnsupported, create.Error);
        Assert.Equal(0, proxy.ExternalOperationCalls);
    }

    private static void AssertBlocked(PlatformAuthorityResult result)
    {
        Assert.Equal(PlatformAuthorityStatus.Unsupported, result.Status);
        Assert.Contains("EXT-HCPU-006 ExternalBlocked", result.Message, StringComparison.Ordinal);
    }

    private static void AssertBlocked<T>(PlatformAuthorityResult<T> result)
    {
        Assert.Equal(PlatformAuthorityStatus.Unsupported, result.Status);
        Assert.Contains("EXT-HCPU-006 ExternalBlocked", result.Message, StringComparison.Ordinal);
    }

    private const PlatformChildAuthorityClass AllAuthority =
        PlatformChildAuthorityClass.Lifecycle |
        PlatformChildAuthorityClass.Execution |
        PlatformChildAuthorityClass.GuestMemory |
        PlatformChildAuthorityClass.Events |
        PlatformChildAuthorityClass.Traps |
        PlatformChildAuthorityClass.Io;

    private static PlatformChildDomainIntent Intent(
        PlatformChildAuthorityClass parent,
        PlatformChildAuthorityClass child) =>
        new(
            new PlatformVirtualDomainProfile(2, 16 * 1024 * 1024),
            new PlatformChildDomainAuthoritySubset(parent, child));

    private static PlatformProviderDomainLease ParentLease(
        ulong leaseId = 11,
        ulong generation = 3) =>
        new(
            new PlatformProviderDomainLeaseId(leaseId),
            new PlatformProviderLeaseGeneration(generation),
            new PlatformDomainIdentity(
                new DomainId(9100),
                new ProcessHandle(new ProcessId(910), 7)));

    private static PlatformProviderChildDomainLease ChildLease(
        PlatformProviderDomainLease parent,
        PlatformChildDomainIntent intent) =>
        new(
            new PlatformProviderChildDomainLeaseId(21),
            new PlatformProviderLeaseGeneration(4),
            parent,
            intent);

    private static PlatformProviderOwnedRegionMapping ParentMapping(
        PlatformProviderDomainLease parent,
        PlatformMemoryAccess access)
    {
        var region = new PlatformRegionIdentity(
            new RegionHandle(new RegionId(71), new RegionGeneration(5)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration),
            4096);
        var slice = new PlatformRegionSlice(region, 256, 1024, access);
        var lease = new PlatformProviderRegionMappingLease(
            new PlatformProviderRegionMappingId(31),
            new PlatformProviderLeaseGeneration(2),
            parent,
            region,
            access);
        return new PlatformProviderOwnedRegionMapping(lease, slice);
    }

    private class ProbeRuntimeProxy : DispatchProxy
    {
        public int ExternalOperationCalls { get; private set; }

        public static (INeutralDomainRuntime Runtime, ProbeRuntimeProxy Proxy) Create()
        {
            var runtime = Create<INeutralDomainRuntime, ProbeRuntimeProxy>();
            return (runtime, (ProbeRuntimeProxy)(object)runtime);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_ImplementationProfile")
                return NeutralRuntimeImplementationProfile.ExecutableAdapter;
            if (targetMethod?.Name == nameof(INeutralRuntimeFeatureProvider.QueryNeutralFeatures))
            {
                return new NeutralRuntimeFeatureManifest(
                [
                    new(
                        NeutralRuntimeFeatureFamily.DomainLifecycle,
                        1,
                        NeutralRuntimeFeatureAvailability.Executable),
                ]);
            }

            ExternalOperationCalls++;
            throw new InvalidOperationException(
                $"Blocked child-domain scaffold attempted external ordinary-domain operation '{targetMethod?.Name}'.");
        }
    }
}
