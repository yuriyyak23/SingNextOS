using SingPlus.Contracts;

namespace SingPlus.Platform;

[Flags]
public enum CxlEndpointFeatures
{
    None = 0,
    Io = 1 << 0,
    Memory = 1 << 1,
    CoherentAccess = 1 << 2
}

public readonly record struct CxlEndpointId(string Value);
public readonly record struct CxlDeviceGeneration(ulong Value);
public readonly record struct CxlFabricBindingId(ulong Value);
public readonly record struct CxlFabricBindingGeneration(ulong Value);
public readonly record struct CxlMemoryBindingId(ulong Value);
public readonly record struct CxlMemoryBindingGeneration(ulong Value);
public readonly record struct CxlCoherentBindingId(ulong Value);
public readonly record struct CxlCoherentBindingGeneration(ulong Value);

public sealed record CxlEndpointSnapshot(
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    CxlEndpointFeatures Features,
    bool Available);

public enum CxlMemoryPersistence { Volatile = 0, Persistent = 1 }
public enum CxlMemorySharing { Exclusive = 0, SharedReadMostly = 1 }
public enum CxlMemoryPlacementPreference { CxlRequired = 0, CxlPreferred = 1, LocalRequired = 2 }
public enum CxlMemoryPlacementKind { CxlType3 = 0, Local = 1 }

public sealed record CxlMemoryPlacementIntent(
    long CapacityBytes,
    CxlMemoryPersistence Persistence,
    CxlMemorySharing Sharing,
    CxlMemoryPlacementPreference Preference,
    int MaximumLatencyClass,
    int MinimumBandwidthClass);

public sealed record CxlMemoryPlacementDecision(
    CxlMemoryPlacementKind Kind,
    CxlEndpointSnapshot? Endpoint,
    CxlMemoryCapacitySnapshot? Capacity);

public sealed record CxlFabricBindingRequest(
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    long CapacityBytes,
    CxlMemoryPersistence Persistence,
    CxlMemorySharing Sharing);

public sealed record CxlFabricBinding(
    CxlFabricBindingId BindingId,
    CxlFabricBindingGeneration Generation,
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    long CapacityBytes);

public sealed record CxlMemoryBinding(
    CxlMemoryBindingId BindingId,
    CxlMemoryBindingGeneration Generation,
    CxlFabricBinding FabricBinding,
    RegionBackingLeaseHandle BackingLease);

public sealed record CxlMemoryCapacitySnapshot(
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    long TotalBytes,
    long AvailableBytes,
    CxlMemoryPersistence Persistence,
    int ProximityClass,
    int LatencyClass,
    int BandwidthClass);

public sealed record CxlCoherentBinding(
    CxlCoherentBindingId BindingId,
    CxlCoherentBindingGeneration Generation,
    CxlFabricBinding FabricBinding,
    RegionUseHandle RegionUse);

public interface ICxlDiscoveryProvider
{
    PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpointId);
}

/// <summary>Projects CXL.io discovery into the ordinary semantic device identity.</summary>
public interface ICxlIoProvider
{
    PlatformAuthorityResult<PlatformDeviceIdentity> ResolveDevice(
        CxlEndpointId endpointId,
        CxlDeviceGeneration expectedGeneration);
}

public interface ICxlFabricProvider
{
    /// <summary>
    /// A failed creation returns NotAccepted only when no provider effect occurred.
    /// Unavailable means acceptance may be ambiguous and requires caller quarantine.
    /// Implementations must report failures as results and must not throw.
    /// </summary>
    PlatformAuthorityResult<CxlFabricBinding> Bind(CxlFabricBindingRequest request);
    PlatformAuthorityResult<CxlFabricBinding> Query(CxlFabricBindingId bindingId);
    PlatformAuthorityResult Unbind(CxlFabricBinding binding);
}

public interface ICxlMemoryProvider
{
    PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId endpointId);
    /// <summary>
    /// A failed creation returns NotAccepted only when no provider effect occurred.
    /// Unavailable means acceptance may be ambiguous and requires caller quarantine.
    /// Implementations must report failures as results and must not throw.
    /// </summary>
    PlatformAuthorityResult<CxlMemoryBinding> BindMemory(
        CxlFabricBinding fabricBinding,
        RegionBackingLeaseDescriptor backingLease);
    PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId bindingId);
    PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding);
}

public interface ICxlCoherentAccessProvider
{
    /// <summary>
    /// A failed creation returns NotAccepted only when no provider effect occurred.
    /// Unavailable means acceptance may be ambiguous and requires caller quarantine.
    /// Implementations must report failures as results and must not throw.
    /// </summary>
    PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(
        CxlFabricBinding fabricBinding,
        RegionUseDescriptor regionUse);
    PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId bindingId);
    PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding);
}

/// <summary>Supplies evidence only; it does not authorize discovery, memory, or coherent access.</summary>
public interface ICxlSecurityEvidenceProvider
{
    PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(
        CxlEndpointId endpointId,
        CxlDeviceGeneration expectedGeneration);
}
