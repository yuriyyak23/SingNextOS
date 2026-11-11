namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Explicit initialization of separate immutable private storage. Mapping correlation is not a guest-memory snapshot or permission.</summary>
public sealed class NeutralSoftwareSecureMemorySourceRequest
{
    public NeutralSoftwareSecureMemorySourceRequest(NeutralGuestMappingLease mapping, Guid operation, ulong generation,
        ulong address, ReadOnlyMemory<byte> initialBytes, uint schemaVersion = 1)
    {
        if (operation == Guid.Empty || generation == 0) throw new ArgumentException("Original source operation required.");
        if (initialBytes.Length is < 8 or > 4096 || (address & 7) != 0 || address > ulong.MaxValue - (ulong)initialBytes.Length)
            throw new ArgumentException("One bounded aligned immutable source required.");
        Mapping = mapping; Operation = operation; Generation = generation; Address = address;
        _initialBytes = initialBytes.ToArray(); SchemaVersion = schemaVersion;
    }
    public NeutralGuestMappingLease Mapping { get; }
    public Guid Operation { get; }
    public ulong Generation { get; }
    public ulong Address { get; }
    private readonly byte[] _initialBytes;
    // A read-only view of an array still permits TryGetArray to recover its mutable owner.
    // Export a bounded snapshot so the original initializer remains privately owned.
    public ReadOnlyMemory<byte> InitialBytes => _initialBytes.ToArray();
    public uint SchemaVersion { get; }
}

/// <summary>Original source correlation; only the minting adapter's retained owner can adopt it.</summary>
public sealed record NeutralSoftwareSecureMemorySourceLease(NeutralSoftwareSecureMemorySourceRequest Request, Guid Region, ulong Generation);
public sealed record NeutralSoftwareSecureMemorySourceClosure(NeutralSoftwareSecureMemorySourceLease Source, bool NoFurtherUse);

public sealed class NeutralSoftwareSecureMemoryReadRequest(NeutralSoftwareSecureMemorySourceLease source, Guid operation,
    ulong generation, ulong address, uint schemaVersion = 1)
{
    public NeutralSoftwareSecureMemorySourceLease Source { get; } = source;
    public Guid Operation { get; } = operation;
    public ulong Generation { get; } = generation;
    public ulong Address { get; } = address;
    public uint SchemaVersion { get; } = schemaVersion;
}
public enum NeutralSoftwareSecureMemoryReadStatus { Denied = 1, Unsupported, CleanupRequired, LoadRetired }
/// <summary>Historical standalone retire/cleanup facts, never OS publication or a reusable execution entitlement.</summary>
public sealed record NeutralSoftwareSecureMemoryReadObservation(NeutralSoftwareSecureMemoryReadRequest Request,
    NeutralSoftwareSecureMemoryReadStatus Status, bool ResourcesClosed = false, ulong MachineAttempt = 0,
    ulong Commit = 0, ulong? Value = null)
{
    public bool IsExecutionAllowed => false;
}
public interface INeutralSoftwareSecureMemoryReadProvider
{
    NeutralSoftwareSecureMemorySourceLease? CreateSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceRequest request);
    NeutralSoftwareSecureMemorySourceLease? ResolveSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceRequest original);
    bool CloseOriginalSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceRequest original);
    bool IsOriginalSoftwareSecureMemorySourceClosed(NeutralSoftwareSecureMemorySourceRequest original);
    NeutralSoftwareSecureMemoryReadObservation ExecuteSoftwareSecureMemoryRead(NeutralSoftwareSecureMemoryReadRequest request);
    NeutralSoftwareSecureMemoryReadObservation ResolveSoftwareSecureMemoryRead(NeutralSoftwareSecureMemoryReadRequest original);
    bool CancelSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceLease original);
    NeutralSoftwareSecureMemorySourceClosure? CloseSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceLease original);
    NeutralSoftwareSecureMemorySourceClosure? ResolveSoftwareSecureMemorySourceClosure(NeutralSoftwareSecureMemorySourceLease original);
}
