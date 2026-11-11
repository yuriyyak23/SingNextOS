namespace SingPlus.Platform;

public enum CxlFabricResourceState { Available = 0, Bound, Draining, Faulted }
public readonly record struct CxlFabricPoolId(string Value);
public readonly record struct CxlFabricReconfigurationId(ulong Value);
public readonly record struct CxlPoolAssignmentId(ulong Value);

public sealed record CxlFabricResourceSnapshot(
    CxlFabricBinding Binding,
    CxlFabricResourceState State,
    long AssignedCapacityBytes);

public sealed record CxlFabricPoolSnapshot(
    CxlFabricPoolId PoolId,
    long TotalCapacityBytes,
    long AvailableCapacityBytes,
    ulong Generation);

public sealed record CxlFabricReconfigurationTicket(
    CxlFabricReconfigurationId ReconfigurationId,
    CxlFabricBinding PreviousBinding,
    CxlFabricBindingGeneration ReplacementGeneration);

public sealed record CxlPoolAssignment(
    CxlPoolAssignmentId AssignmentId,
    ulong Generation,
    CxlFabricPoolId PoolId,
    CxlEndpointId EndpointId,
    long CapacityBytes);

public sealed record CxlPeerAccessRequest(
    CxlEndpointId Initiator,
    CxlEndpointId Target,
    long Length,
    bool PlatformIsolationMaterialized);

public readonly record struct CxlPeerAccessEvidence(
    CxlEndpointId Initiator,
    CxlEndpointId Target,
    bool RouteSupported,
    ulong Generation);

public interface ICxlFabricManagementProvider
{
    // Effect-creating methods return NotAccepted only for proven zero-effect rejection.
    // Unavailable is acceptance-ambiguous and callers must quarantine dependent authority.
    // Implementations report failures as PlatformAuthorityResult and must not throw.
    PlatformAuthorityResult<CxlFabricResourceSnapshot> QueryResource(CxlFabricBindingId bindingId);
    PlatformAuthorityResult<CxlFabricReconfigurationTicket> BeginReconfiguration(CxlFabricBinding binding);
    PlatformAuthorityResult<CxlFabricBinding> CompleteReconfiguration(CxlFabricReconfigurationTicket ticket);
    PlatformAuthorityResult<CxlFabricPoolSnapshot> QueryPool(CxlFabricPoolId poolId);
    PlatformAuthorityResult<CxlPoolAssignment> AssignPoolCapacity(CxlFabricPoolId poolId, CxlEndpointId endpointId, long capacityBytes);
    PlatformAuthorityResult ReleasePoolCapacity(CxlPoolAssignment assignment);
    PlatformAuthorityResult<CxlPeerAccessEvidence> QueryPeerAccess(CxlPeerAccessRequest request);
}
