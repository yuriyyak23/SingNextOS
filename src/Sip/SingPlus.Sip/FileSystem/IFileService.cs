using SingPlus.Contracts;
using SingPlus.Sip.Native;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.FileSystem;

[BoundedPayload(512)] public readonly record struct OpenFileRequest(CapabilityId NamespaceCapability, string Path, bool Create) : IBoundedPayload { public int PayloadSize => 32 + (Path?.Length ?? 0) * 2; public int MaxPayloadSize => 512; }
[BoundedPayload(96)] public readonly record struct FileCommand(FileObjectHandle File, CapabilityId Capability) : IBoundedPayload { public int PayloadSize => 48; public int MaxPayloadSize => 96; }
[BoundedPayload(4224)] public readonly record struct FileWriteRequest(FileObjectHandle File, CapabilityId Capability, BoundedBytes Data) : IBoundedPayload { public int PayloadSize => 64 + Data.PayloadSize; public int MaxPayloadSize => 4224; }
[BoundedPayload(96)] public readonly record struct FileObjectResponse(FileObjectAuthority Authority) : IBoundedPayload { public int PayloadSize => 48; public int MaxPayloadSize => 96; }
[BoundedPayload(4160)] public readonly record struct FileReadResponse(BoundedBytes Data) : IBoundedPayload { public int PayloadSize => 32 + Data.PayloadSize; public int MaxPayloadSize => 4160; }

[SipContract, InitialState("Ready")]
public interface IFileService
{
    [Message(1), Transition("Ready", "Ready")] ValueTask<FileObjectResponse> OpenAsync(OpenFileRequest request);
    [Message(2), Transition("Ready", "Ready")] ValueTask WriteAsync(FileWriteRequest request);
    [Message(3), Transition("Ready", "Ready")] ValueTask<FileReadResponse> ReadAsync(FileCommand command);
    [Message(4), Transition("Ready", "Ready")] ValueTask CloseAsync(FileCommand command);
}
