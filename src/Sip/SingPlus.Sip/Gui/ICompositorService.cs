using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Gui;

[BoundedPayload(512)]
public readonly record struct RegisterSurfaceRequest(
    CapabilityId PresentCapability,
    RegionHandle Buffer,
    SurfaceMetadata Metadata) : IBoundedPayload
{
    public int PayloadSize => 96 + (Metadata.Planes?.Count ?? 0) * 16;
    public int MaxPayloadSize => 512;
}

[BoundedPayload(96)]
public readonly record struct SurfaceAuthorityResponse(SurfaceAuthority Authority) : IBoundedPayload
{
    public int PayloadSize => 64;
    public int MaxPayloadSize => 96;
}

[BoundedPayload(128)]
public readonly record struct ReleaseFenceResponse(ReleaseFence Fence) : IBoundedPayload
{
    public int PayloadSize => 96;
    public int MaxPayloadSize => 128;
}

[BoundedPayload(96)]
public readonly record struct PreparePresentRequest(SurfaceHandle Surface, PresentTransferMode Mode) : IBoundedPayload
{
    public int PayloadSize => 64;
    public int MaxPayloadSize => 96;
}

[SipContract, InitialState("Ready")]
public interface ICompositorService
{
    [Message(1), Transition("Ready", "Ready")]
    ValueTask<SurfaceAuthorityResponse> RegisterSurfaceAsync(RegisterSurfaceRequest request);

    [Message(2), Transition("Ready", "Ready")]
    ValueTask PreparePresentAsync(PreparePresentRequest request);

    [Message(3), Transition("Ready", "Ready")]
    [RequiresCapability(ResourceKind.Compositor, CapabilityResourceIds.CompositorPresent, CapabilityRights.Write)]
    [ReturnsOwnership]
    ValueTask<OwnedBuffer<byte>> PresentMoveAsync([Consumes] OwnedBuffer<byte> buffer);

    [Message(4), Transition("Ready", "Ready")]
    [RequiresCapability(ResourceKind.Compositor, CapabilityResourceIds.CompositorPresent, CapabilityRights.Read)]
    ValueTask<ReleaseFenceResponse> PresentReadLeaseAsync([Borrows] OwnedBuffer<byte> buffer);
}

[BoundedPayload(32)]
public readonly record struct UiRoleQuery(byte Reserved) : IBoundedPayload
{
    public int PayloadSize => 1;
    public int MaxPayloadSize => 32;
}

[BoundedPayload(128)]
public readonly record struct UiRoleResponse(UiRoleDescriptor Descriptor) : IBoundedPayload
{
    public int PayloadSize => 96;
    public int MaxPayloadSize => 128;
}

[SipContract, InitialState("Ready")] public interface IDisplayService { [Message(1), Transition("Ready", "Ready")] ValueTask<UiRoleResponse> DescribeAsync(UiRoleQuery request); }
[SipContract, InitialState("Ready")] public interface IWindowManagerService { [Message(1), Transition("Ready", "Ready")] ValueTask<UiRoleResponse> DescribeAsync(UiRoleQuery request); }
[SipContract, InitialState("Ready")] public interface IClipboardService { [Message(1), Transition("Ready", "Ready")] ValueTask<UiRoleResponse> DescribeAsync(UiRoleQuery request); }
[SipContract, InitialState("Ready")] public interface IFontTextService { [Message(1), Transition("Ready", "Ready")] ValueTask<UiRoleResponse> DescribeAsync(UiRoleQuery request); }
[SipContract, InitialState("Ready")] public interface IAccessibilityService { [Message(1), Transition("Ready", "Ready")] ValueTask<UiRoleResponse> DescribeAsync(UiRoleQuery request); }
[SipContract, InitialState("Ready")] public interface INotificationService { [Message(1), Transition("Ready", "Ready")] ValueTask<UiRoleResponse> DescribeAsync(UiRoleQuery request); }
[SipContract, InitialState("Ready")] public interface IShellService { [Message(1), Transition("Ready", "Ready")] ValueTask<UiRoleResponse> DescribeAsync(UiRoleQuery request); }

[BoundedPayload(48)]
public readonly record struct ReadInputRequest(CapabilityId Capability) : IBoundedPayload { public int PayloadSize => 16; public int MaxPayloadSize => 48; }
[BoundedPayload(160)]
public readonly record struct InputEventResponse(NormalizedInputEvent Event) : IBoundedPayload { public int PayloadSize => 128; public int MaxPayloadSize => 160; }
[SipContract, InitialState("Ready")]
public interface IInputService
{
    [Message(1), Transition("Ready", "Ready")]
    ValueTask<InputEventResponse> ReadAsync(ReadInputRequest request);
}
