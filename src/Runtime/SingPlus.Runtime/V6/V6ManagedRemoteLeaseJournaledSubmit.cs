using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// One-shot managed qualification seam. The owner journal records possible effect
/// before transport delivery. Durability depends on the supplied store; this grants
/// no remote authority.
/// </summary>
internal sealed class V6ManagedRemoteLeaseJournaledSubmit
{
    private readonly V6RemoteLeaseRecoveryJournal journal;
    private readonly V6ManagedRemoteLeaseTransport transport;
    private readonly ulong providerGeneration;
    private readonly object _sync = new();
    private string? _submittedCommandDigest;
    private bool _appendInFlight;
    private bool _appendFaulted;

    internal V6ManagedRemoteLeaseJournaledSubmit(V6RemoteLeaseRecoveryJournal journal,
        V6ManagedRemoteLeaseTransport transport, ulong providerGeneration)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.providerGeneration = providerGeneration;
        transport.BindJournaledSubmit(this);
    }

    internal KernelResult<V6RemoteLeaseTransportReceipt> SendSubmit(V6RemoteLeaseCommand command)
    {
        if (providerGeneration == 0 || command.Kind != V6RemoteLeaseCommandKind.Submit)
            return Fail(KernelError.InvalidMessage, "Journaled submit requires one exact submit and provider generation.");
        byte[] wire;
        try { wire = V6RemoteLeaseWireCodec.Encode(command); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return Fail(KernelError.InvalidMessage, exception.Message); }
        var digest = V6RemoteLeaseWireCodec.Digest(wire);
        var retry = false;
        lock (_sync)
        {
            if (_submittedCommandDigest is { } existing)
            {
                if (existing != digest)
                    return Fail(KernelError.DuplicateIdentity,
                        "A journaled submit cannot change its exact command.");
                retry = true;
            }
            else if (_appendInFlight || _appendFaulted)
                return Fail(KernelError.DependencyUnavailable,
                    "Journal append is in flight or ambiguous; no new submit may be delivered.");
            else _appendInFlight = true;
        }
        if (retry) return SendIfJournalCurrent(command);

        try
        {
            var recovered = journal.Replay();
            var item = recovered.Items.SingleOrDefault(value =>
                value.LastPayload.Lease?.LeaseId == command.Lease.LeaseId);
            if (item is null || item.LastPayload.Transition != V6RemoteLeaseRecoveryTransition.Issued ||
                item.LastPayload.Lease != command.Lease ||
                item.LastPayload.ProviderGeneration != providerGeneration ||
                recovered.OwnerIncarnationHighWatermark != command.Lease.OwnerIncarnation ||
                recovered.OwnerEpochHighWatermark != command.Lease.OwnerEpoch ||
                command.Allocation > item.LastPayload.Escrow.DelegatedAllocation)
                return Fail(KernelError.StaleGeneration,
                    "The exact issued owner journal tuple is unavailable before remote submit.");
            journal.Append(item.LastPayload with
            {
                Transition = V6RemoteLeaseRecoveryTransition.Submitted,
                Escrow = item.LastPayload.Escrow with { ConsumedAllocation = command.Allocation },
            });
            lock (_sync) _submittedCommandDigest = digest;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                           ArgumentException or InvalidOperationException)
        {
            lock (_sync) _appendFaulted = true;
            return Fail(KernelError.DependencyUnavailable,
                $"Owner journal did not confirm write-ahead submit: {exception.Message}");
        }
        finally { lock (_sync) _appendInFlight = false; }

        return SendIfJournalCurrent(command);
    }

    private KernelResult<V6RemoteLeaseTransportReceipt> SendIfJournalCurrent(
        V6RemoteLeaseCommand command)
    {
        try
        {
            var recovered = journal.Replay();
            var item = recovered.Items.SingleOrDefault(value =>
                value.LastPayload.Lease?.LeaseId == command.Lease.LeaseId);
            if (recovered.OwnerIncarnationHighWatermark != command.Lease.OwnerIncarnation ||
                recovered.OwnerEpochHighWatermark != command.Lease.OwnerEpoch ||
                item?.LastPayload.Transition != V6RemoteLeaseRecoveryTransition.Submitted ||
                item.LastPayload.Lease != command.Lease ||
                item.LastPayload.ProviderGeneration != providerGeneration ||
                item.LastPayload.Escrow.ConsumedAllocation != command.Allocation)
                return Fail(KernelError.StaleGeneration,
                    "Owner journal generation or exact submitted command changed before delivery.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
        {
            return Fail(KernelError.DependencyUnavailable,
                $"Owner journal could not be revalidated before delivery: {exception.Message}");
        }
        return transport.SendJournaledSubmit(command, this);
    }

    private static KernelResult<V6RemoteLeaseTransportReceipt> Fail(KernelError error, string message) =>
        KernelResult<V6RemoteLeaseTransportReceipt>.Fail(error, message);
}
