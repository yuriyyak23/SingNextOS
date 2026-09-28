namespace SingPlus.Platform;

public enum PlatformVirtualTrapKind
{
    MemoryFault = 0,
    IllegalInstruction,
    Hypercall,
    ExternalEvent,
    Timer,
    Preemption,
    DeviceOrIoFault,
}

/// <summary>
/// Semantic trap evidence bound to one exact child lease. This value is neither
/// a capability nor completion or closure authority.
/// </summary>
public readonly record struct PlatformVirtualTrapEvidence(
    PlatformProviderChildDomainLeaseId ChildLeaseId,
    PlatformProviderLeaseGeneration ChildGeneration,
    PlatformProviderDomainLeaseId ParentLeaseId,
    PlatformProviderLeaseGeneration ParentGeneration,
    ulong Sequence,
    PlatformVirtualTrapKind Kind);

public static class PlatformVirtualTrapContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateObservationRequest(
        PlatformProviderChildDomainLease childLease,
        PlatformChildDomainState state = PlatformChildDomainState.Running)
    {
        var child = PlatformChildDomainContract.ValidateLease(childLease.ParentDomainLease, childLease.Intent, childLease);
        if (!child.IsSuccess) return child;
        var effect = PlatformChildDomainContract.ValidateEffectState(state);
        if (!effect.IsSuccess) return effect;
        if ((childLease.Intent.Authority.ChildAuthority & PlatformChildAuthorityClass.Traps) == 0)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "The child has no neutral-trap observation authority.");
        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateEvidence(
        PlatformProviderChildDomainLease expectedChild,
        PlatformVirtualTrapEvidence evidence)
    {
        var request = ValidateObservationRequest(expectedChild);
        if (!request.IsSuccess) return request;
        if (evidence.ChildLeaseId != expectedChild.LeaseId)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "Trap evidence belongs to another child.");
        if (evidence.ChildGeneration != expectedChild.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Trap evidence belongs to another child generation.");
        if (evidence.ParentLeaseId != expectedChild.ParentDomainLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Trap evidence belongs to another parent.");
        if (evidence.ParentGeneration != expectedChild.ParentDomainLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Trap evidence belongs to another parent generation.");
        if (evidence.Sequence == 0 || !Enum.IsDefined(evidence.Kind))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Trap evidence is empty or has an unknown semantic kind.");
        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformVirtualTrapProvider
{
    PlatformAuthorityResult<PlatformVirtualTrapEvidence> ObserveVirtualTrap(
        PlatformProviderChildDomainLease childLease);
}
