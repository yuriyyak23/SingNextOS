namespace SingPlus.Contracts;

public readonly record struct FileObjectId(ulong Value);
public readonly record struct FileObjectGeneration(ulong Value);
public readonly record struct FileObjectHandle(
    EndpointSessionHandle Session,
    FileObjectId ObjectId,
    FileObjectGeneration Generation,
    SealedHandle<FileObjectSeal> Seal = default);
public readonly struct FileObjectSeal : ISealedContractMarker;
public readonly record struct SocketObjectId(ulong Value);
public readonly record struct SocketObjectGeneration(ulong Value);
public readonly record struct SocketObjectHandle(
    EndpointSessionHandle Session,
    SocketObjectId ObjectId,
    SocketObjectGeneration Generation,
    SealedHandle<SocketObjectSeal> Seal = default);
public enum ChildProcessPolicy : byte { Attached = 0, Independent = 1 }
public readonly struct ProcessControlSeal : ISealedContractMarker;
public readonly record struct ProcessControlObjectHandle(ProcessHandle Process, ulong Generation, SealedHandle<ProcessControlSeal> Seal);
public readonly record struct ProcessAuthority(ProcessHandle Process, CapabilityId ControlCapability, ProcessControlObjectHandle Control = default);
public readonly record struct FileObjectAuthority(FileObjectHandle File, CapabilityId Capability);
public readonly record struct SocketObjectAuthority(SocketObjectHandle Socket, CapabilityId Capability);
