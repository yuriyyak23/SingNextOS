using SingPlus.Contracts;
using SingPlus.Sip.Native;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Networking;

[BoundedPayload(512)] public readonly record struct OpenSocketRequest(CapabilityId EndpointCapability, string EndpointLabel) : IBoundedPayload { public int PayloadSize => 32 + (EndpointLabel?.Length ?? 0) * 2; public int MaxPayloadSize => 512; }
[BoundedPayload(96)] public readonly record struct SocketCommand(SocketObjectHandle Socket, CapabilityId Capability) : IBoundedPayload { public int PayloadSize => 48; public int MaxPayloadSize => 96; }
[BoundedPayload(4224)] public readonly record struct SendPacketRequest(SocketObjectHandle Socket, CapabilityId Capability, BoundedBytes Packet) : IBoundedPayload { public int PayloadSize => 64 + Packet.PayloadSize; public int MaxPayloadSize => 4224; }
[BoundedPayload(96)] public readonly record struct SocketObjectResponse(SocketObjectAuthority Authority) : IBoundedPayload { public int PayloadSize => 48; public int MaxPayloadSize => 96; }
[BoundedPayload(4160)] public readonly record struct ReceivePacketResponse(BoundedBytes Packet) : IBoundedPayload { public int PayloadSize => 32 + Packet.PayloadSize; public int MaxPayloadSize => 4160; }

[SipContract, InitialState("Ready")]
public interface INetworkService
{
    [Message(1), Transition("Ready", "Ready")] ValueTask<SocketObjectResponse> OpenAsync(OpenSocketRequest request);
    [Message(2), Transition("Ready", "Ready")] ValueTask SendAsync(SendPacketRequest request);
    [Message(3), Transition("Ready", "Ready")] ValueTask<ReceivePacketResponse> ReceiveAsync(SocketCommand command);
    [Message(4), Transition("Ready", "Ready")] ValueTask CloseAsync(SocketCommand command);
}
