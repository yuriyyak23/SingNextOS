using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class ChannelRegistry
{
    private sealed class ChannelRecord
    {
        public required ChannelId Id { get; init; }
        public required ulong Generation { get; set; }
        public required ProtocolDefinitionV1 Protocol { get; init; }
        public required ProcessHandle LeftOwner { get; init; }
        public required DomainId LeftDomain { get; init; }
        public required ProcessHandle RightOwner { get; init; }
        public required DomainId RightDomain { get; init; }
        public required int Capacity { get; init; }
        public required string State { get; set; }
        public ulong Sequence { get; set; }
        public bool Closed { get; set; }
        public Queue<ChannelEnvelope> Queue { get; } = new();
    }

    private readonly CapabilityAuthority _capabilities;
    private readonly RegionAuthority _regions;
    private readonly Dictionary<ChannelId, ChannelRecord> _channels = [];
    private ulong _nextChannelId = 1;

    internal ChannelRegistry(CapabilityAuthority capabilities, RegionAuthority regions)
    {
        _capabilities = capabilities;
        _regions = regions;
    }

    internal (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Create(ProtocolDefinitionV1 protocol, SingProcess left, SingProcess right, int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var id = new ChannelId(_nextChannelId++);
        var record = new ChannelRecord
        {
            Id = id,
            Generation = 1,
            Protocol = protocol,
            LeftOwner = new ProcessHandle(left.ProcessId, left.Generation),
            LeftDomain = left.DomainId,
            RightOwner = new ProcessHandle(right.ProcessId, right.Generation),
            RightDomain = right.DomainId,
            Capacity = capacity,
            State = protocol.InitialState
        };
        _channels.Add(id, record);
        return (new ChannelEndpointHandle(id, new EndpointId(1), 1), new ChannelEndpointHandle(id, new EndpointId(2), 1));
    }

    public KernelResult<ChannelEndpoint> GetEndpoint(ChannelEndpointHandle handle)
    {
        var validation = Resolve(handle);
        if (!validation.IsSuccess) return KernelResult<ChannelEndpoint>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var owner = handle.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        var domain = handle.EndpointId.Value == 1 ? record.LeftDomain : record.RightDomain;
        return KernelResult<ChannelEndpoint>.Ok(new ChannelEndpoint(handle, domain, owner.Generation, record.State, record.Sequence, record.Capacity));
    }

    internal KernelResult<ChannelEnvelope> Send(SingProcess sender, SingProcess receiver, ChannelEndpointHandle endpoint, uint messageId, object? payload, IReadOnlyCollection<CapabilityId>? capabilityIds)
    {
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var expectedSender = endpoint.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        var expectedReceiver = endpoint.EndpointId.Value == 1 ? record.RightOwner : record.LeftOwner;
        if (expectedSender != new ProcessHandle(sender.ProcessId, sender.Generation)) return KernelResult<ChannelEnvelope>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the sender.");
        if (expectedReceiver != new ProcessHandle(receiver.ProcessId, receiver.Generation)) return KernelResult<ChannelEnvelope>.Fail(KernelError.WrongEndpointOwner, "Peer process does not own the opposite endpoint.");
        if (record.Queue.Count >= record.Capacity) return KernelResult<ChannelEnvelope>.Fail(KernelError.CapacityExhausted, "Channel capacity is exhausted.");
        if (!record.Protocol.TryGetMessage(messageId, out var message)) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidMessage, $"Message {messageId} is not part of the protocol.");
        if (!record.Protocol.TryTransition(record.State, messageId, out var transition)) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidProtocolTransition, $"Message {messageId} is illegal in state '{record.State}'.");
        var capabilityValidation = ValidateCapabilities(sender, message, capabilityIds);
        if (!capabilityValidation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(capabilityValidation.Error, capabilityValidation.Message!);
        if (!IsSupportedPayload(payload)) return KernelResult<ChannelEnvelope>.Fail(KernelError.UnsupportedPayload, "Payload must be primitive, enum, bounded payload, or owned region data.");
        if (message.Borrows.Count != 0 && message.Consumes.Count != 0) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidMessage, "A single payload cannot be both borrowed and consumed.");

        object? queuedPayload = payload;
        if (message.Borrows.Count != 0)
        {
            if (payload is not ITransferableOwnedPayload borrowed || !borrowed.IsValidForRuntime) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidRegionState, "Borrowing messages require a valid owned payload.");
            var owner = new RegionOwner(sender.DomainId, sender.Generation);
            var borrower = new RegionOwner(receiver.DomainId, receiver.Generation);
            var borrowValidation = _regions.Validate(borrowed.Handle, owner);
            if (!borrowValidation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(borrowValidation.Error, borrowValidation.Message!);
            var acquired = _regions.AcquireLoan(borrowed.Handle, owner, borrower);
            if (!acquired.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(acquired.Error, acquired.Message!);
            var grant = acquired.Value!;
            try
            {
                queuedPayload = borrowed.CreateBorrowLeaseForRuntime(grant.Handle, grant.Lifetime);
            }
            catch (InvalidOperationException exception)
            {
                _ = _regions.RevokeLoan(grant.Handle, owner);
                return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidRegionState, exception.Message);
            }
        }

        if (message.Consumes.Count != 0)
        {
            if (payload is not ITransferableOwnedPayload owned || !owned.IsValidForRuntime) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidRegionState, "Consuming messages require a valid owned payload.");
            var oldHandle = owned.Handle;
            var owner = new RegionOwner(sender.DomainId, sender.Generation);
            var regionValidation = _regions.Validate(oldHandle, owner);
            if (!regionValidation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(regionValidation.Error, regionValidation.Message!);
            var transfer = _regions.Transfer(oldHandle, owner, new RegionOwner(receiver.DomainId, receiver.Generation));
            if (!transfer.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(transfer.Error, transfer.Message!);
            queuedPayload = owned.TransferForRuntime(transfer.Value);
            _regions.ReplacePayload(oldHandle, transfer.Value, (ITransferableOwnedPayload)queuedPayload);
            sender.RemoveRegion(oldHandle);
            receiver.AddRegion(transfer.Value);
        }

        record.Sequence++;
        var envelope = new ChannelEnvelope(record.Sequence, messageId, queuedPayload);
        record.Queue.Enqueue(envelope);
        record.State = transition.ToState;
        return KernelResult<ChannelEnvelope>.Ok(envelope);
    }

    internal KernelResult<ChannelEnvelope> Receive(SingProcess receiver, ChannelEndpointHandle endpoint)
    {
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var owner = endpoint.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        if (owner != new ProcessHandle(receiver.ProcessId, receiver.Generation)) return KernelResult<ChannelEnvelope>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the receiver.");
        if (record.Queue.Count == 0) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidMessage, "Channel queue is empty.");
        return KernelResult<ChannelEnvelope>.Ok(record.Queue.Dequeue());
    }

    internal void CloseAllForDomain(DomainId domainId)
    {
        foreach (var record in _channels.Values.Where(r => !r.Closed && (r.LeftDomain == domainId || r.RightDomain == domainId)))
        {
            record.Closed = true;
            record.Generation++;
            record.Queue.Clear();
        }
    }

    private KernelResult<ChannelRecord> Resolve(ChannelEndpointHandle handle)
    {
        if (!_channels.TryGetValue(handle.ChannelId, out var record)) return KernelResult<ChannelRecord>.Fail(KernelError.ChannelNotFound, "Channel was not found.");
        if (record.Closed || record.Generation != handle.Generation) return KernelResult<ChannelRecord>.Fail(KernelError.StaleGeneration, "Channel endpoint generation is stale.");
        if (handle.EndpointId.Value is not 1 and not 2) return KernelResult<ChannelRecord>.Fail(KernelError.EndpointNotFound, "Endpoint id is invalid.");
        return KernelResult<ChannelRecord>.Ok(record);
    }

    private KernelResult ValidateCapabilities(SingProcess sender, ProtocolMessageDescriptorV1 message, IReadOnlyCollection<CapabilityId>? capabilityIds)
    {
        foreach (var requirement in message.RequiredCapabilities)
        {
            var found = false;
            foreach (var id in capabilityIds ?? Array.Empty<CapabilityId>())
            {
                var validation = _capabilities.Validate(id, sender.DomainId, sender.Generation, requirement.Rights);
                if (validation.IsSuccess && validation.Value!.ResourceKind == requirement.ResourceKind && string.Equals(validation.Value.ResourceId, requirement.ResourceId, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found) return KernelResult.Fail(KernelError.MissingCapability, $"Message requires {requirement.ResourceKind}:{requirement.ResourceId} ({requirement.Rights}).");
        }
        return KernelResult.Ok();
    }

    private static bool IsSupportedPayload(object? payload)
    {
        if (payload is null) return true;
        if (payload is ITransferableOwnedPayload) return true;
        if (payload is IBoundedPayload bounded) return bounded.PayloadSize >= 0 && bounded.PayloadSize <= bounded.MaxPayloadSize;
        return payload is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or bool or char or decimal or Enum;
    }
}
