namespace SingPlus.Platform;

/// <summary>
/// Provider-neutral admission for a child whose immediate parent is another
/// live child. The returned child lease remains provider-private above the
/// Platform Authority Bridge.
/// </summary>
public readonly record struct PlatformNestedDomainRequest(
    PlatformProviderChildDomainLease ParentChildLease,
    PlatformChildDomainIntent ChildIntent);

/// <summary>Admission evidence only; the bridge retains it as private provider state.</summary>
public readonly record struct PlatformProviderNestedDomainLease(
    PlatformProviderChildDomainLease ChildLease,
    PlatformProviderChildDomainLeaseId ImmediateParentLeaseId,
    PlatformProviderLeaseGeneration ImmediateParentGeneration);

public static class PlatformNestedDomainContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformNestedDomainRequest request,
        PlatformChildDomainState parentState = PlatformChildDomainState.Created)
    {
        var parent = PlatformChildDomainContract.ValidateLease(
            request.ParentChildLease.ParentDomainLease,
            request.ParentChildLease.Intent,
            request.ParentChildLease);
        if (!parent.IsSuccess) return parent;

        var state = PlatformChildDomainContract.ValidateEffectState(parentState);
        if (!state.IsSuccess) return state;

        var child = PlatformChildDomainContract.ValidateCreateRequest(
            request.ParentChildLease.ParentDomainLease,
            request.ChildIntent);
        if (!child.IsSuccess) return child;

        var admittedParent = request.ParentChildLease.Intent.Authority.ChildAuthority;
        if ((request.ChildIntent.Authority.ParentAuthority & admittedParent) !=
            request.ChildIntent.Authority.ParentAuthority)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Nested parent authority cannot exceed the exact live parent-child authority.");
        }

        if ((request.ChildIntent.Authority.ChildAuthority & admittedParent) !=
            request.ChildIntent.Authority.ChildAuthority)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Nested child authority cannot amplify its immediate parent's authority.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateLease(
        PlatformNestedDomainRequest request,
        PlatformProviderNestedDomainLease nestedLease)
    {
        var valid = ValidateRequest(request);
        if (!valid.IsSuccess) return valid;

        var lease = nestedLease.ChildLease;

        if (nestedLease.ImmediateParentLeaseId != request.ParentChildLease.LeaseId)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "Nested provider evidence identifies another immediate parent child.");
        if (nestedLease.ImmediateParentGeneration != request.ParentChildLease.Generation)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "Nested provider evidence identifies a stale immediate parent generation.");

        if (lease.LeaseId.Value == 0 || lease.Generation.Value == 0)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Nested provider child identity and epoch must be materialized.");

        if (lease.ParentDomainLease != request.ParentChildLease.ParentDomainLease)
            return PlatformAuthorityResult.Fail(
                lease.ParentDomainLease.Generation != request.ParentChildLease.ParentDomainLease.Generation
                    ? PlatformAuthorityStatus.Stale
                    : PlatformAuthorityStatus.WrongDomain,
                "Nested child lease belongs to another root parent domain.");

        if (lease.Intent != request.ChildIntent)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Nested child lease does not preserve the exact admitted authority subset.");

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformNestedDomainProvider
{
    PlatformAuthorityResult<PlatformProviderNestedDomainLease> CreateNestedChildDomain(
        PlatformNestedDomainRequest request);
}
