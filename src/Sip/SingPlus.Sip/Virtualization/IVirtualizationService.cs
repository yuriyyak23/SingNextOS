using SingPlus.Contracts;
using SingPlus.Sip.Native;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Virtualization;

[BoundedPayload(64)]
public readonly record struct CreateVirtualDomainRequest(CapabilityId CreateCapability, VirtualDomainProfile Profile) : IBoundedPayload
{
    public int PayloadSize => 32;
    public int MaxPayloadSize => 64;
}

[BoundedPayload(160)]
public readonly record struct VirtualDomainAuthorityResponse(VirtualDomainAuthoritySet Authority) : IBoundedPayload
{
    public int PayloadSize => 112;
    public int MaxPayloadSize => 160;
}

[BoundedPayload(80)]
public readonly record struct VirtualDomainCommand(VirtualDomainHandle Domain, CapabilityId Capability) : IBoundedPayload
{
    public int PayloadSize => 48;
    public int MaxPayloadSize => 80;
}

[BoundedPayload(192)]
public readonly record struct MapGuestRegionRequest(VirtualDomainHandle Domain, CapabilityId MemoryCapability, CapabilityId RegionCapability, RegionHandle Region, GuestAddressRange GuestRange, GuestMemoryAccess Access) : IBoundedPayload
{
    public int PayloadSize => 144;
    public int MaxPayloadSize => 192;
}

[BoundedPayload(128)]
public readonly record struct GuestRegionMappingResponse(GuestRegionMapping Mapping) : IBoundedPayload
{
    public int PayloadSize => 104;
    public int MaxPayloadSize => 128;
}

[BoundedPayload(112)]
public readonly record struct CloseGuestRegionMappingRequest(VirtualDomainHandle Domain, CapabilityId MemoryCapability, GuestRegionMappingHandle Mapping) : IBoundedPayload
{
    public int PayloadSize => 80;
    public int MaxPayloadSize => 112;
}

[BoundedPayload(112)]
public readonly record struct VirtualEventCommand(VirtualDomainHandle Domain, CapabilityId EventCapability, KernelEventEndpoint Endpoint) : IBoundedPayload
{
    public int PayloadSize => 88;
    public int MaxPayloadSize => 112;
}

[BoundedPayload(80)]
public readonly record struct WaitVirtualEventRequest(VirtualDomainHandle Domain, KernelEventEndpoint Endpoint) : IBoundedPayload
{
    public int PayloadSize => 64;
    public int MaxPayloadSize => 80;
}

[BoundedPayload(192)]
public readonly record struct VirtualEventResponse(KernelEvent Event) : IBoundedPayload
{
    public int PayloadSize => 160;
    public int MaxPayloadSize => 192;
}

[BoundedPayload(128)]
public readonly record struct CreateNestedVirtualDomainRequest(
    VirtualDomainHandle Parent,
    CapabilityId ParentCapability,
    NestedVirtualDomainRequestProfile Profile) : IBoundedPayload
{
    public int PayloadSize => 96;
    public int MaxPayloadSize => 128;
}

[BoundedPayload(96)]
public readonly record struct ObserveVirtualTrapRequest(
    VirtualDomainHandle Domain,
    CapabilityId TrapCapability) : IBoundedPayload
{
    public int PayloadSize => 48;
    public int MaxPayloadSize => 96;
}

[BoundedPayload(96)]
public readonly record struct VirtualTrapResponse(VirtualTrapObservation Trap) : IBoundedPayload
{
    public int PayloadSize => 48;
    public int MaxPayloadSize => 96;
}

/// <summary>Bounded-copy executable admission; package bytes are data, never provider authority or evidence.</summary>
[BoundedPayload(4352)]
public readonly record struct BindVirtualExecutableArtifactRequest(
    VirtualDomainHandle Domain, CapabilityId ExecuteCapability, GuestRegionMappingHandle Mapping,
    BoundedBytes ImmutablePackage, int MaximumExecutionSteps) : IBoundedPayload
{
    public int PayloadSize => 112 + ImmutablePackage.PayloadSize;
    public int MaxPayloadSize => 4352;
}

/// <summary>
/// Session-bound local virtualization control plane. Every command carries exact
/// Sing-local authority; discovery and the session itself confer no VM authority.
/// Platform leases and provider identities never cross this surface.
/// </summary>
[SipContract, InitialState("Ready")]
public interface IVirtualizationService
{
    [Message(1), Transition("Ready", "Ready")]
    ValueTask<VirtualDomainAuthorityResponse> CreateAsync(CreateVirtualDomainRequest request);

    [Message(2), Transition("Ready", "Ready")]
    ValueTask ConfigureAsync(VirtualDomainCommand command);

    [Message(3), Transition("Ready", "Ready")]
    ValueTask<GuestRegionMappingResponse> MapGuestRegionAsync(MapGuestRegionRequest request);

    [Message(4), Transition("Ready", "Ready")]
    ValueTask CloseGuestRegionMappingAsync(CloseGuestRegionMappingRequest request);

    [Message(5), Transition("Ready", "Ready")]
    ValueTask StartAsync(VirtualDomainCommand command);

    [Message(6), Transition("Ready", "Ready")]
    ValueTask ParkAsync(VirtualDomainCommand command);

    [Message(7), Transition("Ready", "Ready")]
    ValueTask ResumeAsync(VirtualDomainCommand command);

    [Message(8), Transition("Ready", "Ready")]
    ValueTask DestroyAsync(VirtualDomainCommand command);

    [Message(9), Transition("Ready", "Ready")]
    ValueTask InjectEventAsync(VirtualEventCommand command);

    [Message(10), Transition("Ready", "Ready")]
    ValueTask<VirtualEventResponse> WaitEventAsync(WaitVirtualEventRequest request);

    [Message(11), Transition("Ready", "Ready")]
    ValueTask<VirtualDomainAuthorityResponse> CreateNestedAsync(CreateNestedVirtualDomainRequest request);

    [Message(12), Transition("Ready", "Ready")]
    ValueTask<VirtualTrapResponse> ObserveTrapAsync(ObserveVirtualTrapRequest request);

    [Message(13), Transition("Ready", "Ready")]
    ValueTask BindExecutableArtifactAsync(BindVirtualExecutableArtifactRequest request);
}
