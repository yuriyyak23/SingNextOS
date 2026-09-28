using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum V6RemoteLeaseSimulationPhase : byte
{
    Issued = 1,
    PossibleEffect = 2,
    Quarantined = 3,
    Fenced = 4,
    EffectClosed = 5,
    Published = 6,
    Reclaimed = 7,
    Stale = 8,
    Revoked = 9,
}

internal sealed record V6RemoteLeaseSimulationState(
    RemoteAuthorityLeaseV1 Lease,
    RemoteLeaseCurrentStateV1 Current,
    V6RemoteLeaseSimulationPhase Phase,
    uint SubmitCount,
    uint PublishCount,
    bool PossibleEffect,
    bool Fenced,
    bool EffectClosed,
    bool Quarantined,
    bool Reclaimed,
    ulong ProviderGeneration,
    RemoteResourceEscrowV1 Escrow)
{
    internal bool HasSingleLogicalOwner => Lease.OwnerHostIdentity != Lease.RemoteHostIdentity;
}

/// <summary>Bounded deterministic safety simulator. It is not a distributed runtime implementation.</summary>
internal static class V6RemoteLeaseFaultSimulator
{
    internal static V6RemoteLeaseSimulationState Initial(
        RemoteAuthorityLeaseV1 lease, ulong providerGeneration, RemoteResourceEscrowV1 escrow) =>
        new(lease.Validate(), new(lease.OwnerIncarnation, lease.RemoteIncarnation, lease.OwnerEpoch,
                lease.LeaseGeneration, lease.IssuedAtOwnerSequence, false, false),
            V6RemoteLeaseSimulationPhase.Issued, 0, 0, false, false, false, false, false,
            providerGeneration != 0 ? providerGeneration : throw new ArgumentException("Provider generation is required."),
            escrow.Validate());

    internal static KernelResult<V6RemoteLeaseSimulationState> Submit(V6RemoteLeaseSimulationState state)
    {
        if (state.Phase != V6RemoteLeaseSimulationPhase.Issued || state.SubmitCount != 0 ||
            RemoteAuthorityLeaseEvaluatorV1.Evaluate(state.Lease, state.Current) != RemoteLeaseAdmissionCodeV1.Eligible)
            return Deny("Remote submit requires one fresh exact issued lease.");
        return Ok(state with { Phase = V6RemoteLeaseSimulationPhase.PossibleEffect,
            SubmitCount = 1, PossibleEffect = true });
    }

    internal static KernelResult<V6RemoteLeaseSimulationState> Partition(V6RemoteLeaseSimulationState state) =>
        Ok(state with
        {
            Current = state.Current with { Partitioned = true },
            Phase = state.PossibleEffect ? V6RemoteLeaseSimulationPhase.Quarantined : state.Phase,
            Quarantined = state.PossibleEffect || state.Quarantined,
        });

    internal static KernelResult<V6RemoteLeaseSimulationState> Revoke(V6RemoteLeaseSimulationState state) =>
        Ok(state with
        {
            Current = state.Current with { Revoked = true },
            Phase = state.PossibleEffect ? V6RemoteLeaseSimulationPhase.Quarantined : V6RemoteLeaseSimulationPhase.Revoked,
            Quarantined = state.PossibleEffect || state.Quarantined,
        });

    internal static KernelResult<V6RemoteLeaseSimulationState> OwnerReboot(V6RemoteLeaseSimulationState state) =>
        Ok(state with
        {
            Current = state.Current with
            { OwnerIncarnation = state.Current.OwnerIncarnation + 1, OwnerEpoch = state.Current.OwnerEpoch + 1 },
            Phase = state.PossibleEffect ? V6RemoteLeaseSimulationPhase.Quarantined : V6RemoteLeaseSimulationPhase.Stale,
            Quarantined = state.PossibleEffect || state.Quarantined,
        });

    internal static KernelResult<V6RemoteLeaseSimulationState> RemoteReboot(V6RemoteLeaseSimulationState state) =>
        Ok(state with
        {
            Current = state.Current with { RemoteIncarnation = state.Current.RemoteIncarnation + 1 },
            Phase = state.PossibleEffect ? V6RemoteLeaseSimulationPhase.Quarantined : V6RemoteLeaseSimulationPhase.Stale,
            Quarantined = state.PossibleEffect || state.Quarantined,
        });

    internal static KernelResult<V6RemoteLeaseSimulationState> Expire(V6RemoteLeaseSimulationState state) =>
        Ok(state with
        {
            Current = state.Current with { OwnerSequence = state.Lease.NotAfterOwnerSequence + 1 },
            Phase = state.PossibleEffect ? V6RemoteLeaseSimulationPhase.Quarantined : V6RemoteLeaseSimulationPhase.Stale,
            Quarantined = state.PossibleEffect || state.Quarantined,
        });

    internal static KernelResult<V6RemoteLeaseSimulationState> Renew(V6RemoteLeaseSimulationState state)
    {
        if (state.PossibleEffect || state.Current.Partitioned || state.Current.Revoked ||
            state.Phase != V6RemoteLeaseSimulationPhase.Issued)
            return Deny("Renewal cannot race a possible effect, partition, or revocation.");
        var nextGeneration = state.Lease.LeaseGeneration + 1;
        var nextSequence = state.Current.OwnerSequence + 1;
        var renewed = state.Lease with
        {
            LeaseGeneration = nextGeneration,
            IssuedAtOwnerSequence = nextSequence,
            NotAfterOwnerSequence = Math.Max(state.Lease.NotAfterOwnerSequence + 1, nextSequence),
        };
        return Ok(state with { Lease = renewed, Current = state.Current with
            { LeaseGeneration = nextGeneration, OwnerSequence = nextSequence } });
    }

    internal static KernelResult<V6RemoteLeaseSimulationState> ProviderLoss(V6RemoteLeaseSimulationState state) =>
        state.PossibleEffect
            ? Ok(state with { Phase = V6RemoteLeaseSimulationPhase.Quarantined, Quarantined = true })
            : Ok(state with { Phase = V6RemoteLeaseSimulationPhase.Stale });

    internal static KernelResult<V6RemoteLeaseSimulationState> FabricReconfigure(V6RemoteLeaseSimulationState state) =>
        Ok(state with
        {
            ProviderGeneration = state.ProviderGeneration + 1,
            Phase = state.PossibleEffect ? V6RemoteLeaseSimulationPhase.Quarantined : V6RemoteLeaseSimulationPhase.Stale,
            Quarantined = state.PossibleEffect || state.Quarantined,
        });

    internal static KernelResult<V6RemoteLeaseSimulationState> Fence(V6RemoteLeaseSimulationState state)
    {
        if (!state.PossibleEffect || state.Fenced)
            return Deny("Only one fence may close a possible remote effect.");
        return Ok(state with { Phase = V6RemoteLeaseSimulationPhase.Fenced, Fenced = true });
    }

    internal static KernelResult<V6RemoteLeaseSimulationState> ObserveEffectClosure(V6RemoteLeaseSimulationState state)
    {
        if (!state.Fenced || state.EffectClosed)
            return Deny("Effect closure requires a prior exact fence.");
        return Ok(state with { Phase = V6RemoteLeaseSimulationPhase.EffectClosed,
            EffectClosed = true, PossibleEffect = false, Quarantined = false });
    }

    internal static KernelResult<V6RemoteLeaseSimulationState> Publish(V6RemoteLeaseSimulationState state)
    {
        if (state.Phase != V6RemoteLeaseSimulationPhase.EffectClosed || state.PublishCount != 0 ||
            RemoteAuthorityLeaseEvaluatorV1.Evaluate(state.Lease, state.Current) != RemoteLeaseAdmissionCodeV1.Eligible)
            return Deny("Publication requires exact fresh lease state and explicit remote effect closure.");
        return Ok(state with { Phase = V6RemoteLeaseSimulationPhase.Published, PublishCount = 1 });
    }

    internal static KernelResult<V6RemoteLeaseSimulationState> Reclaim(V6RemoteLeaseSimulationState state)
    {
        var closure = new RemoteEffectClosureV1(1, state.Lease.LeaseId, state.Lease.OwnerEpoch,
            state.Lease.LeaseGeneration, state.ProviderGeneration, state.Current.OwnerSequence,
            state.Fenced, state.EffectClosed);
        if (state.Reclaimed || !RemoteReclaimPredicateV1.IsSatisfied(state.Lease, closure))
            return Deny("Reclaim requires an exact fence and observed effect closure.");
        var returned = state.Escrow.DelegatedAllocation - state.Escrow.ConsumedAllocation;
        var escrow = state.Escrow with { ReturnedAllocation = returned };
        return Ok(state with { Phase = V6RemoteLeaseSimulationPhase.Reclaimed,
            Reclaimed = true, Escrow = escrow.Validate() });
    }

    private static KernelResult<V6RemoteLeaseSimulationState> Ok(V6RemoteLeaseSimulationState state) =>
        KernelResult<V6RemoteLeaseSimulationState>.Ok(state);

    private static KernelResult<V6RemoteLeaseSimulationState> Deny(string message) =>
        KernelResult<V6RemoteLeaseSimulationState>.Fail(KernelError.PlatformDenied, message);
}
