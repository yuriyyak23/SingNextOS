namespace SingPlus.Contracts;

public enum SurfacePixelFormat : byte { Bgra8888 = 0, Rgba8888 = 1 }
public enum SurfaceLayout : byte { Linear = 0 }
public enum PresentTransferMode : byte { Move = 0, ReadLease = 1 }
public enum SurfaceLifecycleState : byte { Writable = 0, Presented = 1, Draining = 2, Closed = 3, Quarantined = 4 }

public readonly record struct SurfaceIdentity(ulong Value);
public readonly record struct SurfaceGeneration(ulong Value);
public readonly record struct SurfaceHandle(
    EndpointSessionHandle Session,
    SurfaceIdentity Identity,
    SurfaceGeneration Generation);
public readonly record struct SurfacePlaneSlice(int Offset, int Length, int RowStride);
public sealed record SurfaceMetadata(
    SurfacePixelFormat Format,
    int Width,
    int Height,
    int Stride,
    SurfaceLayout Layout,
    IReadOnlyList<SurfacePlaneSlice> Planes,
    ulong ProducerGeneration);
public readonly record struct SurfaceAuthority(SurfaceHandle Surface, CapabilityId PresentCapability);
public readonly record struct PresentOperationIdentity(ulong Value);
public readonly record struct ReleaseFence(
    PresentOperationIdentity Operation,
    SurfaceHandle Surface,
    RegionGeneration PresentedRegionGeneration,
    bool Terminal);
public readonly record struct UiRoleDescriptor(string Role, uint ContractVersion, string Availability);

public enum NormalizedInputKind : byte { PointerMove = 0, PointerButton = 1, Key = 2, Text = 3 }
public readonly record struct NormalizedInputEvent(
    ulong Sequence,
    NormalizedInputKind Kind,
    int Code,
    int Value,
    string Text);

public enum CompatibilityPersonality : byte { Posix = 0, Win32 = 1, Wine = 2 }
