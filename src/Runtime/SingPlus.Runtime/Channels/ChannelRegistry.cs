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

    internal event Action<ChannelId>? ChannelClosed;

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

    internal KernelResult<ChannelEnvelope> Send(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload,
        IReadOnlyCollection<CapabilityId>? capabilityIds) =>
        SendCore(sender, receiver, endpoint, messageId, payload, secondaryPayload: null, capabilityIds);

    internal KernelResult<ChannelEnvelope> SendOwnershipPair(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload,
        IReadOnlyCollection<CapabilityId>? capabilityIds) =>
        SendCore(sender, receiver, endpoint, messageId, firstOwnershipPayload, secondOwnershipPayload, capabilityIds);

    private KernelResult<ChannelEnvelope> SendCore(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload,
        object? secondaryPayload,
        IReadOnlyCollection<CapabilityId>? capabilityIds)
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
        var payloadValidation = ValidateRequestPayload(message.RequestPayload, payload, secondaryPayload);
        if (!payloadValidation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(payloadValidation.Error, payloadValidation.Message!);

        object? queuedPayload = payload;
        object? queuedSecondaryPayload = secondaryPayload;
        if (message.RequestPayload.Kind == RequestPayloadKind.OwnershipPair)
        {
            var pair = PrepareOwnershipPair(sender, receiver, message.RequestPayload, payload!, secondaryPayload!);
            if (!pair.IsSuccess)
                return KernelResult<ChannelEnvelope>.Fail(pair.Error, pair.Message!);
            queuedPayload = pair.Value!.First;
            queuedSecondaryPayload = pair.Value.Second;
        }
        else
        {
            if (message.Borrows.Count != 0)
            {
                var borrowed = (ITransferableOwnedPayload)payload!;
                if (!borrowed.IsValidForRuntime) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidRegionState, "Borrowing messages require a valid owned payload.");
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
                var owned = (ITransferableOwnedPayload)payload!;
                if (!owned.IsValidForRuntime) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidRegionState, "Consuming messages require a valid owned payload.");
                var oldHandle = owned.Handle;
                var owner = new RegionOwner(sender.DomainId, sender.Generation);
                var regionValidation = _regions.Validate(oldHandle, owner);
                if (!regionValidation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(regionValidation.Error, regionValidation.Message!);
                try
                {
                    owned.ValidateTransferForRuntime();
                }
                catch (InvalidOperationException exception)
                {
                    return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidRegionState, exception.Message);
                }
                var transfer = _regions.Transfer(oldHandle, owner, new RegionOwner(receiver.DomainId, receiver.Generation));
                if (!transfer.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(transfer.Error, transfer.Message!);
                queuedPayload = owned.TransferForRuntime(transfer.Value);
                _regions.ReplacePayload(oldHandle, transfer.Value, (ITransferableOwnedPayload)queuedPayload);
                sender.RemoveRegion(oldHandle);
                receiver.AddRegion(transfer.Value);
            }
        }

        record.Sequence++;
        var envelope = new ChannelEnvelope(record.Sequence, messageId, queuedPayload, queuedSecondaryPayload);
        record.Queue.Enqueue(envelope);
        record.State = transition.ToState;
        return KernelResult<ChannelEnvelope>.Ok(envelope);
    }

    private KernelResult<(object First, object Second)> PrepareOwnershipPair(
        SingProcess sender,
        SingProcess receiver,
        RequestPayloadDescriptorV1 expected,
        object firstPayload,
        object secondPayload)
    {
        var slots = expected.OwnershipPair;
        if (slots.Count != 2 || firstPayload is not ITransferableOwnedPayload first || secondPayload is not ITransferableOwnedPayload second)
            return KernelResult<(object, object)>.Fail(KernelError.UnsupportedPayload, "OwnershipPair requires exactly two ownership payloads.");

        if (first.Handle.RegionId == second.Handle.RegionId)
            return KernelResult<(object, object)>.Fail(KernelError.InvalidRegionState, "Borrow and consume authorities must refer to distinct regions.");

        var owner = new RegionOwner(sender.DomainId, sender.Generation);
        var borrower = new RegionOwner(receiver.DomainId, receiver.Generation);
        var payloads = new[] { first, second };
        for (var index = 0; index < payloads.Length; index++)
        {
            var current = payloads[index];
            if (!current.IsValidForRuntime)
                return KernelResult<(object, object)>.Fail(KernelError.InvalidRegionState, $"OwnershipPair payload '{slots[index].ParameterName}' is not a live ownership token.");
            var authority = _regions.Validate(current.Handle, owner);
            if (!authority.IsSuccess)
                return KernelResult<(object, object)>.Fail(authority.Error, authority.Message!);
            if (slots[index].Disposition == OwnershipRequestDisposition.Consume)
            {
                try
                {
                    current.ValidateTransferForRuntime();
                }
                catch (InvalidOperationException exception)
                {
                    return KernelResult<(object, object)>.Fail(KernelError.InvalidRegionState, exception.Message);
                }
            }
        }

        var borrowIndex = slots[0].Disposition == OwnershipRequestDisposition.Borrow ? 0 : 1;
        var consumeIndex = 1 - borrowIndex;
        var borrowed = payloads[borrowIndex];
        var consumed = payloads[consumeIndex];
        var acquired = _regions.AcquireLoan(borrowed.Handle, owner, borrower);
        if (!acquired.IsSuccess)
            return KernelResult<(object, object)>.Fail(acquired.Error, acquired.Message!);

        var grant = acquired.Value!;
        object borrowedLease;
        try
        {
            borrowedLease = borrowed.CreateBorrowLeaseForRuntime(grant.Handle, grant.Lifetime);
        }
        catch (InvalidOperationException exception)
        {
            _ = _regions.RevokeLoan(grant.Handle, owner);
            return KernelResult<(object, object)>.Fail(KernelError.InvalidRegionState, exception.Message);
        }

        var oldConsumedHandle = consumed.Handle;
        var transfer = _regions.Transfer(oldConsumedHandle, owner, borrower);
        if (!transfer.IsSuccess)
        {
            _ = _regions.RevokeLoan(grant.Handle, owner);
            return KernelResult<(object, object)>.Fail(transfer.Error, transfer.Message!);
        }

        var moved = consumed.TransferForRuntime(transfer.Value);
        _regions.ReplacePayload(oldConsumedHandle, transfer.Value, (ITransferableOwnedPayload)moved);
        sender.RemoveRegion(oldConsumedHandle);
        receiver.AddRegion(transfer.Value);

        return borrowIndex == 0
            ? KernelResult<(object, object)>.Ok((borrowedLease, moved))
            : KernelResult<(object, object)>.Ok((moved, borrowedLease));
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

    internal void CloseAllForProcess(ProcessHandle owner)
    {
        var closing = _channels.Values
            .Where(record => !record.Closed && (record.LeftOwner == owner || record.RightOwner == owner))
            .OrderBy(static record => record.Id.Value)
            .ToArray();
        CloseRecords(closing);
    }

    internal void CloseAllForDomain(DomainId domainId)
    {
        var closing = _channels.Values
            .Where(record => !record.Closed && (record.LeftDomain == domainId || record.RightDomain == domainId))
            .OrderBy(static record => record.Id.Value)
            .ToArray();
        CloseRecords(closing);
    }

    private void CloseRecords(IEnumerable<ChannelRecord> records)
    {
        foreach (var record in records)
        {
            record.Closed = true;
            record.Generation++;
            record.Queue.Clear();
            ChannelClosed?.Invoke(record.Id);
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

    private static KernelResult ValidateRequestPayload(
        RequestPayloadDescriptorV1 expected,
        object? payload,
        object? secondaryPayload)
    {
        if (expected.Kind != RequestPayloadKind.OwnershipPair && secondaryPayload is not null)
            return KernelResult.Fail(KernelError.UnsupportedPayload, "Only OwnershipPair requests may carry a secondary payload slot.");

        switch (expected.Kind)
        {
            case RequestPayloadKind.None:
                return payload is null
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, "Message does not declare a request payload.");

            case RequestPayloadKind.Primitive:
                if (payload is null) return KernelResult.Fail(KernelError.UnsupportedPayload, "Message requires a primitive request payload.");
                var primitiveTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                return string.Equals(primitiveTypeName, expected.TypeName, StringComparison.Ordinal)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected primitive payload type {expected.TypeName}, got {primitiveTypeName}.");

            case RequestPayloadKind.Enum:
                if (payload is not Enum) return KernelResult.Fail(KernelError.UnsupportedPayload, "Message requires the declared enum request payload.");
                var enumTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                return string.Equals(enumTypeName, expected.TypeName, StringComparison.Ordinal)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected enum payload type {expected.TypeName}, got {enumTypeName}.");

            case RequestPayloadKind.Bounded:
                if (payload is not IBoundedPayload bounded) return KernelResult.Fail(KernelError.UnsupportedPayload, "Message requires the declared bounded request payload.");
                var boundedTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                if (!string.Equals(boundedTypeName, expected.TypeName, StringComparison.Ordinal))
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected bounded payload type {expected.TypeName}, got {boundedTypeName}.");
                if (bounded.PayloadSize < 0)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Bounded payload size cannot be negative.");
                if (bounded.MaxPayloadSize != expected.MaxBytes)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Bounded payload self-reported limit {bounded.MaxPayloadSize} does not match contract MaxBytes {expected.MaxBytes}.");
                if (bounded.PayloadSize > expected.MaxBytes)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Bounded payload size {bounded.PayloadSize} exceeds contract MaxBytes {expected.MaxBytes}.");
                return KernelResult.Ok();

            case RequestPayloadKind.Ownership:
                if (payload is not ITransferableOwnedPayload owned)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Message requires the declared ownership request payload.");
                return owned.PayloadKind == expected.OwnershipPayloadKind
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected ownership payload kind {expected.OwnershipPayloadKind}, got {owned.PayloadKind}.");

            case RequestPayloadKind.OwnershipPair:
                if (payload is not ITransferableOwnedPayload first || secondaryPayload is not ITransferableOwnedPayload second || expected.OwnershipPair.Count != 2)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Message requires the declared two-slot ownership request payload.");
                if (first.PayloadKind != expected.OwnershipPair[0].PayloadKind)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected ownership-pair slot 0 kind {expected.OwnershipPair[0].PayloadKind}, got {first.PayloadKind}.");
                if (second.PayloadKind != expected.OwnershipPair[1].PayloadKind)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected ownership-pair slot 1 kind {expected.OwnershipPair[1].PayloadKind}, got {second.PayloadKind}.");
                return KernelResult.Ok();

            default:
                return KernelResult.Fail(KernelError.UnsupportedPayload, "Request payload kind is not supported.");
        }
    }
}