namespace SingPlus.Platform;

/// <summary>Initialize separate immutable private storage and load one word; never a snapshot of existing guest bytes.</summary>
public sealed class PlatformSoftwareSecureMemoryReadRequest
{
    public PlatformSoftwareSecureMemoryReadRequest(PlatformProviderGuestRegionMappingLease mapping, Guid operation,
        ulong generation, ulong address, ReadOnlyMemory<byte> initialBytes)
    {
        if (operation == Guid.Empty || generation == 0 || initialBytes.Length is < 8 or > 4096 ||
            (address & 7) != 0 || address > ulong.MaxValue - (ulong)initialBytes.Length) throw new ArgumentException("Bounded original initialized source required.");
        Mapping = mapping; Operation = operation; Generation = generation; Address = address; _initialBytes = initialBytes.ToArray();
    }
    public PlatformProviderGuestRegionMappingLease Mapping { get; }
    public Guid Operation { get; }
    public ulong Generation { get; }
    public ulong Address { get; }
    private readonly byte[] _initialBytes;
    // A read-only view of an array still permits TryGetArray to recover its mutable owner.
    // Export a bounded snapshot so the original initializer remains privately owned.
    public ReadOnlyMemory<byte> InitialBytes => _initialBytes.ToArray();
}
/// <summary>Historical load/independent cleanup facts, not OS publication or permission.</summary>
public sealed record PlatformSoftwareSecureMemoryReadObservation(PlatformSoftwareSecureMemoryReadRequest Request,
    bool ResourcesClosed, ulong MachineAttempt = 0, ulong Commit = 0, ulong? Value = null)
{
    public bool IsExecutionAllowed => false;
}
public interface IPlatformSoftwareSecureMemoryReadProvider
{
    PlatformSoftwareSecureMemoryReadObservation ExecuteSoftwareSecureMemoryRead(PlatformSoftwareSecureMemoryReadRequest request);
    PlatformSoftwareSecureMemoryReadObservation ResolveSoftwareSecureMemoryRead(PlatformSoftwareSecureMemoryReadRequest original);
}
