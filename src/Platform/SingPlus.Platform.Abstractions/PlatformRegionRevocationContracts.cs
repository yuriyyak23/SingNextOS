namespace SingPlus.Platform;

public readonly record struct PlatformRegionRevocationTicket(
    PlatformProviderRegionMappingId MappingId,
    PlatformProviderLeaseGeneration MappingGeneration,
    PlatformOperationIdentity Operation);

public static class PlatformRegionRevocationContract
{
    public static PlatformAuthorityResult ValidateTicket(
        PlatformProviderRegionMappingLease expectedMapping,
        PlatformRegionRevocationTicket ticket)
    {
        if (expectedMapping.MappingId.Value == 0 ||
            expectedMapping.Generation.Value == 0 ||
            ticket.MappingId.Value == 0 ||
            ticket.MappingGeneration.Value == 0 ||
            ticket.Operation.OperationId.Value == 0 ||
            ticket.Operation.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-revocation tickets must use non-zero mapping and operation identities.");
        }

        if (ticket.MappingId != expectedMapping.MappingId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The region-revocation ticket belongs to a different provider mapping.");
        }

        if (ticket.MappingGeneration != expectedMapping.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The region-revocation ticket mapping generation is stale.");
        }

        if (ticket.Operation.DomainLease.LeaseId != expectedMapping.DomainLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The region-revocation operation belongs to a different provider domain lease.");
        }

        if (ticket.Operation.DomainLease.Generation != expectedMapping.DomainLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The region-revocation operation provider-domain generation is stale.");
        }

        if (ticket.Operation.DomainLease.Subject != expectedMapping.DomainLease.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The region-revocation operation subject does not match the provider mapping.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformRegionRevocationProvider : IPlatformCompletionProvider
{
    PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy);
}

/// <summary>Optional guarantee: every Begin overload returns NotAccepted only
/// with proven zero effect of that invocation and a default ticket. This permits
/// no-effect intent consumption, never closure or quantitative release. V1
/// providers without this guarantee retain conservative unknown-outcome semantics.</summary>
public interface IPlatformRegionRevocationNoEffectProvider : IPlatformRegionRevocationProvider { }

public readonly record struct PlatformRegionRevocationRequest(
    Guid InvocationId,
    PlatformProviderRegionMappingLease Mapping,
    PlatformRegionRevocationPolicy Policy);

public readonly record struct PlatformRegionRevocationResolution(
    PlatformRegionRevocationRequest Request,
    PlatformRegionRevocationTicket Ticket);

/// <summary>
/// Optional surviving-original response resolution. Resolve never submits or
/// repeats a revoke and never treats absence as closure. Success requires the
/// exact original request and retained, proven closure at its original owner.
/// Unknown underlying outcomes and lost original owners remain unresolved.
/// </summary>
public interface IPlatformRegionRevocationResolutionProvider : IPlatformRegionRevocationProvider
{
    PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
        PlatformRegionRevocationRequest request);

    PlatformAuthorityResult<PlatformRegionRevocationResolution> ResolveRegionMappingRevocation(
        PlatformRegionRevocationRequest request);
}
