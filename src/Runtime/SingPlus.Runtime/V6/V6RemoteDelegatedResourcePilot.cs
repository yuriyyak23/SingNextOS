using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum V6RemoteDelegatedResourcePilotState : byte
{
    Issued = 1,
    Submitted = 2,
    Fenced = 3,
    ProviderLost = 4,
    EffectClosed = 5,
    Published = 6,
    Reclaimed = 7,
}

internal sealed record V6RemoteDelegatedResourcePilotSnapshot(
    RemoteAuthorityLeaseV1 Lease,
    RemoteLeaseCurrentStateV1 Current,
    RemoteResourceEscrowV1 Escrow,
    ulong ProviderGeneration,
    V6RemoteDelegatedResourcePilotState State,
    RemoteLeaseRightsV1 SubmittedRights,
    uint SubmitCount,
    uint PublishCount,
    bool ProviderLost)
{
    public bool HasSingleLogicalOwner => true;
    public bool TransfersParentAuthority => false;
}

/// <summary>
/// In-process P11-C protocol pilot. It has no transport or production registration and cannot
/// bypass the rollout gate. The qualification factory exists only for bounded managed evidence.
/// </summary>
internal sealed class V6RemoteDelegatedResourcePilot
{
    private const string GateName = "V6-MULTIHOST-LEASES";
    private readonly object _sync = new();
    private readonly RemoteAuthorityLeaseV1 _lease;
    private RemoteLeaseCurrentStateV1 _current;
    private RemoteResourceEscrowV1 _escrow;
    private readonly ulong _providerGeneration;
    private V6RemoteDelegatedResourcePilotState _state;
    private RemoteLeaseRightsV1 _submittedRights;
    private RemoteEffectClosureV1? _closure;
    private uint _submitCount;
    private uint _publishCount;
    private bool _providerLost;

    private V6RemoteDelegatedResourcePilot(
        RemoteAuthorityLeaseV1 lease,
        RemoteLeaseCurrentStateV1 current,
        RemoteResourceEscrowV1 escrow,
        ulong providerGeneration)
    {
        _lease = lease;
        _current = current;
        _escrow = escrow;
        _providerGeneration = providerGeneration;
        _state = V6RemoteDelegatedResourcePilotState.Issued;
    }

    internal static KernelResult<V6RemoteDelegatedResourcePilot> Create(
        RemoteAuthorityLeaseV1 lease,
        RemoteLeaseCurrentStateV1 current,
        RemoteResourceEscrowV1 escrow,
        ulong providerGeneration)
    {
        if (!V6FeatureGates.IsEnabled(GateName))
            return KernelResult<V6RemoteDelegatedResourcePilot>.Fail(
                KernelError.PlatformUnsupported, $"{GateName} is disabled.");
        return CreateCore(lease, current, escrow, providerGeneration);
    }

    internal static KernelResult<V6RemoteDelegatedResourcePilot> CreateForQualification(
        RemoteAuthorityLeaseV1 lease,
        RemoteLeaseCurrentStateV1 current,
        RemoteResourceEscrowV1 escrow,
        ulong providerGeneration) => CreateCore(lease, current, escrow, providerGeneration);

    internal KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Submit(
        RemoteAuthorityLeaseV1 presentedLease,
        RemoteLeaseRightsV1 requestedRights,
        ulong allocation)
    {
        lock (_sync)
        {
            if (_state != V6RemoteDelegatedResourcePilotState.Issued)
                return Fail(KernelError.InvalidTransition, "The delegated resource already has a submit decision.");
            var admission = Admit(presentedLease);
            if (!admission.IsSuccess) return admission;
            if (requestedRights == RemoteLeaseRightsV1.None || (requestedRights & ~_lease.Rights) != 0)
                return Fail(KernelError.InsufficientRights, "Requested remote rights are not a non-empty lease subset.");
            if (allocation == 0 || allocation > _escrow.DelegatedAllocation - _escrow.ConsumedAllocation)
                return Fail(KernelError.BudgetExceeded, "Remote allocation exceeds the delegated escrow.");

            _escrow = (_escrow with
            {
                ConsumedAllocation = checked(_escrow.ConsumedAllocation + allocation),
            }).Validate();
            _submittedRights = requestedRights;
            _submitCount = 1;
            _state = V6RemoteDelegatedResourcePilotState.Submitted;
            return Ok();
        }
    }

    internal KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Fence(RemoteAuthorityLeaseV1 presentedLease)
    {
        lock (_sync)
        {
            if (presentedLease != _lease)
                return Fail(KernelError.StaleGeneration, "The fence does not identify the exact issued lease.");
            if (_state is not (V6RemoteDelegatedResourcePilotState.Issued or
                V6RemoteDelegatedResourcePilotState.Submitted))
                return Fail(KernelError.InvalidTransition, "The delegated resource cannot be fenced in its current state.");
            _state = V6RemoteDelegatedResourcePilotState.Fenced;
            return Ok();
        }
    }

    internal KernelResult<V6RemoteDelegatedResourcePilotSnapshot> ObserveEffectClosure(
        RemoteEffectClosureV1 closure)
    {
        lock (_sync)
        {
            if (_state is not (V6RemoteDelegatedResourcePilotState.Fenced or
                V6RemoteDelegatedResourcePilotState.ProviderLost))
                return Fail(KernelError.InvalidTransition, "The lease must be fenced before effect closure.");
            if (closure.ProviderGeneration != _providerGeneration ||
                !RemoteReclaimPredicateV1.IsSatisfied(_lease, closure))
                return Fail(KernelError.ExternalEffectUncontained, "Remote effect closure does not match the fenced lease and provider generation.");
            _closure = closure;
            _state = V6RemoteDelegatedResourcePilotState.EffectClosed;
            return Ok();
        }
    }

    internal KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Publish(
        RemoteAuthorityLeaseV1 presentedLease)
    {
        lock (_sync)
        {
            if (_state != V6RemoteDelegatedResourcePilotState.EffectClosed)
                return Fail(KernelError.InvalidTransition, "Publication requires exact remote effect closure.");
            if (_providerLost)
                return Fail(KernelError.PlatformUnavailable,
                    "A staged result from the lost provider cannot be published after recovery closure.");
            var admission = Admit(presentedLease);
            if (!admission.IsSuccess) return admission;
            if ((_submittedRights & RemoteLeaseRightsV1.StagedWrite) == 0)
                return Fail(KernelError.InsufficientRights, "The admitted submit did not include staged-write publication rights.");
            _publishCount = 1;
            _state = V6RemoteDelegatedResourcePilotState.Published;
            return Ok();
        }
    }

    internal KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Reclaim(RemoteEffectClosureV1 closure)
    {
        lock (_sync)
        {
            if (_state is not (V6RemoteDelegatedResourcePilotState.EffectClosed or
                V6RemoteDelegatedResourcePilotState.Published))
                return Fail(KernelError.InvalidTransition, "Reclaim requires a closed remote effect.");
            if (_closure is null || closure != _closure.Value ||
                closure.ProviderGeneration != _providerGeneration ||
                !RemoteReclaimPredicateV1.IsSatisfied(_lease, closure))
                return Fail(KernelError.ExternalEffectUncontained, "Reclaim closure is stale or does not identify this pilot resource.");

            _escrow = (_escrow with
            {
                ReturnedAllocation = _escrow.DelegatedAllocation - _escrow.ConsumedAllocation,
            }).Validate();
            _state = V6RemoteDelegatedResourcePilotState.Reclaimed;
            return Ok();
        }
    }

    internal KernelResult<V6RemoteDelegatedResourcePilotSnapshot> RecordProviderLoss(
        RemoteAuthorityLeaseV1 presentedLease)
    {
        lock (_sync)
        {
            if (presentedLease != _lease)
                return Fail(KernelError.StaleGeneration,
                    "Provider loss does not identify the exact issued lease.");
            if (_state is not (V6RemoteDelegatedResourcePilotState.Issued or
                V6RemoteDelegatedResourcePilotState.Submitted or
                V6RemoteDelegatedResourcePilotState.Fenced))
                return Fail(KernelError.InvalidTransition,
                    "Provider loss is not relevant in the current delegated-resource state.");
            _providerLost = true;
            _state = V6RemoteDelegatedResourcePilotState.ProviderLost;
            return Ok();
        }
    }

    internal V6RemoteDelegatedResourcePilotSnapshot Partition()
    {
        lock (_sync)
        {
            _current = _current with { Partitioned = true };
            return Snapshot();
        }
    }

    internal V6RemoteDelegatedResourcePilotSnapshot Revoke()
    {
        lock (_sync)
        {
            _current = _current with { Revoked = true };
            return Snapshot();
        }
    }

    internal V6RemoteDelegatedResourcePilotSnapshot AdvanceOwnerSequence(ulong ownerSequence)
    {
        lock (_sync)
        {
            if (ownerSequence <= _current.OwnerSequence)
                throw new ArgumentOutOfRangeException(nameof(ownerSequence), "Owner sequence must advance monotonically.");
            _current = _current with { OwnerSequence = ownerSequence };
            return Snapshot();
        }
    }

    internal V6RemoteDelegatedResourcePilotSnapshot RebootOwner(ulong ownerIncarnation, ulong ownerEpoch)
    {
        lock (_sync)
        {
            if (ownerIncarnation <= _current.OwnerIncarnation || ownerEpoch <= _current.OwnerEpoch)
                throw new ArgumentOutOfRangeException(nameof(ownerIncarnation), "Owner reboot must advance incarnation and epoch.");
            _current = _current with { OwnerIncarnation = ownerIncarnation, OwnerEpoch = ownerEpoch };
            return Snapshot();
        }
    }

    internal V6RemoteDelegatedResourcePilotSnapshot RebootRemote(ulong remoteIncarnation)
    {
        lock (_sync)
        {
            if (remoteIncarnation <= _current.RemoteIncarnation)
                throw new ArgumentOutOfRangeException(nameof(remoteIncarnation), "Remote reboot must advance incarnation.");
            _current = _current with { RemoteIncarnation = remoteIncarnation };
            return Snapshot();
        }
    }

    internal V6RemoteDelegatedResourcePilotSnapshot Query()
    {
        lock (_sync) return Snapshot();
    }

    private static KernelResult<V6RemoteDelegatedResourcePilot> CreateCore(
        RemoteAuthorityLeaseV1 lease,
        RemoteLeaseCurrentStateV1 current,
        RemoteResourceEscrowV1 escrow,
        ulong providerGeneration)
    {
        try
        {
            lease.Validate();
            escrow.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return KernelResult<V6RemoteDelegatedResourcePilot>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        if (providerGeneration == 0 || escrow.ConsumedAllocation != 0 || escrow.ReturnedAllocation != 0)
            return KernelResult<V6RemoteDelegatedResourcePilot>.Fail(
                KernelError.InvalidMessage, "A pilot starts with a live provider generation and untouched escrow.");
        var admission = RemoteAuthorityLeaseEvaluatorV1.Evaluate(lease, current);
        if (admission != RemoteLeaseAdmissionCodeV1.Eligible)
            return KernelResult<V6RemoteDelegatedResourcePilot>.Fail(
                MapAdmission(admission), $"Initial lease admission failed: {admission}.");
        return KernelResult<V6RemoteDelegatedResourcePilot>.Ok(
            new V6RemoteDelegatedResourcePilot(lease, current, escrow, providerGeneration));
    }

    private KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Admit(RemoteAuthorityLeaseV1 presentedLease)
    {
        if (presentedLease != _lease)
            return Fail(KernelError.StaleGeneration, "The presented lease is not the exact issued delegation.");
        var decision = RemoteAuthorityLeaseEvaluatorV1.Evaluate(presentedLease, _current);
        return decision == RemoteLeaseAdmissionCodeV1.Eligible
            ? Ok()
            : Fail(MapAdmission(decision), $"Remote lease admission failed: {decision}.");
    }

    private static KernelError MapAdmission(RemoteLeaseAdmissionCodeV1 code) => code switch
    {
        RemoteLeaseAdmissionCodeV1.Partitioned => KernelError.DependencyUnavailable,
        RemoteLeaseAdmissionCodeV1.Revoked => KernelError.PlatformBindingRevoked,
        _ => KernelError.StaleGeneration,
    };

    private KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Ok() =>
        KernelResult<V6RemoteDelegatedResourcePilotSnapshot>.Ok(Snapshot());

    private static KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Fail(
        KernelError error, string message) =>
        KernelResult<V6RemoteDelegatedResourcePilotSnapshot>.Fail(error, message);

    private V6RemoteDelegatedResourcePilotSnapshot Snapshot() => new(
        _lease, _current, _escrow, _providerGeneration, _state,
        _submittedRights, _submitCount, _publishCount, _providerLost);
}
