namespace YAKSys_Hybrid_CPU.Core.Authority;

public readonly record struct NeutralOperationHandle(ulong Value);
public readonly record struct NeutralOperationGeneration(ulong Value);
public readonly record struct NeutralOperationIdentity(
    NeutralOperationHandle Handle,
    NeutralOperationGeneration Generation);

public enum NeutralLateResultDisposition
{
    Applied = 0,
    RejectedDifferentGeneration,
    RejectedUnknownOperation,
    QuarantinedUnknownOutcome,
}

public sealed class OperationReservationRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<NeutralOperationHandle, NeutralOperationGeneration> _active = [];
    private ulong _nextHandle = 1;

    public NeutralOperationIdentity Reserve()
    {
        lock (_gate)
        {
            if (_nextHandle == 0 || _nextHandle == ulong.MaxValue)
                throw new InvalidOperationException("Operation identity space is exhausted.");

            var identity = new NeutralOperationIdentity(
                new NeutralOperationHandle(_nextHandle++),
                new NeutralOperationGeneration(1));
            _active.Add(identity.Handle, identity.Generation);
            return identity;
        }
    }

    public NeutralLateResultDisposition Reconcile(NeutralOperationIdentity identity, bool outcomeKnown)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(identity.Handle, out var generation))
                return NeutralLateResultDisposition.RejectedUnknownOperation;
            if (generation != identity.Generation)
                return NeutralLateResultDisposition.RejectedDifferentGeneration;
            if (!outcomeKnown)
                return NeutralLateResultDisposition.QuarantinedUnknownOutcome;

            _active.Remove(identity.Handle);
            return NeutralLateResultDisposition.Applied;
        }
    }

    public bool IsReserved(NeutralOperationIdentity identity)
    {
        lock (_gate)
            return _active.TryGetValue(identity.Handle, out var generation) && generation == identity.Generation;
    }
}
