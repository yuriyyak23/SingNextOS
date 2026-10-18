using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class ChannelRegistry
{
    internal readonly record struct BoundedPayloadSnapshot(int Size, int Limit);

    internal BoundedPayloadSnapshot? CaptureInlineCopiedPayload(
        ChannelEndpointHandle endpoint, uint messageId, object? payload)
    {
        var channel = Resolve(endpoint);
        if (!channel.IsSuccess || !channel.Value!.Protocol.TryGetMessage(messageId, out var message) ||
            message.RequestPayload.Kind != RequestPayloadKind.Bounded || payload is not IBoundedPayload bounded ||
            !string.Equals(payload.GetType().FullName ?? payload.GetType().Name,
                message.RequestPayload.TypeName, StringComparison.Ordinal))
            return null;
        return new BoundedPayloadSnapshot(bounded.PayloadSize, bounded.MaxPayloadSize);
    }
    internal readonly record struct TelemetrySummary(int Channels, int QueuedMessages);
    internal readonly record struct ChannelClosureSnapshot(
        ChannelEndpointHandle Original, ulong CurrentGeneration, bool Closed)
    {
        internal bool AuthorizesReclaim => false;
    }
    internal readonly record struct InlineBorrowProjection<T>(ulong Sequence, BorrowLease<T> Lease) where T : unmanaged;
    internal readonly record struct InlineMoveProjection<T>(ulong Sequence, OwnedBuffer<T> Payload) where T : unmanaged;
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
        public PreparedOwnedTransfer? PendingOwnership { get; set; }
        public (ProcessHandle Owner, BudgetReservationHandle Reservation)? PendingSendBudget { get; set; }
        public object? PendingBorrowOriginal { get; set; }
        public object? PendingBorrowProjection { get; set; }
        public BorrowLeaseHandle? PendingBorrowHandle { get; set; }
        public QueuedLifetime? PreparedLifetime { get; set; }
        public Queue<ChannelEnvelope> Queue { get; } = new();
        public Dictionary<ulong, QueuedLifetime> Lifetimes { get; } = [];
    }

    private sealed class QueuedLifetime(ChannelRecord owner, ulong sequence)
    {
        public ChannelRecord Owner { get; } = owner;
        public ulong Sequence { get; } = sequence;
        public bool Ended { get; set; }
        public ChannelEnvelope? OriginalWithdrawal { get; set; }
        public bool WithdrawalCommitted { get; set; }
        public PreparedOwnedTransfer? OriginalTransferWithdrawal { get; set; }
        public bool Enqueued { get; set; }
        public bool BudgetAttached { get; set; }
        public object? OriginalBudgetOwner { get; set; }
    }

    private readonly CapabilityAuthority _capabilities;
    private readonly RegionAuthority _regions;
    private readonly Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>> _transferRegion;
    private readonly Dictionary<ChannelId, ChannelRecord> _channels = [];
    // Existing owner retains the same records; _channels is the endpoint routing index.
    private readonly Dictionary<ChannelId, ChannelRecord> _ownedChannels = [];
    private ulong _nextChannelId = 1;

    internal ChannelRegistry(CapabilityAuthority capabilities, RegionAuthority regions,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>> transferRegion)
    {
        _capabilities = capabilities;
        _regions = regions;
        _transferRegion = transferRegion;
    }

    // Reservation truth remains in ResourceBudgetAuthority. This is original
    // publication correlation retained by the existing channel owner before effect.
    internal KernelResult RetainSendBudget(ChannelEndpointHandle endpoint, ProcessHandle owner, BudgetReservationHandle reservation)
    {
        var resolved=Resolve(endpoint);
        if(!resolved.IsSuccess) return KernelResult.Fail(resolved.Error,resolved.Message!);
        var record=resolved.Value!;
        var expected=endpoint.EndpointId.Value==1 ? record.LeftOwner : record.RightOwner;
        if(expected!=owner) return KernelResult.Fail(KernelError.WrongEndpointOwner,"Original send budget owner differs.");
        if(record.PendingSendBudget is not null || record.PendingOwnership is not null)
            return KernelResult.Fail(KernelError.RegionUseConflict,"Original send publication remains pending.");
        record.PendingSendBudget=(owner,reservation);
        return KernelResult.Ok();
    }

    internal bool SendOwnershipIsPending(ChannelEndpointHandle endpoint) =>
        _ownedChannels.TryGetValue(endpoint.ChannelId,out var record) && OwnsOriginalChannel(record) && record.PendingOwnership is not null;

    internal void ConsumeSendBudgetCorrelation(ChannelEndpointHandle endpoint, ProcessHandle owner, BudgetReservationHandle reservation)
    {
        if(_ownedChannels.TryGetValue(endpoint.ChannelId,out var record) && OwnsOriginalChannel(record) &&
            record.PendingSendBudget is { } original && original.Owner==owner && original.Reservation==reservation)
            record.PendingSendBudget=null;
    }
    internal event Action<ChannelId>? ChannelClosed;

    internal KernelResult<object[]> WithdrawUnpublishedOriginalTransfer(ChannelEndpointHandle endpoint,ulong sequence,PreparedOwnedTransfer prepared,object lifetime,object budgetOwner)
    {
        if(lifetime is not QueuedLifetime fact || !OwnsOriginalChannel(fact.Owner) || fact.Owner.Id!=endpoint.ChannelId || fact.Sequence!=sequence ||
            !ReferenceEquals(fact.OriginalBudgetOwner,budgetOwner) || !fact.Owner.Lifetimes.TryGetValue(sequence,out var owned) || !ReferenceEquals(owned,fact) ||
            fact.Enqueued || !ReferenceEquals(fact.Owner.PendingOwnership,prepared) ||
            fact.OriginalTransferWithdrawal is { } previous && !ReferenceEquals(previous,prepared))
            return KernelResult<object[]>.Fail(KernelError.PlatformFaulted,"Unpublished original transfer lifetime differs.");
        if(fact.OriginalTransferWithdrawal is null)
        {
            if(!ReferenceEquals(fact.Owner.PreparedLifetime,fact) || fact.Owner.Sequence!=sequence-1)
                return KernelResult<object[]>.Fail(KernelError.ResponseNotPending,"Original unpublished identity changed.");
            fact.OriginalTransferWithdrawal=prepared;
            fact.Owner.Sequence=sequence;
            fact.Ended=true;
        }
        return KernelResult<object[]>.Ok(fact.Owner.PendingBorrowProjection is { } borrow ? [borrow,prepared.Projection] : [prepared.Projection]);
    }

    internal bool CompleteUnpublishedOriginalTransfer(object lifetime,PreparedOwnedTransfer prepared,object budgetOwner)
    {
        if(lifetime is not QueuedLifetime fact || !OwnsOriginalChannel(fact.Owner) || !fact.Ended || fact.Enqueued ||
            !ReferenceEquals(fact.OriginalTransferWithdrawal,prepared) || !ReferenceEquals(fact.OriginalBudgetOwner,budgetOwner) ||
            fact.Owner.PendingOwnership is { } pending && !ReferenceEquals(pending,prepared)) return false;
        fact.Owner.PendingOwnership=null;
        fact.Owner.PendingBorrowOriginal=null;
        fact.Owner.PendingBorrowProjection=null;
        fact.Owner.PendingBorrowHandle=null;
        if(ReferenceEquals(fact.Owner.PreparedLifetime,fact)) fact.Owner.PreparedLifetime=null;
        return true;
    }

    internal KernelResult WithdrawOriginalQueuedEnvelope(ChannelEndpointHandle endpoint,ChannelEnvelope raw,object lifetime,object budgetOwner)
    {
        if(lifetime is not QueuedLifetime fact || !OwnsOriginalChannel(fact.Owner) || fact.Owner.Id!=endpoint.ChannelId ||
            fact.Sequence!=raw.Sequence || !ReferenceEquals(fact.OriginalBudgetOwner,budgetOwner) ||
            !fact.Owner.Lifetimes.TryGetValue(fact.Sequence,out var owned) || !ReferenceEquals(owned,fact))
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original queue lifetime continuity unavailable.");
        if(fact.WithdrawalCommitted)
            return ReferenceEquals(fact.OriginalWithdrawal,raw) && fact.Ended ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.PlatformFaulted,"Original withdrawal raw continuity changed.");
        if(fact.Ended) return KernelResult.Fail(KernelError.ResponseNotPending,"Ended queue lifetime is not withdrawal proof.");
        var record=fact.Owner;
        if(record.Queue.Count==0 || !ReferenceEquals(record.Queue.Peek(),raw))
            return KernelResult.Fail(KernelError.ResponseNotPending,"Exact original envelope is not at the queue head.");
        if(fact.OriginalWithdrawal is { } previous && !ReferenceEquals(previous,raw))
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original withdrawal was replaced.");
        fact.OriginalWithdrawal=raw; // Retain original raw before the queue effect.
        record.Queue.Dequeue();
        fact.Ended=true;
        fact.WithdrawalCommitted=true;
        return KernelResult.Ok();
    }

    internal KernelResult<object> PrepareOriginalQueuedLifetime(ChannelEndpointHandle endpoint)
    {
        var resolved=Resolve(endpoint);
        if(!resolved.IsSuccess) return KernelResult<object>.Fail(resolved.Error,resolved.Message!);
        var record=resolved.Value!;
        if(record.PreparedLifetime is not null || record.Sequence==ulong.MaxValue || record.Queue.Count>=record.Capacity)
            return KernelResult<object>.Fail(KernelError.CapacityExhausted,"Original queue publication is pending or exhausted.");
        record.Lifetimes.EnsureCapacity(record.Lifetimes.Count+1);
        record.Queue.EnsureCapacity(record.Queue.Count+1);
        var fact=new QueuedLifetime(record,record.Sequence+1);
        record.Lifetimes.Add(fact.Sequence,fact);
        record.PreparedLifetime=fact;
        return KernelResult<object>.Ok(fact);
    }

    internal void EndRefusedQueuedPreparation(ChannelEndpointHandle endpoint)
    {
        if(_ownedChannels.TryGetValue(endpoint.ChannelId,out var record) && OwnsOriginalChannel(record) && record.PreparedLifetime is { } fact)
        { fact.Ended=true; record.PreparedLifetime=null; }
    }

    internal bool RetireUnpublishedSequence(ChannelEndpointHandle endpoint,ulong sequence)
    {
        var resolved=Resolve(endpoint);
        if(!resolved.IsSuccess || sequence==0 || resolved.Value!.Sequence!=sequence-1 || resolved.Value.PendingOwnership is not null) return false;
        resolved.Value.Sequence=sequence;
        return true; // Identity consumed; protocol state and Region effect unchanged.
    }

    internal TelemetrySummary InspectionSummary(ProcessHandle owner)
    {
        var selected = _ownedChannels.Values.Where(record => !record.Closed && (record.LeftOwner == owner || record.RightOwner == owner)).ToArray();
        return new(selected.Length, selected.Sum(static record => record.Queue.Count));
    }

    internal KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> Create(ProtocolDefinitionV1 protocol, SingProcess left, SingProcess right, int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        // Reserve the terminal allocator value; never wrap into an old identity.
        if (_nextChannelId is 0 or ulong.MaxValue)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(
                KernelError.CapacityExhausted, "Channel identity allocator is exhausted.");
        _ownedChannels.EnsureCapacity(_ownedChannels.Count + 1);
        _channels.EnsureCapacity(_channels.Count + 1);
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
        _ownedChannels.Add(id, record);
        _channels.Add(id, record);
        return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Ok(
            (new ChannelEndpointHandle(id, new EndpointId(1), 1), new ChannelEndpointHandle(id, new EndpointId(2), 1)));
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

    // Observation only. Callers must not use this snapshot as a closure or reclaim decision.
    internal ChannelClosureSnapshot? SnapshotClosure(ChannelEndpointHandle original)
    {
        if (original.Generation is 0 or ulong.MaxValue || original.EndpointId.Value is not (1 or 2) ||
            !_channels.TryGetValue(original.ChannelId, out var record) ||
            !OwnsOriginalChannel(record) ||
            record.Generation != original.Generation + (record.Closed ? 1UL : 0UL))
            return null;
        return new(original, record.Generation, record.Closed);
    }

    internal KernelResult ValidateV2Semantic(
        ChannelEndpointHandle endpoint,
        uint messageId,
        IpcTransferSemantic semantic)
    {
        var resolved = Resolve(endpoint);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        if (!resolved.Value!.Protocol.TryGetMessage(messageId, out var message))
            return KernelResult.Fail(KernelError.InvalidMessage, "Message is not part of the protocol.");
        var valid = semantic switch
        {
            IpcTransferSemantic.Copy or IpcTransferSemantic.ScatterGatherCopy =>
                message.Consumes.Count == 0 && message.Borrows.Count == 0 && message.RequestPayload.Kind == RequestPayloadKind.Bounded,
            IpcTransferSemantic.Move => message.Consumes.Count == 1 && message.Borrows.Count == 0,
            IpcTransferSemantic.BorrowRead => message.Borrows.Count == 1 && message.Consumes.Count == 0,
            IpcTransferSemantic.RequestReply => true,
            _ => false,
        };
        return valid ? KernelResult.Ok() : KernelResult.Fail(KernelError.UnsupportedPayload,
            $"Protocol message does not declare {semantic} semantics.");
    }

    internal KernelResult<ChannelEnvelope> Send(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload,
        IReadOnlyCollection<CapabilityId>? capabilityIds,
        BoundedPayloadSnapshot? boundedSnapshot = null,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null) =>
        SendCore(sender, receiver, endpoint, messageId, payload, secondaryPayload: null, capabilityIds, boundedSnapshot, transferRegion);

    internal KernelResult<ChannelEnvelope> SendOwnershipPair(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload,
        IReadOnlyCollection<CapabilityId>? capabilityIds,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null) =>
        SendCore(sender, receiver, endpoint, messageId, firstOwnershipPayload, secondOwnershipPayload, capabilityIds, transferRegion: transferRegion);

    // Owner-side transport-elision primitive for the narrow synchronous copied-value
    // contour. It performs the same process/endpoint, schema and protocol transition
    // checks as SendCore, but creates no queue entry or ManagedCap-visible envelope.
    // Callers must still register and settle the invocation with its existing owner.
    internal KernelResult<ulong> BeginInlineCopiedInvocation(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? copiedPayload,
        BoundedPayloadSnapshot? boundedSnapshot = null)
    {
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess) return KernelResult<ulong>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var expectedSender = endpoint.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        var expectedReceiver = endpoint.EndpointId.Value == 1 ? record.RightOwner : record.LeftOwner;
        if (expectedSender != new ProcessHandle(sender.ProcessId, sender.Generation))
            return KernelResult<ulong>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the inline invocation sender.");
        if (expectedReceiver != new ProcessHandle(receiver.ProcessId, receiver.Generation))
            return KernelResult<ulong>.Fail(KernelError.WrongEndpointOwner, "Peer process does not own the inline invocation receiver endpoint.");
        if (!record.Protocol.TryGetMessage(messageId, out var message))
            return KernelResult<ulong>.Fail(KernelError.InvalidMessage, $"Message {messageId} is not part of the protocol.");
        if (message.RequestPayload.Kind is RequestPayloadKind.Ownership or RequestPayloadKind.OwnershipPair ||
            message.Borrows.Count != 0 || message.Consumes.Count != 0)
            return KernelResult<ulong>.Fail(KernelError.UnsupportedPayload, "Inline invocation supports only copied closed-value payloads.");
        if (message.RequiredCapabilities.Count != 0)
            return KernelResult<ulong>.Fail(KernelError.MissingCapability, "Inline invocation cannot elide message-attached capability validation.");
        if (!record.Protocol.TryTransition(record.State, messageId, out var transition))
            return KernelResult<ulong>.Fail(KernelError.InvalidProtocolTransition, $"Message {messageId} is illegal in state '{record.State}'.");
        var payloadValidation = ValidateRequestPayload(message.RequestPayload, copiedPayload, secondaryPayload: null, boundedSnapshot);
        if (!payloadValidation.IsSuccess)
            return KernelResult<ulong>.Fail(payloadValidation.Error, payloadValidation.Message!);
        if (record.Sequence == ulong.MaxValue)
            return KernelResult<ulong>.Fail(KernelError.CapacityExhausted, "Channel sequence is exhausted.");

        record.Sequence++;
        record.State = transition.ToState;
        return KernelResult<ulong>.Ok(record.Sequence);
    }

    // Owner-side transport elision for one linear read-only BORROW edge. The
    // returned lease is a TCB-private projection created by RegionAuthority's
    // existing loan lifetime; it is not an ownership token or authority cache.
    internal KernelResult<InlineBorrowProjection<T>> BeginInlineBorrowInvocation<T>(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        OwnedBuffer<T> payload) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(payload);
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess)
            return KernelResult<InlineBorrowProjection<T>>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var expectedSender = endpoint.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        var expectedReceiver = endpoint.EndpointId.Value == 1 ? record.RightOwner : record.LeftOwner;
        if (expectedSender != new ProcessHandle(sender.ProcessId, sender.Generation))
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the inline BORROW sender.");
        if (expectedReceiver != new ProcessHandle(receiver.ProcessId, receiver.Generation))
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.WrongEndpointOwner, "Peer process does not own the inline BORROW receiver endpoint.");
        if (!record.Protocol.TryGetMessage(messageId, out var message))
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.InvalidMessage, $"Message {messageId} is not part of the protocol.");
        if (message.RequestPayload.Kind != RequestPayloadKind.Ownership || message.Borrows.Count != 1 ||
            message.Consumes.Count != 0)
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.UnsupportedPayload, "Inline BORROW requires exactly one declared ownership payload with read-only borrow semantics.");
        if (message.RequiredCapabilities.Count != 0)
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.MissingCapability, "Inline BORROW cannot elide message-attached capability validation.");
        if (!record.Protocol.TryTransition(record.State, messageId, out var transition))
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.InvalidProtocolTransition, $"Message {messageId} is illegal in state '{record.State}'.");
        var payloadValidation = ValidateRequestPayload(message.RequestPayload, payload, secondaryPayload: null);
        if (!payloadValidation.IsSuccess)
            return KernelResult<InlineBorrowProjection<T>>.Fail(payloadValidation.Error, payloadValidation.Message!);
        if (record.Sequence == ulong.MaxValue)
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.CapacityExhausted, "Channel sequence is exhausted.");

        if(record.PendingOwnership is not null) return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.RegionUseConflict,"Original ownership publication is still pending.");
        var transferable = (ITransferableOwnedPayload)payload;
        if (!transferable.IsValidForRuntime)
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.InvalidRegionState, "Inline BORROW requires a live owned payload.");
        var owner = new RegionOwner(sender.DomainId, sender.Generation);
        var borrower = new RegionOwner(receiver.DomainId, receiver.Generation);
        var authoritative = _regions.Validate(payload.Handle, owner);
        if (!authoritative.IsSuccess)
            return KernelResult<InlineBorrowProjection<T>>.Fail(authoritative.Error, authoritative.Message!);
        var acquired = _regions.AcquireLoan(payload.Handle, owner, borrower);
        if (!acquired.IsSuccess)
            return KernelResult<InlineBorrowProjection<T>>.Fail(acquired.Error, acquired.Message!);
        var grant = acquired.Value!;
        BorrowLease<T> lease;
        try
        {
            lease = (BorrowLease<T>)transferable.CreateBorrowLeaseForRuntime(grant.Handle, grant.Lifetime);
        }
        catch (InvalidOperationException exception)
        {
            _ = _regions.RevokeLoan(grant.Handle, owner);
            return KernelResult<InlineBorrowProjection<T>>.Fail(KernelError.InvalidRegionState, exception.Message);
        }

        record.Sequence++;
        record.State = transition.ToState;
        return KernelResult<InlineBorrowProjection<T>>.Ok(new(record.Sequence, lease));
    }

    // Owner-side transport elision for one linear exclusive MOVE request. This
    // preserves SendCore's authoritative caller-to-service transfer and protocol
    // transition but creates no queue entry or envelope. It is not compensation
    // for an earlier move and never transfers directly between service stages.
    internal KernelResult<InlineMoveProjection<T>> BeginInlineMoveInvocation<T>(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        OwnedBuffer<T> payload,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(payload);
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess)
            return KernelResult<InlineMoveProjection<T>>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var expectedSender = endpoint.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        var expectedReceiver = endpoint.EndpointId.Value == 1 ? record.RightOwner : record.LeftOwner;
        if (expectedSender != new ProcessHandle(sender.ProcessId, sender.Generation))
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the inline MOVE sender.");
        if (expectedReceiver != new ProcessHandle(receiver.ProcessId, receiver.Generation))
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.WrongEndpointOwner, "Peer process does not own the inline MOVE receiver endpoint.");
        if (!record.Protocol.TryGetMessage(messageId, out var message))
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.InvalidMessage, $"Message {messageId} is not part of the protocol.");
        if (message.RequestPayload.Kind != RequestPayloadKind.Ownership || message.Consumes.Count != 1 ||
            message.Borrows.Count != 0)
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.UnsupportedPayload, "Inline MOVE requires exactly one consumed ownership payload.");
        if (message.RequiredCapabilities.Count != 0)
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.MissingCapability, "Inline MOVE cannot elide message-attached capability validation.");
        if (!record.Protocol.TryTransition(record.State, messageId, out var transition))
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.InvalidProtocolTransition, $"Message {messageId} is illegal in state '{record.State}'.");
        var payloadValidation = ValidateRequestPayload(message.RequestPayload, payload, secondaryPayload: null);
        if (!payloadValidation.IsSuccess)
            return KernelResult<InlineMoveProjection<T>>.Fail(payloadValidation.Error, payloadValidation.Message!);
        if (record.Sequence == ulong.MaxValue)
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.CapacityExhausted, "Channel sequence is exhausted.");

        if(record.PendingOwnership is not null) return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.RegionUseConflict,"Original ownership publication is still pending.");
        var transferable = (ITransferableOwnedPayload)payload;
        if (!transferable.IsValidForRuntime)
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.InvalidRegionState, "Inline MOVE requires a live owned payload.");
        var oldHandle = payload.Handle;
        var owner = new RegionOwner(sender.DomainId, sender.Generation);
        var target = new RegionOwner(receiver.DomainId, receiver.Generation);
        var authoritative = _regions.Validate(oldHandle, owner);
        if (!authoritative.IsSuccess)
            return KernelResult<InlineMoveProjection<T>>.Fail(authoritative.Error, authoritative.Message!);
        try
        {
            transferable.ValidateTransferForRuntime();
        }
        catch (InvalidOperationException exception)
        {
            return KernelResult<InlineMoveProjection<T>>.Fail(KernelError.InvalidRegionState, exception.Message);
        }
        var transfer = TransferPrepared(record, sender, receiver, transferable, null, transferRegion);
        if (!transfer.IsSuccess)
            return KernelResult<InlineMoveProjection<T>>.Fail(transfer.Error, transfer.Message!);
        var moved = (OwnedBuffer<T>)record.PendingOwnership!.Projection;
        sender.RemoveRegion(oldHandle);
        receiver.AddRegion(transfer.Value);

        record.Sequence++;
        record.State = transition.ToState;
        var result = KernelResult<InlineMoveProjection<T>>.Ok(new(record.Sequence, moved));
        record.PendingOwnership = null;
        record.PendingBorrowOriginal = null;
        record.PendingBorrowProjection = null;
        record.PendingBorrowHandle = null;
        return result;
    }

    private KernelResult<ChannelEnvelope> SendCore(
        SingProcess sender,
        SingProcess receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload,
        object? secondaryPayload,
        IReadOnlyCollection<CapabilityId>? capabilityIds,
        BoundedPayloadSnapshot? boundedSnapshot = null,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null)
    {
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var expectedSender = endpoint.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        var expectedReceiver = endpoint.EndpointId.Value == 1 ? record.RightOwner : record.LeftOwner;
        if (expectedSender != new ProcessHandle(sender.ProcessId, sender.Generation)) return KernelResult<ChannelEnvelope>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the sender.");
        if (expectedReceiver != new ProcessHandle(receiver.ProcessId, receiver.Generation)) return KernelResult<ChannelEnvelope>.Fail(KernelError.WrongEndpointOwner, "Peer process does not own the opposite endpoint.");
        if(record.PendingOwnership is not null) return KernelResult<ChannelEnvelope>.Fail(KernelError.RegionUseConflict,"Original ownership publication is still pending.");
        if (record.Queue.Count >= record.Capacity) return KernelResult<ChannelEnvelope>.Fail(KernelError.CapacityExhausted, "Channel capacity is exhausted.");
        if (!record.Protocol.TryGetMessage(messageId, out var message)) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidMessage, $"Message {messageId} is not part of the protocol.");
        if (!record.Protocol.TryTransition(record.State, messageId, out var transition)) return KernelResult<ChannelEnvelope>.Fail(KernelError.InvalidProtocolTransition, $"Message {messageId} is illegal in state '{record.State}'.");
        if (record.Sequence == ulong.MaxValue || record.Lifetimes.Count == int.MaxValue)
            return KernelResult<ChannelEnvelope>.Fail(KernelError.CapacityExhausted, "Queued lifetime identity/capacity is exhausted before transfer.");
        record.Lifetimes.EnsureCapacity(record.Lifetimes.Count + 1);
        var queuedLifetime = record.PreparedLifetime ?? new QueuedLifetime(record, record.Sequence + 1);
        if(queuedLifetime.Sequence!=record.Sequence+1 || queuedLifetime.Ended)
            return KernelResult<ChannelEnvelope>.Fail(KernelError.PlatformFaulted,"Original queue preparation changed before effect.");
        if (message.RequiredCapabilities.Count != 0)
            return _capabilities.CommitSessionPublication(sender.DomainId, sender.Generation,
                capabilityIds?.ToArray() ?? [], message.RequiredCapabilities.ToArray(), CommitMessage);
        return CommitMessage();

        KernelResult<ChannelEnvelope> CommitMessage()
        {
        var capabilityValidation = ValidateCapabilities(sender, message, capabilityIds);
        if (!capabilityValidation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(capabilityValidation.Error, capabilityValidation.Message!);
        var payloadValidation = ValidateRequestPayload(message.RequestPayload, payload, secondaryPayload, boundedSnapshot);
        if (!payloadValidation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(payloadValidation.Error, payloadValidation.Message!);
        if (record.Sequence == ulong.MaxValue)
            return KernelResult<ChannelEnvelope>.Fail(KernelError.CapacityExhausted, "Channel sequence is exhausted.");

        object? queuedPayload = payload;
        object? queuedSecondaryPayload = secondaryPayload;
        if (message.RequestPayload.Kind == RequestPayloadKind.OwnershipPair)
        {
            var pair = PrepareOwnershipPair(record, sender, receiver, message.RequestPayload, payload!, secondaryPayload!, transferRegion);
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
                var transfer = TransferPrepared(record, sender, receiver, owned, null, transferRegion); if (!transfer.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(transfer.Error, transfer.Message!);
                queuedPayload = record.PendingOwnership!.Projection;
                sender.RemoveRegion(oldHandle);
                receiver.AddRegion(transfer.Value);
            }
        }

        record.Sequence++;
        var envelope = new ChannelEnvelope(record.Sequence, messageId, queuedPayload, queuedSecondaryPayload);
        record.Queue.Enqueue(envelope);
        queuedLifetime.Enqueued=true;
        if(record.PreparedLifetime is null) record.Lifetimes.Add(envelope.Sequence, queuedLifetime);
        record.PreparedLifetime=null;
        record.State = transition.ToState;
        record.PendingOwnership = null;
        record.PendingBorrowOriginal = null;
        record.PendingBorrowProjection = null;
        record.PendingBorrowHandle = null;
        return KernelResult<ChannelEnvelope>.Ok(envelope);
        }
    }

    private KernelResult<RegionHandle> TransferPrepared(
        ChannelRecord record, SingProcess sender, SingProcess receiver, ITransferableOwnedPayload original,
        Func<KernelResult>? admission,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transfer,
        Action? noEffect = null)
    {
        PreparedOwnedTransfer prepared;
        try { prepared = original.PrepareTransferForRuntime(); }
        catch(InvalidOperationException exception) { return KernelResult<RegionHandle>.Fail(KernelError.InvalidRegionState,exception.Message); }
        record.PendingOwnership = prepared; // Retain exact source/projection before possible effect.
        return (transfer ?? _transferRegion)(sender,receiver,prepared.SourceHandle,admission, () =>
        {
            prepared.Dispose();
            if(ReferenceEquals(record.PendingOwnership,prepared)) record.PendingOwnership=null;
            noEffect?.Invoke();
        },prepared);
    }
    private KernelResult<(object First, object Second)> PrepareOwnershipPair( ChannelRecord record, SingProcess sender,
        SingProcess receiver,
        RequestPayloadDescriptorV1 expected,
        object firstPayload,
        object secondPayload,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null)
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
        var oldConsumedHandle = consumed.Handle;
        BorrowLeaseHandle? acquiredLease = null;
        object? borrowedLease = null;
        var exactNoEffect = false;
        record.PendingBorrowOriginal = borrowed;
        var transfer = TransferPrepared(record, sender, receiver, consumed, () =>
        {
            var acquired = _regions.AcquireLoan(borrowed.Handle, owner, borrower);
            if (!acquired.IsSuccess) return KernelResult.Fail(acquired.Error, acquired.Message!);
            var grant = acquired.Value!;
            acquiredLease = grant.Handle;
            record.PendingBorrowHandle = grant.Handle;
            try { borrowedLease = borrowed.CreateBorrowLeaseForRuntime(grant.Handle, grant.Lifetime); record.PendingBorrowProjection = borrowedLease; }
            catch (InvalidOperationException exception)
            {
                return KernelResult.Fail(KernelError.InvalidRegionState, exception.Message);
            }
            return KernelResult.Ok(); }, transferRegion, () => exactNoEffect = true); if (!transfer.IsSuccess)
        {
            if (exactNoEffect && acquiredLease is { } lease) _ = _regions.RevokeLoan(lease, owner);
            return KernelResult<(object, object)>.Fail(transfer.Error, transfer.Message!);
        }

        var moved = record.PendingOwnership!.Projection;
        sender.RemoveRegion(oldConsumedHandle);
        receiver.AddRegion(transfer.Value);

        return borrowIndex == 0
            ? KernelResult<(object, object)>.Ok((borrowedLease!, moved))
            : KernelResult<(object, object)>.Ok((moved, borrowedLease!));
    }

    private KernelResult<ChannelRecord> ResolveQueuedReceiver(SingProcess receiver, ChannelEndpointHandle endpoint)
    {
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess) return validation;
        var record = validation.Value!;
        var owner = endpoint.EndpointId.Value == 1 ? record.LeftOwner : record.RightOwner;
        if (owner != new ProcessHandle(receiver.ProcessId, receiver.Generation)) return KernelResult<ChannelRecord>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the receiver.");
        if (record.Queue.Count == 0) return KernelResult<ChannelRecord>.Fail(KernelError.InvalidMessage, "Channel queue is empty.");
        return validation;
    }

    internal KernelResult<ChannelEnvelope> Peek(SingProcess receiver, ChannelEndpointHandle endpoint)
    {
        var validation = ResolveQueuedReceiver(receiver, endpoint);
        return validation.IsSuccess ? KernelResult<ChannelEnvelope>.Ok(validation.Value!.Queue.Peek())
            : KernelResult<ChannelEnvelope>.Fail(validation.Error, validation.Message!);
    }

    internal KernelResult<ChannelEnvelope> Receive(SingProcess receiver, ChannelEndpointHandle endpoint)
    {
        var validation = ResolveQueuedReceiver(receiver, endpoint);
        if (!validation.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        var envelope = record.Queue.Dequeue();
        record.Lifetimes[envelope.Sequence].Ended = true;
        if (!record.Lifetimes[envelope.Sequence].BudgetAttached) record.Lifetimes.Remove(envelope.Sequence);
        return KernelResult<ChannelEnvelope>.Ok(envelope);
    }

    internal void CloseAllForProcess(ProcessHandle owner)
    {
        var closing = _ownedChannels.Values
            .Where(record => !record.Closed && (record.LeftOwner == owner || record.RightOwner == owner))
            .OrderBy(static record => record.Id.Value)
            .ToArray();
        CloseRecords(closing);
    }

    internal KernelResult Close(ChannelEndpointHandle endpoint, bool allowAlreadyClosed = false)
    {
        if (allowAlreadyClosed && endpoint.Generation is not 0 and not ulong.MaxValue &&
            endpoint.EndpointId.Value is 1 or 2 && _channels.TryGetValue(endpoint.ChannelId, out var closed) &&
            OwnsOriginalChannel(closed) && closed.Closed && closed.Generation == endpoint.Generation + 1)
            return KernelResult.Ok();
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        CloseRecords([validation.Value!]);
        return KernelResult.Ok();
    }

    internal KernelResult ApplyCancellationTransition(ChannelEndpointHandle endpoint, uint messageId)
    {
        var validation = Resolve(endpoint);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = validation.Value!;
        if (record.Protocol.CancellationTransitions.Count == 0) return KernelResult.Ok();
        if (!record.Protocol.TryCancellationTransition(record.State, messageId, out var transition))
            return KernelResult.Fail(KernelError.InvalidProtocolTransition, $"Cancellation of message {messageId} is illegal in protocol state '{record.State}'.");
        record.State = transition.ToState;
        return KernelResult.Ok();
    }

    internal void CloseAllForDomain(DomainId domainId)
    {
        var closing = _ownedChannels.Values
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
            foreach (var lifetime in record.Lifetimes.Values) lifetime.Ended = true;
            foreach (var sequence in record.Lifetimes.Where(item => !item.Value.BudgetAttached).Select(item => item.Key).ToArray())
                record.Lifetimes.Remove(sequence);
            ChannelClosed?.Invoke(record.Id);
        }
    }

    internal object? CaptureQueuedLifetime(ChannelId channel, ulong sequence)
    {
        if (!_channels.TryGetValue(channel, out var record) || !OwnsOriginalChannel(record) ||
            !record.Lifetimes.TryGetValue(sequence, out var fact)) return null;
        fact.BudgetAttached = true;
        return fact;
    }

    internal bool BindOriginalQueuedBudgetOwner(object original, object budgetOwner)
    {
        if (original is not QueuedLifetime fact || !_channels.TryGetValue(fact.Owner.Id, out var routed) ||
            !ReferenceEquals(routed, fact.Owner) || !OwnsOriginalChannel(fact.Owner) ||
            !fact.Owner.Lifetimes.TryGetValue(fact.Sequence, out var owned) ||
            !ReferenceEquals(owned, fact) || fact.OriginalBudgetOwner is { } prior && !ReferenceEquals(prior, budgetOwner)) return false;
        fact.OriginalBudgetOwner = budgetOwner;
        return true;
    }

    internal void ConsumeOriginalQueuedLifetime(object? original, ChannelId channel, ulong sequence, object budgetOwner)
    {
        if (HasEndedOriginalQueuedLifetime(original, channel, sequence, budgetOwner))
            ((QueuedLifetime)original!).Owner.Lifetimes.Remove(sequence);
    }

    internal bool HasEndedOriginalQueuedLifetime(object? original, ChannelId channel, ulong sequence, object budgetOwner) =>
        original is QueuedLifetime fact && fact.Sequence == sequence && fact.Owner.Id == channel &&
        ReferenceEquals(fact.OriginalBudgetOwner, budgetOwner) &&
        OwnsOriginalChannel(fact.Owner) &&
        fact.Owner.Lifetimes.TryGetValue(sequence, out var owned) && ReferenceEquals(owned, fact) && fact.Ended;

    private bool OwnsOriginalChannel(ChannelRecord record) =>
        _ownedChannels.TryGetValue(record.Id, out var original) && ReferenceEquals(original, record);

    private KernelResult<ChannelRecord> Resolve(ChannelEndpointHandle handle)
    {
        if (!_channels.TryGetValue(handle.ChannelId, out var record)) return KernelResult<ChannelRecord>.Fail(KernelError.ChannelNotFound, "Channel was not found.");
        if (!OwnsOriginalChannel(record)) return KernelResult<ChannelRecord>.Fail(KernelError.PlatformFaulted, "Original channel owner continuity was lost.");
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
        object? secondaryPayload,
        BoundedPayloadSnapshot? boundedSnapshot = null)
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
                var size = boundedSnapshot?.Size ?? bounded.PayloadSize;
                var limit = boundedSnapshot?.Limit ?? bounded.MaxPayloadSize;
                if (size < 0)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Bounded payload size cannot be negative.");
                if (limit != expected.MaxBytes)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Bounded payload self-reported limit {limit} does not match contract MaxBytes {expected.MaxBytes}.");
                if (size > expected.MaxBytes)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Bounded payload size {size} exceeds contract MaxBytes {expected.MaxBytes}.");
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







