using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal sealed class EndpointSessionInvocationRegistry
{
    internal readonly record struct InvocationConsequenceSnapshot(
        EndpointSessionInvocationHandle Invocation,
        bool Delivered,
        bool ServiceAccepted,
        ResponsePublicationStatus? SettlementInProgress,
        ResponsePublicationStatus? TerminalStatus,
        bool UnclosedPossibleEffect,
        ResourceDonationState? DonationState)
    {
        internal bool AuthorizesClosure => false;
        internal bool AuthorizesReclaim => false;
    }

    private sealed class Record
    {
        public Record? OriginalSelf { get; set; }
        public required EndpointSessionInvocationHandle Handle { get; init; }
        public required ProcessHandle Caller { get; init; }
        public required ProcessHandle Service { get; init; }
        public required uint MessageId { get; init; }
        public CancellationScopeHandle? CancellationScope { get; set; }
        public bool Delivered { get; set; }
        public bool OriginalSendPending { get; set; }
        public bool OriginalSendNoEffect { get; set; }
        public bool OriginalSendCleanupCompleted { get; set; }
        public OriginalSend? OriginalSendFact { get; set; }
        public object? OriginalSendInput { get; set; }
        public object? OriginalSendSecondInput { get; set; }
        public EndpointSessionPin? OriginalSendPin { get; set; }
        public ChannelEnvelope? OriginalSendReply { get; set; }
        public SingPlus.Sip.PreparedOwnedTransfer? OriginalSendTransfer { get; set; }
        public KernelResult<RegionHandle>? OriginalSendTransferResult { get; set; }
        public KernelResult<RegionHandle>? OriginalSendRegionRaw { get; set; }
        public object? OriginalSendTargetReservationOwner { get; set; }
        public bool OriginalSendTargetBudgetExpected { get; set; }
        public bool OriginalSendSourceBudgetExpected { get; set; }
        public BudgetReservationHandle? OriginalSendTargetReservation { get; set; }
        public bool OriginalSendProducerPrepared { get; set; }
        public RegionHandle OriginalSendSourceRegion { get; set; }
        public DomainId OriginalSendSourceRegionDomain { get; set; }
        public RegionOwner OriginalSendTargetRegionOwner { get; set; }
        public object? OriginalSendRegionAccountingOwner { get; set; }
        public (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[]? OriginalSendSourceCharges { get; set; }
        public bool ServiceAccepted { get; set; }
        public ChannelEnvelope? OriginalDeliveredRequest { get; set; }
        public ChannelEnvelope? OriginalServiceRequest { get; set; }
        public bool InFlightCancellationAllowed { get; set; }
        public bool CancellationRequested { get; set; }
        public bool CancellationAccepted { get; set; }
        public bool AcceptedResponseCancelled { get; set; }
        public object? OriginalVirtualIoTransfer { get; set; }
        public bool VirtualIoTransferContained { get; set; }
        public bool VirtualIoTransferNeverInvoked { get; set; }
        public bool VirtualIoTransferNoEffect { get; set; }
        public bool VirtualIoTransferReadReleased { get; set; }
        public object? OriginalVirtualIoBind { get; set; }
        public bool VirtualIoBindContained { get; set; }
        public bool VirtualIoBindNeverInvoked { get; set; }
        public object? OriginalVirtualIoClose { get; set; }
        public VirtualIoBinding? VirtualIoCloseBinding { get; set; }
        public bool VirtualIoCloseContained { get; set; }
        public bool VirtualIoCloseNeverInvoked { get; set; }
        public object? OriginalVirtualCreate { get; set; }
        internal object? CapturedVirtualCreate { get; set; }
        internal bool VirtualCreateCleanupCompleted { get; set; }
        public bool VirtualCreateContained { get; set; }
        public bool VirtualCreateNeverInvoked { get; set; }
        public bool VirtualCreateNoEffect { get; set; }
        public object? OriginalComputeCopy { get; set; }
        public bool ComputeCopyContained { get; set; }
        public object? OriginalVirtualArtifactOwner { get; set; }
        public GuestRegionMappingHandle? VirtualArtifactMapping { get; set; }
        public bool VirtualArtifactContained { get; set; }
        public object? OriginalVirtualTransitionOwner { get; set; }
        public VirtualDomainHandle? VirtualTransitionDomain { get; set; }
        public bool VirtualTransitionContained { get; set; }
        public bool VirtualTransitionNeverInvoked { get; set; }
        public bool VirtualTransitionNoEffect { get; set; }
        public bool FailedSettlementMayHaveEffect { get; set; }
        public bool DonationReturnInProgress { get; set; }
        public bool DonationBindingInProgress { get; set; } // Admission only.
        public ResponsePublicationStatus? SettlementInProgress { get; set; }
        public ResponsePublicationStatus? TerminalStatus { get; set; }
        public ResponseEnvelope? SettlementOutcome { get; set; } // Retained outcome, never permission.
        public bool SettlementNeverStarted { get; set; } // Exact local response no-effect, not external closure.
        public ResponseRegistry.OriginalPublication? OriginalResponse { get; set; }
        public ResourceDonationBinding? ResourceDonation { get; set; }
    }

    private readonly Dictionary<(EndpointSessionId Session, EndpointSessionGeneration SessionGeneration, EndpointSessionInvocationId Invocation), Record> _records = [];
    // The same authoritative records survive loss of the invocation routing index.
    private readonly Dictionary<(EndpointSessionId Session, EndpointSessionGeneration SessionGeneration, EndpointSessionInvocationId Invocation), Record> _ownedRecords = [];
    private readonly object _gate = new();
    private readonly object _settlementAdmissionGate;
    private readonly CancellationScopeAuthority _cancellationScopes;

    internal Action? SettlementReservedHook { get; set; }

    internal EndpointSessionInvocationRegistry(CancellationScopeAuthority cancellationScopes,
        object? settlementAdmissionGate = null)
    {
        _cancellationScopes = cancellationScopes;
        _settlementAdmissionGate = settlementAdmissionGate ?? _gate;
    }

    internal sealed class OriginalSend
    {
        internal readonly object Record;
        internal readonly EndpointSessionInvocationRegistry Owner;
        internal readonly OriginalSend Self;
        internal OriginalSend(EndpointSessionInvocationRegistry owner,object record) { Owner=owner; Record=record; Self=this; }
    }

    internal OriginalSend PrepareOriginalSend(EndpointSessionHandle session,ProcessHandle caller,ProcessHandle service,
        ulong sequence,uint messageId,EndpointSessionPin pin,object? input,object? second,CancellationScopeHandle? scope)
    {
        lock(_gate)
        {

            var handle=new EndpointSessionInvocationHandle(session,new(sequence),new(1));
            if(_ownedRecords.ContainsKey(Key(handle))) throw new InvalidOperationException("Original send identity already exists.");
            Register(session,caller,service,sequence,messageId,scope);
            var record=_ownedRecords[Key(handle)];
            var original=new OriginalSend(this,record);
            record.OriginalSendFact=original;
            record.OriginalSendInput=input;
            record.OriginalSendSecondInput=second;
            record.OriginalSendPin=pin;
            record.OriginalSendPending=true;
            return original;
        }
    }

    internal bool CompleteOriginalSend(OriginalSend original,ChannelEnvelope? reply,bool finalPublication=true)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) || original.Record is not Record record ||
                !ReferenceEquals(record.OriginalSelf,record) || !_ownedRecords.TryGetValue(Key(record.Handle),out var owned) ||
                !ReferenceEquals(owned,record) || !ReferenceEquals(record.OriginalSendFact,original) || !record.OriginalSendPending) return false;
            if(record.OriginalSendReply is { } raw && !ReferenceEquals(raw,reply)) return false;
            if(reply is not null && (reply.Sequence!=record.Handle.InvocationId.Value || reply.MessageId!=record.MessageId)) return false;
            record.OriginalSendReply=reply;
            if(!finalPublication) return true;
            record.OriginalSendPending=false;
            if(reply is null) record.OriginalSendNoEffect=true;
            return true;
        }
    }

    internal bool OriginalSendIsPending(OriginalSend original)
    {
        lock(_gate)
        {
            if(ReferenceEquals(original.Self,original) && ReferenceEquals(original.Owner,this) && original.Record is Record terminal &&
                ReferenceEquals(terminal.OriginalSelf,terminal) && ReferenceEquals(terminal.OriginalSendFact,original) && terminal.OriginalSendCleanupCompleted) return false;
            // Absence/foreign routing is not a completed original send. Keep
            // caller admission pinned unless this exact owner proves completion.
            if(!ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) ||
                !ReferenceEquals(record.OriginalSendFact,original) || !_ownedRecords.TryGetValue(Key(record.Handle),out var owned) ||
                !ReferenceEquals(owned,record)) return true;
            return record.OriginalSendPending;
        }
    }

    internal bool OriginalSendCleanupIsCompleted(OriginalSend original)
    {
        lock(_gate) return ReferenceEquals(original.Self,original) && ReferenceEquals(original.Owner,this) &&
            original.Record is Record record && ReferenceEquals(record.OriginalSelf,record) &&
            ReferenceEquals(record.OriginalSendFact,original) && record.OriginalSendCleanupCompleted;
    }

    internal bool BindOriginalSendTransfer(OriginalSend original,SingPlus.Sip.PreparedOwnedTransfer prepared)
    {
        lock(_gate)
        {
            if(!OriginalSendIsPending(original) || !ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSendFact,original) ||
                !_ownedRecords.TryGetValue(Key(record.Handle),out var owned) || !ReferenceEquals(owned,record) ||
                record.OriginalSendTransfer is not null ||
                !ReferenceEquals(prepared.Source,record.OriginalSendInput) && !ReferenceEquals(prepared.Source,record.OriginalSendSecondInput)) return false;
            record.OriginalSendTransfer=prepared;
            return true;
        }
    }

    internal void RetainOriginalSendTransferResult(OriginalSend original,SingPlus.Sip.PreparedOwnedTransfer prepared,KernelResult<RegionHandle> result)
    {
        lock(_gate)
        {
            if(original.Record is not Record record || !ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) ||
                !ReferenceEquals(record.OriginalSendFact,original) || !ReferenceEquals(record.OriginalSendTransfer,prepared) || record.OriginalSendTransferResult is not null)
                throw new InvalidOperationException("Original transfer result owner continuity changed.");
            record.OriginalSendTransferResult=result;
        }
    }

    internal void RetainOriginalTransferProducerAdmission(OriginalSend original,SingPlus.Sip.PreparedOwnedTransfer prepared,BudgetReservationHandle? reservation,object? budgetOwner,
        RegionHandle sourceRegion, DomainId sourceDomain, RegionOwner targetOwner,
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[] sourceCharges, object regionAccountingOwner)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) || original.Record is not Record record ||
                !ReferenceEquals(record.OriginalSendFact,original) || !ReferenceEquals(record.OriginalSendTransfer,prepared) ||
                record.OriginalSendProducerPrepared || reservation is not null && budgetOwner is null)
                throw new InvalidOperationException("Original producer admission continuity changed.");
            record.OriginalSendTargetReservation=reservation;
            record.OriginalSendTargetBudgetExpected=reservation is not null;
            record.OriginalSendSourceBudgetExpected=sourceCharges.Length!=0;
            record.OriginalSendTargetReservationOwner=budgetOwner;
            record.OriginalSendSourceRegion=sourceRegion;
            record.OriginalSendSourceRegionDomain=sourceDomain;
            record.OriginalSendTargetRegionOwner=targetOwner;
            record.OriginalSendSourceCharges=sourceCharges.ToArray();
            record.OriginalSendRegionAccountingOwner=regionAccountingOwner;
            record.OriginalSendProducerPrepared=true;
        }
    }

    internal KernelResult<(RegionHandle Source, DomainId SourceDomain, RegionOwner TargetOwner, object RegionOwner, SingPlus.Sip.PreparedOwnedTransfer Prepared,
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[] Charges)>
        ReadOriginalTransferCleanupFacts(OriginalSend original)
    {
        lock(_gate)
        {
            var producer=ReadOriginalTransferProducer(original);
            if(!producer.IsSuccess || original.Record is not Record record || record.OriginalSendSourceCharges is null ||
                record.OriginalSendSourceBudgetExpected!=(record.OriginalSendSourceCharges.Length!=0) ||
                record.OriginalSendSourceCharges.Length>1 || record.OriginalSendRegionAccountingOwner is null || record.OriginalSendTransfer is null)
                return KernelResult<(RegionHandle,DomainId,RegionOwner,object,SingPlus.Sip.PreparedOwnedTransfer,(object,object?,ProcessHandle,BudgetReservationHandle)[])>.Fail(
                    KernelError.PlatformFaulted,"Original quantitative admission facts unavailable.");
            return KernelResult<(RegionHandle,DomainId,RegionOwner,object,SingPlus.Sip.PreparedOwnedTransfer,(object,object?,ProcessHandle,BudgetReservationHandle)[])>.Ok(
                (record.OriginalSendSourceRegion,record.OriginalSendSourceRegionDomain,record.OriginalSendTargetRegionOwner,record.OriginalSendRegionAccountingOwner,record.OriginalSendTransfer,record.OriginalSendSourceCharges.ToArray()));
        }
    }

    internal void RetainOriginalTransferRegionRaw(OriginalSend original,SingPlus.Sip.PreparedOwnedTransfer prepared,KernelResult<RegionHandle> raw)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) || original.Record is not Record record ||
                !ReferenceEquals(record.OriginalSendFact,original) || !ReferenceEquals(record.OriginalSendTransfer,prepared) ||
                !record.OriginalSendProducerPrepared || record.OriginalSendRegionRaw is not null)
                throw new InvalidOperationException("Original Region producer raw continuity changed.");
            record.OriginalSendRegionRaw=raw;
        }
    }

    internal KernelResult<(KernelResult<RegionHandle>? Raw,BudgetReservationHandle? Reservation,object? BudgetOwner)> ReadOriginalTransferProducer(OriginalSend original)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) || original.Record is not Record record ||
                !ReferenceEquals(record.OriginalSelf,record) || !ReferenceEquals(record.OriginalSendFact,original) || !record.OriginalSendProducerPrepared)
                return KernelResult<(KernelResult<RegionHandle>?,BudgetReservationHandle?,object?)>.Fail(KernelError.PlatformFaulted,"Original Region producer fact unavailable.");
            if(record.OriginalSendTargetBudgetExpected!=(record.OriginalSendTargetReservation is not null) ||
                record.OriginalSendTargetBudgetExpected!=(record.OriginalSendTargetReservationOwner is not null))
                return KernelResult<(KernelResult<RegionHandle>?,BudgetReservationHandle?,object?)>.Fail(KernelError.PlatformFaulted,"Original target quantitative fact absent or malformed.");
            return KernelResult<(KernelResult<RegionHandle>?,BudgetReservationHandle?,object?)>.Ok((record.OriginalSendRegionRaw,record.OriginalSendTargetReservation,record.OriginalSendTargetReservationOwner));
        }
    }

    internal KernelResult<(ChannelEndpointHandle Channel,ulong Sequence,ProcessHandle Service,SingPlus.Sip.PreparedOwnedTransfer Prepared)> ReadOriginalCompletedSendTransfer(OriginalSend original,ProcessHandle caller)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) || original.Record is not Record record ||
                !ReferenceEquals(record.OriginalSelf,record) || !ReferenceEquals(record.OriginalSendFact,original) ||
                !_ownedRecords.TryGetValue(Key(record.Handle),out var owned) || !ReferenceEquals(owned,record) ||
                record.Caller!=caller || !record.OriginalSendPending || record.Delivered || record.ServiceAccepted || record.OriginalSendReply is not null ||
                record.OriginalSendTransfer is not { } prepared || record.OriginalSendTransferResult is not { } composition ||
                record.OriginalSendRegionRaw is not { IsSuccess:true } result ||
                !Enum.IsDefined(composition.Error) || composition.IsSuccess && composition.Value!=result.Value ||
                !prepared.IsOriginal || prepared.SourceHandle!=record.OriginalSendSourceRegion ||
                !ReferenceEquals(prepared.Source,record.OriginalSendInput) && !ReferenceEquals(prepared.Source,record.OriginalSendSecondInput) ||
                prepared.Projection is not SingPlus.Sip.ITransferableOwnedPayload projection || projection.Handle!=result.Value)
                return KernelResult<(ChannelEndpointHandle,ulong,ProcessHandle,SingPlus.Sip.PreparedOwnedTransfer)>.Fail(KernelError.ResponseNotPending,"Exact completed original transfer result unavailable.");
            return KernelResult<(ChannelEndpointHandle,ulong,ProcessHandle,SingPlus.Sip.PreparedOwnedTransfer)>.Ok((record.OriginalSendPin!.OriginalRecord.Channel,record.Handle.InvocationId.Value,record.Service,prepared));
        }
    }

    internal KernelResult<(EndpointSessionInvocationHandle Invocation,ProcessHandle Service,ChannelEnvelope Raw,ChannelEndpointHandle Channel)> ReadPendingOriginalSend(OriginalSend original,ProcessHandle caller)
    {
        lock(_gate)
        {
            if(!OriginalSendIsPending(original) || !ReferenceEquals(original.Self,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) ||
                !ReferenceEquals(record.OriginalSendFact,original) || !_ownedRecords.TryGetValue(Key(record.Handle),out var owned) ||
                !ReferenceEquals(owned,record) || record.Caller!=caller || record.Delivered || record.ServiceAccepted || record.OriginalSendReply is not { } raw)
                return KernelResult<(EndpointSessionInvocationHandle,ProcessHandle,ChannelEnvelope,ChannelEndpointHandle)>.Fail(KernelError.ResponseNotPending,"Exact undelivered original send raw is unavailable; cleanup remains pinned.");
            return KernelResult<(EndpointSessionInvocationHandle,ProcessHandle,ChannelEnvelope,ChannelEndpointHandle)>.Ok((record.Handle,record.Service,raw,record.OriginalSendPin!.OriginalRecord.Channel));
        }
    }

    internal KernelResult<EndpointSessionPin> CompleteWithdrawnOriginalSend(OriginalSend original,ProcessHandle caller)
    {
        lock(_gate)
        {
            var pending=ReadPendingOriginalSend(original,caller);
            if(!pending.IsSuccess && !ReadOriginalCompletedSendTransfer(original,caller).IsSuccess)
                return KernelResult<EndpointSessionPin>.Fail(pending.Error,pending.Message!);
            var record=(Record)original.Record;
            record.OriginalSendCleanupCompleted=true;
            record.OriginalSendPending=false;
            record.OriginalSendNoEffect=true; // No service invocation effect; ownership cleanup was separately completed.
            return KernelResult<EndpointSessionPin>.Ok(record.OriginalSendPin!);
        }
    }
    internal EndpointSessionInvocationHandle Register(
        EndpointSessionHandle session,
        ProcessHandle caller,
        ProcessHandle service,
        ulong requestSequence,
        uint messageId,
        CancellationScopeHandle? cancellationScope = null)
    {
        if (requestSequence == 0) throw new ArgumentOutOfRangeException(nameof(requestSequence));
        var handle = new EndpointSessionInvocationHandle(
            session,
            new EndpointSessionInvocationId(requestSequence),
            new EndpointSessionInvocationGeneration(1));
        var key = Key(handle);
        lock (_gate)
        {
            if (_ownedRecords.TryGetValue(key, out var existing))
            {
                if (!_records.TryGetValue(key, out var routed) || !ReferenceEquals(routed, existing))
                    throw new InvalidOperationException("Original invocation owner continuity was lost before registration.");
                if (existing.Caller != caller ||
                    existing.Service != service ||
                    existing.MessageId != messageId ||
                    cancellationScope is { } supplied && existing.CancellationScope is { } current && supplied != current)
                {
                    throw new InvalidOperationException(
                        "Endpoint session invocation identity conflicts with existing correlation.");
                }

                existing.CancellationScope ??= cancellationScope;
                return existing.Handle;
            }

            _ownedRecords.EnsureCapacity(_ownedRecords.Count + 1);
            _records.EnsureCapacity(_records.Count + 1);
            var record = new Record
            {
                Handle = handle,
                Caller = caller,
                Service = service,
                MessageId = messageId,
                CancellationScope = cancellationScope
            };
            record.OriginalSelf=record;
            _ownedRecords.Add(key, record);
            _records.Add(key, record);
        }
        return handle;
    }

    // Caller holds the kernel correlation gate and supplies the actual queued request.
    internal KernelResult<EndpointSessionInvocationHandle> PrepareOriginalRequestDelivery(
        EndpointSessionHandle session, ProcessHandle caller, ProcessHandle service,
        ulong requestSequence, uint messageId)
    {
        var handle = new EndpointSessionInvocationHandle(session,
            new EndpointSessionInvocationId(requestSequence), new EndpointSessionInvocationGeneration(1));
        lock (_gate)
        {
            // V1 public Send can precede the first session invocation registration.
            // A retained original owner or any existing route must never be replaced.
            if (!_ownedRecords.ContainsKey(Key(handle)) && !_records.ContainsKey(Key(handle)))
                Register(session, caller, service, requestSequence, messageId);
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.Caller != caller || record.MessageId != messageId)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(KernelError.PlatformFaulted,
                    "Queued request does not match its original invocation owner.");
            if (record.OriginalSendPending || record.OriginalSendNoEffect || record.Delivered || record.TerminalStatus is not null ||
                record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(KernelError.ResponseNotPending,
                    "Original invocation is no longer eligible for queued delivery.");
            return KernelResult<EndpointSessionInvocationHandle>.Ok(handle);
        }
    }

    internal KernelResult<EndpointSessionInvocationHandle> MarkDelivered(
        EndpointSessionHandle session,
        ProcessHandle service,
        ulong requestSequence,ChannelEnvelope? originalRequest=null)
    {
        var handle = new EndpointSessionInvocationHandle(
            session,
            new EndpointSessionInvocationId(requestSequence),
            new EndpointSessionInvocationGeneration(1));
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.OriginalSendPending || resolved.Value.OriginalSendNoEffect || resolved.Value.TerminalStatus is not null)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if(originalRequest is not null)
            {
                if(originalRequest.Sequence!=requestSequence || originalRequest.MessageId!=resolved.Value.MessageId ||
                    resolved.Value.OriginalDeliveredRequest is { } prior && !ReferenceEquals(prior,originalRequest))
                    return KernelResult<EndpointSessionInvocationHandle>.Fail(KernelError.PlatformFaulted,"Original delivered request cannot change.");
                if(resolved.Value.MessageId is SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync or
                    SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync or
                    SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync or
                    SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseIoAsync)
                    resolved.Value.OriginalDeliveredRequest=originalRequest;
            }
            resolved.Value.Delivered = true;
            return KernelResult<EndpointSessionInvocationHandle>.Ok(handle);
        }
    }

    internal KernelResult RequestCancellation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller)
    {
        lock (_gate)
        {
            var resolved = ResolveForCaller(handle, caller);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (record.ServiceAccepted && !record.InFlightCancellationAllowed)
                return KernelResult.Fail(KernelError.InvalidTransition, "The service already accepted this invocation without an in-flight cancellation contour.");
            record.CancellationRequested = true;
            return KernelResult.Ok();
        }
    }

    internal KernelResult BindCancellationScope(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller,
        CancellationScopeHandle scope, long? observedTimestamp = null)
    {
        var timestamp = observedTimestamp ?? _cancellationScopes.CaptureTimestamp(scope);
        lock (_gate)
        {
            var resolved = ResolveForCaller(handle, caller);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.CancellationScope is { } existing && existing != scope)
                return KernelResult.Fail(KernelError.StaleGeneration, "Invocation is already bound to another cancellation scope generation.");
            var temporal = _cancellationScopes.Observe(caller, scope, timestamp);
            if (!temporal.IsSuccess) return KernelResult.Fail(temporal.Error, temporal.Message!);
            if (temporal.Value!.Disposition == CancellationDisposition.Stale)
                return KernelResult.Fail(KernelError.StaleGeneration, "Cancellation scope generation is stale.");
            var binding = _cancellationScopes.BindConsumer(
                caller,
                scope,
                $"ipc:{handle.Session.SessionId.Value}:{handle.Session.Generation.Value}:{handle.InvocationId.Value}:{handle.Generation.Value}");
            if (!binding.IsSuccess) return binding;
            resolved.Value.CancellationScope = scope;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<CancellationObservation> RequestCancellation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller,
        CancellationScopeHandle scope, long? observedTimestamp = null)
    {
        var timestamp = observedTimestamp ?? _cancellationScopes.CaptureTimestamp(scope);
        lock (_gate)
        {
            var resolved = ResolveForCaller(handle, caller);
            if (!resolved.IsSuccess) return KernelResult<CancellationObservation>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.CancellationScope != scope)
                return KernelResult<CancellationObservation>.Fail(KernelError.StaleGeneration, "Invocation is not bound to the exact cancellation scope generation.");
            var request = _cancellationScopes.Request(caller, scope, timestamp);
            if (!request.IsSuccess || request.Value!.Disposition == CancellationDisposition.Stale)
                return request;
            if (record.TerminalStatus == ResponsePublicationStatus.Published)
                return _cancellationScopes.RecordDisposition(caller, scope,
                    record.FailedSettlementMayHaveEffect
                        ? CancellationDisposition.TooLateEffectMayExist
                        : CancellationDisposition.CompletedBeforeCancellation, timestamp);
            if (record.TerminalStatus == ResponsePublicationStatus.Cancelled)
                return _cancellationScopes.RecordDisposition(caller, scope,
                    record.AcceptedResponseCancelled || record.FailedSettlementMayHaveEffect
                    ? CancellationDisposition.TooLateEffectMayExist
                    : record.ServiceAccepted
                        ? CancellationDisposition.ProviderEffectContained
                        : CancellationDisposition.CancelledBeforeEffect, timestamp);
            if (record.FailedSettlementMayHaveEffect ||
                record.ServiceAccepted && !record.InFlightCancellationAllowed)
                return _cancellationScopes.RecordDisposition(caller, scope, CancellationDisposition.TooLateEffectMayExist, timestamp);
            record.CancellationRequested = true;
            return request;
        }
    }

    internal KernelResult<bool> IsCancellationRequested(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            return resolved.IsSuccess
                ? KernelResult<bool>.Ok(resolved.Value!.CancellationRequested)
                : KernelResult<bool>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult AcceptInvocation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        bool allowInFlightCancellation)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (!record.Delivered)
                return KernelResult.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (record.FailedSettlementMayHaveEffect || record.SettlementInProgress is not null)
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "Invocation settlement may have an effect; new service acceptance requires exact closure.");
            if (record.ServiceAccepted)
                return KernelResult.Fail(KernelError.InvalidTransition, "Endpoint session invocation was already accepted by the service.");
            if(record.VirtualIoBindNeverInvoked || record.VirtualIoTransferNeverInvoked || record.VirtualIoCloseNeverInvoked || record.VirtualCreateNeverInvoked)
                return KernelResult.Fail(KernelError.InvalidTransition,"Original IO input was refused before acceptance.");
            if (record.CancellationRequested)
                return KernelResult.Fail(KernelError.InvalidTransition, "Cancellation was requested before service acceptance; execution must not begin.");
            record.ServiceAccepted = true;
            record.InFlightCancellationAllowed = allowInFlightCancellation;
            return KernelResult.Ok();
        }
    }

    internal KernelResult AcceptCancellation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (!record.Delivered)
                return KernelResult.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (!record.CancellationRequested)
                return KernelResult.Fail(KernelError.InvalidTransition, "The caller has not requested cancellation for this endpoint session invocation.");
            if (record.ServiceAccepted && !record.InFlightCancellationAllowed)
                return KernelResult.Fail(KernelError.InvalidTransition, "The accepted endpoint session invocation does not allow in-flight cancellation.");
            record.CancellationAccepted = true;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<ResponseEnvelope> Publish(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        Func<KernelResult<ResponseEnvelope>> publication,
        Func<KernelResult>? admission = null)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var reservation = ReserveSettlement(
            handle,
            service,
            ResponsePublicationStatus.Published,
            requireCancellationAccepted: false);
        if (!reservation.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(reservation.Error, reservation.Message!);

        return ExecuteSettlement(reservation.Value!, publication, admission);
    }

    internal KernelResult<ResponseEnvelope> Cancel(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        Func<KernelResult<ResponseEnvelope>> cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        var reservation = ReserveSettlement(
            handle,
            service,
            ResponsePublicationStatus.Cancelled,
            requireCancellationAccepted: null);
        if (!reservation.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(reservation.Error, reservation.Message!);

        return ExecuteSettlement(reservation.Value!, cancellation);
    }

    internal KernelResult CompleteInline(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        bool succeeded)
    {
        var status = succeeded ? ResponsePublicationStatus.Published : ResponsePublicationStatus.Cancelled;
        var reservation = ReserveSettlement(
            handle,
            service,
            status,
            requireCancellationAccepted: succeeded ? false : null,
            localCompletionOnly: true);
        if (!reservation.IsSuccess) return KernelResult.Fail(reservation.Error, reservation.Message!);

        (ProcessHandle Caller, CancellationScopeHandle Scope, CancellationDisposition Disposition)? outcome = null;
        lock (_gate)
        {
            var record = reservation.Value!;
            if (record.SettlementInProgress != status)
                return KernelResult.Fail(KernelError.InvalidTransition, "Inline invocation settlement reservation was lost.");
            record.SettlementInProgress = null;
            record.TerminalStatus = status;
            // The inline success flag reports local completion, not external containment.
            if (!succeeded && record.ServiceAccepted)
                record.AcceptedResponseCancelled = true;
            if (record.CancellationScope is { } scope)
            {
                var disposition = succeeded
                    ? record.FailedSettlementMayHaveEffect
                        ? CancellationDisposition.TooLateEffectMayExist
                        : CancellationDisposition.CompletedBeforeCancellation
                    : record.ServiceAccepted || record.FailedSettlementMayHaveEffect
                        ? CancellationDisposition.TooLateEffectMayExist
                        : CancellationDisposition.CancelledBeforeEffect;
                if (!succeeded || record.CancellationRequested)
                    outcome = (record.Caller, scope, disposition);
            }
        }
        if (outcome is { } observed)
            _ = _cancellationScopes.RecordDisposition(observed.Caller, observed.Scope, observed.Disposition);
        return KernelResult.Ok();
    }

    private KernelResult<ResponseEnvelope> ExecuteSettlement(
        Record reservation,
        Func<KernelResult<ResponseEnvelope>> action,
        Func<KernelResult>? admission = null)
    {
        var actionStarted = false;
        try
        {
            SettlementReservedHook?.Invoke();
            // Trusted admission runs after callbacks and before the response
            // action; refusal proves only that this action never started.
            var admitted=admission?.Invoke()??KernelResult.Ok();
            if(!admitted.IsSuccess)
            {
                lock(_settlementAdmissionGate)
                lock(_gate)
                {
                    var current=Resolve(reservation.Handle);
                    if(!current.IsSuccess || !ReferenceEquals(current.Value,reservation) || reservation.SettlementInProgress is null)
                    {
                        RetainUnstartedSettlement(reservation);
                        return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original response admission owner changed.");
                    }
                    RetainUnstartedSettlement(reservation);
                }
                return KernelResult<ResponseEnvelope>.Fail(admitted.Error,admitted.Message!);
            }
            lock (_settlementAdmissionGate)
            lock (_gate)
            {
                var current = Resolve(reservation.Handle);
                if (!current.IsSuccess || !ReferenceEquals(current.Value, reservation) ||
                    reservation.SettlementInProgress is null || reservation.FailedSettlementMayHaveEffect)
                {
                    RetainUnstartedSettlement(reservation);
                    return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,
                        "Original invocation settlement source was lost before action.");
                }
                actionStarted = true;
            }
            return CompleteSettlement(reservation, action());
        }
        catch
        {
            lock (_gate)
            {
                reservation.SettlementInProgress = null;
                if(!actionStarted && OwnsOriginalRecord(reservation))reservation.SettlementNeverStarted=true;
                if (actionStarted && reservation.TerminalStatus is null)
                    reservation.FailedSettlementMayHaveEffect = true;
            }
            throw;
        }
    }

    // Caller holds the existing admission/record gates; this fact covers only
    // this uninvoked response action, independently of native lifetime closure.
    private void RetainUnstartedSettlement(Record reservation)
    {
        if(OwnsOriginalRecord(reservation) &&
            _ownedRecords.TryGetValue(Key(reservation.Handle),out var original) && ReferenceEquals(original,reservation) &&
            reservation.SettlementInProgress is not null)
        {
            reservation.SettlementInProgress=null;
            reservation.SettlementNeverStarted=true;
        }
    }

    private KernelResult<Record> ReserveSettlement(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        ResponsePublicationStatus status,
        bool? requireCancellationAccepted, bool localCompletionOnly = false)
    {
        lock (_settlementAdmissionGate)
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess)
                return KernelResult<Record>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null || record.SettlementInProgress is not null ||
                record.OriginalSendPending ||
        record.DonationReturnInProgress || record.FailedSettlementMayHaveEffect && !localCompletionOnly)
                return KernelResult<Record>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal or settlement is in progress.");
            if (!record.Delivered)
                return KernelResult<Record>.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (requireCancellationAccepted == false && record.CancellationAccepted)
                return KernelResult<Record>.Fail(KernelError.InvalidTransition, "The service accepted cancellation and must settle the invocation as Cancelled.");

            record.SettlementInProgress = status;
            record.SettlementNeverStarted=false;
            return KernelResult<Record>.Ok(record);
        }
    }

    private KernelResult<ResponseEnvelope> CompleteSettlement(
        Record reservation,
        KernelResult<ResponseEnvelope> result)
    {
        (ProcessHandle Caller, CancellationScopeHandle Scope, CancellationDisposition Disposition)? outcome = null;
        lock (_gate)
        {
            if (reservation.SettlementInProgress is not { } status)
                throw new InvalidOperationException("Endpoint session invocation settlement reservation was lost.");

            reservation.SettlementInProgress = null;
            if (result.IsSuccess)
            {
                reservation.SettlementOutcome = result.Value;
                if (!OwnsOriginalRecord(reservation) || result.Value is not { } envelope ||
                    envelope.RequestSequence != reservation.Handle.InvocationId.Value ||
                    envelope.MessageId != reservation.MessageId || envelope.Status != status)
                {
                    reservation.FailedSettlementMayHaveEffect = true;
                    return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,
                        "Original invocation settlement outcome is not exact.");
                }
            }
            if (result.IsSuccess)
            {
                reservation.TerminalStatus = status;
                TryRetireConsumedVirtualIoInvocation(reservation);
                if (status == ResponsePublicationStatus.Cancelled && reservation.ServiceAccepted)
                    reservation.AcceptedResponseCancelled = true;
                if (reservation.CancellationScope is { } scope)
                {
                    var disposition = status == ResponsePublicationStatus.Published
                        ? reservation.FailedSettlementMayHaveEffect
                            ? CancellationDisposition.TooLateEffectMayExist
                            : CancellationDisposition.CompletedBeforeCancellation
                        : reservation.ServiceAccepted || reservation.FailedSettlementMayHaveEffect
                            ? CancellationDisposition.TooLateEffectMayExist
                            : CancellationDisposition.CancelledBeforeEffect;
                    if (status == ResponsePublicationStatus.Cancelled || reservation.CancellationRequested)
                        outcome = (reservation.Caller, scope, disposition);
                }
            }
            else
            {
                // A failed callback result does not establish whether the callback
                // crossed an irreversible response or external-effect boundary.
                reservation.FailedSettlementMayHaveEffect = true;
            }
        }
        if (outcome is { } observed)
            _ = _cancellationScopes.RecordDisposition(observed.Caller, observed.Scope, observed.Disposition);
        return result;
    }

    internal int PendingCount(EndpointSessionHandle session)
    {
        lock (_gate)
        {
            return _ownedRecords.Values.Count(record =>
                record.Handle.Session == session &&
                (HasUnclosedPossibleEffect(record) || record.TerminalStatus is null && !record.OriginalSendNoEffect ||
                 record.ResourceDonation?.State == ResourceDonationState.Active));
        }
    }

    internal int UnclosedPossibleEffectCount(EndpointSessionHandle session)
    {
        lock (_gate)
            return _ownedRecords.Values.Count(record =>
                record.Handle.Session == session && HasUnclosedPossibleEffect(record));
    }

    // Exact, non-authoritative owner observation. Absence of a retained record is
    // not closure evidence; only the owner transition can release its pin.
    internal InvocationConsequenceSnapshot? SnapshotConsequence(EndpointSessionInvocationHandle handle)
    {
        lock (_gate)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return null;
            var record = resolved.Value!;
            return new(handle, record.Delivered, record.ServiceAccepted,
                record.SettlementInProgress, record.TerminalStatus,
                HasUnclosedPossibleEffect(record), record.ResourceDonation?.State);
        }
    }

    internal KernelResult<ResourceDonationBinding> BindResourceDonation(ResourceDonationBinding binding)
    {
        lock (_gate)
        {
            var resolved = Resolve(binding.Invocation);
            if (!resolved.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.Caller != binding.Caller || record.Service != binding.Service)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.WrongSessionOwner, "Donation provenance does not match the exact invocation peers.");
            if (record.TerminalStatus is not null || record.ResourceDonation is not null ||
                record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition, "Invocation is terminal or already has a resource donation.");
            record.ResourceDonation = binding;
            return KernelResult<ResourceDonationBinding>.Ok(binding);
        }
    }

    internal KernelResult ValidateResourceDonationBindingAdmission(
        EndpointSessionInvocationHandle invocation, ProcessHandle caller, ProcessHandle service)
    {
        lock (_gate)
        {
            var resolved = ResolveForCaller(invocation, caller);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.Service != service)
                return KernelResult.Fail(KernelError.WrongSessionOwner, "Donation service generation does not match.");
            if (record.DonationBindingInProgress || record.TerminalStatus is not null || record.ResourceDonation is not null ||
                record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect)
                return KernelResult.Fail(KernelError.InvalidTransition, "Invocation cannot admit a new donation binding.");
            return KernelResult.Ok();
        }
    }

    internal KernelResult BeginResourceDonationBinding(EndpointSessionInvocationHandle invocation,
        ProcessHandle caller, ProcessHandle service, out Action release)
    {
        release = static () => { };
        lock (_gate)
        {
            var admission = ValidateResourceDonationBindingAdmission(invocation, caller, service);
            if (!admission.IsSuccess) return admission;
            var record = Resolve(invocation).Value!;
            record.DonationBindingInProgress = true;
            release = () => { lock (_gate) record.DonationBindingInProgress = false; };
            return KernelResult.Ok();
        }
    }

    internal KernelResult<ResourceDonationBinding> ResolveResourceDonation(
        EndpointSessionInvocationHandle invocation, ProcessHandle service, bool requireAdmission = false)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(resolved.Error, resolved.Message!);
            var donation = resolved.Value!.ResourceDonation;
            if (requireAdmission && (resolved.Value.DonationReturnInProgress || resolved.Value.TerminalStatus is not null || resolved.Value.SettlementInProgress is not null ||
                resolved.Value.FailedSettlementMayHaveEffect))
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "Invocation settlement prevents new donation consumption.");
            return donation is not null && donation.State is ResourceDonationState.Bound or ResourceDonationState.Active
                ? KernelResult<ResourceDonationBinding>.Ok(donation)
                : KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition, "Invocation has no live resource donation.");
        }
    }

    internal KernelResult<ResourceDonationBinding> BindNestedResourceDonation(
        ResourceDonationBinding expectedParent, ResourceDonationBinding child)
    {
        lock (_gate)
        {
            var parent = ResolveResourceDonation(expectedParent.Invocation, expectedParent.Service, requireAdmission: true);
            if (!parent.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(parent.Error, parent.Message!);
            if (!ReferenceEquals(parent.Value, expectedParent) || parent.Value!.State != ResourceDonationState.Bound)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition, "Nested donation parent changed before publication.");
            return BindResourceDonation(child);
        }
    }

    internal KernelResult<ResourceDonationBinding> InspectResourceDonation(
        EndpointSessionInvocationHandle invocation, ProcessHandle service, bool requirePreSubmitClosure = false)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(resolved.Error, resolved.Message!);
            if (requirePreSubmitClosure && (resolved.Value!.SettlementInProgress is not null ||
                resolved.Value.FailedSettlementMayHaveEffect))
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "Ambiguous invocation settlement cannot prove pre-submit donation closure.");
            return resolved.Value!.ResourceDonation is { } donation
                ? KernelResult<ResourceDonationBinding>.Ok(donation)
                : KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition, "Invocation has no resource donation.");
        }
    }

    internal KernelResult<ResourceDonationBinding> ActivateResourceDonation(
        EndpointSessionInvocationHandle invocation, ProcessHandle service, ResourceDonationBinding? expected = null)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(resolved.Error, resolved.Message!);
            var donation = resolved.Value!.ResourceDonation;
            if (donation is null || donation.State != ResourceDonationState.Bound ||
                expected is not null && !ReferenceEquals(donation, expected))
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "Only a bound resource donation can cross the possible-submit boundary.");
            if (resolved.Value.DonationReturnInProgress || resolved.Value.TerminalStatus is not null || resolved.Value.SettlementInProgress is not null ||
                resolved.Value.FailedSettlementMayHaveEffect)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "Invocation settlement prevents new donation activation.");
            var active = donation with { State = ResourceDonationState.Active };
            resolved.Value.ResourceDonation = active;
            return KernelResult<ResourceDonationBinding>.Ok(active);
        }
    }

    internal KernelResult<ResourceDonationBinding> ReturnResourceDonationPreSubmit(
        EndpointSessionInvocationHandle invocation, ProcessHandle service,
        Func<ResourceDonationBinding, KernelResult> cancelBudget)
    {
        ArgumentNullException.ThrowIfNull(cancelBudget);
        Record record;
        ResourceDonationBinding donation;
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            if (record.OriginalSendPending ||
        record.DonationReturnInProgress || record.SettlementInProgress is not null ||
                record.FailedSettlementMayHaveEffect || record.ResourceDonation is not
                { State: ResourceDonationState.Bound or ResourceDonationState.Returned } current)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "Donation lacks exact pre-submit return admission.");
            donation = current;
            record.DonationReturnInProgress = true;
        }
        try
        {
            var cancelled = cancelBudget(donation);
            lock (_gate)
            {
                if (!cancelled.IsSuccess)
                    return KernelResult<ResourceDonationBinding>.Fail(cancelled.Error, cancelled.Message!);
                if (record.ResourceDonation != donation)
                    return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                        "Session loss changed donation closure during budget cancellation.");
                var returned = donation with { State = ResourceDonationState.Returned };
                record.ResourceDonation = returned;
                return KernelResult<ResourceDonationBinding>.Ok(returned);
            }
        }
        catch
        {
            lock (_gate)
                record.ResourceDonation = donation with { State = ResourceDonationState.Quarantined };
            throw;
        }
        finally
        {
            lock (_gate) record.DonationReturnInProgress = false;
        }
    }

    internal KernelResult<ResourceDonationBinding> CloseResourceDonation(
        EndpointSessionInvocationHandle invocation, ProcessHandle service, ResourceDonationState terminal)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(resolved.Error, resolved.Message!);
            var donation = resolved.Value!.ResourceDonation;
            if (donation is null)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition, "Invocation has no resource donation.");
            if (resolved.Value.DonationReturnInProgress)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "Donation return already has an owner winner.");
            if (terminal == ResourceDonationState.Returned &&
                (resolved.Value.SettlementInProgress is not null || resolved.Value.FailedSettlementMayHaveEffect))
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "Ambiguous invocation settlement cannot prove pre-submit donation closure.");
            if (donation.State is ResourceDonationState.Returned or ResourceDonationState.Quarantined or ResourceDonationState.Closed)
                return donation.State == terminal
                    ? KernelResult<ResourceDonationBinding>.Ok(donation)
                    : KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                        "A terminal donation cannot change its closure consequence.");
            if (donation.State == ResourceDonationState.Active && terminal == ResourceDonationState.Returned)
                return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidTransition,
                    "A donation past possible submit cannot be returned as pre-submit closure.");
            var closed = donation with { State = terminal };
            resolved.Value.ResourceDonation = closed;
            return KernelResult<ResourceDonationBinding>.Ok(closed);
        }
    }

    internal ResourceDonationBinding[] CloseSession(EndpointSessionHandle session)
    {
        lock (_gate)
        {
            var keys = _ownedRecords.Keys
                .Where(key => key.Session == session.SessionId && key.SessionGeneration == session.Generation)
                .ToArray();
            var donations = keys
                .Select(key => _ownedRecords[key].ResourceDonation)
                .Where(donation => donation is not null &&
                    donation.State is ResourceDonationState.Bound or ResourceDonationState.Active)
                .Cast<ResourceDonationBinding>()
                .ToArray();
            foreach (var key in keys)
            {
                var record = _ownedRecords[key];
                if (record.ResourceDonation is { State: ResourceDonationState.Bound or ResourceDonationState.Active } live)
                    record.ResourceDonation = live with { State = ResourceDonationState.Quarantined };
                if (!HasUnclosedPossibleEffect(record) &&
                    (record.OriginalVirtualIoTransfer is null || record.VirtualIoTransferReadReleased || record.VirtualIoTransferContained || record.VirtualIoTransferNoEffect || record.VirtualIoTransferReadReleased) &&
                    (record.OriginalVirtualIoBind is null || record.VirtualIoBindContained) &&
                    (record.OriginalVirtualIoClose is null || record.VirtualIoCloseContained) &&
                    (record.OriginalVirtualCreate is null || record.VirtualCreateContained || record.VirtualCreateNoEffect || record.VirtualCreateNeverInvoked) &&
                    (record.OriginalVirtualArtifactOwner is null || record.VirtualArtifactContained) &&
                    (record.OriginalVirtualTransitionOwner is null || record.VirtualTransitionContained || record.VirtualTransitionNeverInvoked || record.VirtualTransitionNoEffect))
                {
                    _ownedRecords.Remove(key);
                    if (_records.TryGetValue(key, out var routed) && ReferenceEquals(routed, record))
                        _records.Remove(key);
                }
            }
            return donations;
        }
    }

    internal void ObserveConsumedVirtualIoInvocation(ProcessHandle caller,ChannelEndpointHandle endpoint,ResponseEnvelope raw)
    {
        lock(_gate)
        {
            foreach(var record in _ownedRecords.Values.Where(r=>r.Caller==caller && r.Handle.InvocationId.Value==raw.RequestSequence &&
                r.OriginalResponse is { } response && response.Endpoint.ChannelId==endpoint.ChannelId).ToArray())
                TryRetireConsumedVirtualIoInvocation(record);
        }
    }

    private void TryRetireConsumedVirtualIoInvocation(Record record)
    {
        // Each actual IO consumer supplies its own exact closure or trusted
        // pre-invocation fact. A delivered reply cannot stand in for that fact.
        var lifetimeClosed=record.MessageId switch
        {
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync =>
                !record.ServiceAccepted && record.OriginalVirtualCreate is null && HasVirtualizationPreAcceptanceRefusal(record),
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync =>
                record.OriginalVirtualIoTransfer is not null
                    ? record.VirtualIoTransferReadReleased || record.VirtualIoTransferNoEffect || record.VirtualIoTransferContained
                    : record.VirtualIoTransferNeverInvoked,
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync =>
                record.OriginalVirtualIoBind is not null ? record.VirtualIoBindContained : record.VirtualIoBindNeverInvoked,
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseIoAsync =>
                record.OriginalVirtualIoClose is not null
                    ? record.VirtualIoCloseBinding is not null && record.VirtualIoCloseContained
                    : record.VirtualIoCloseBinding is null && record.VirtualIoCloseNeverInvoked,
            _ => false
        };
        if(!lifetimeClosed || !record.ServiceAccepted && !HasVirtualizationPreAcceptanceRefusal(record) || HasUnclosedPossibleEffect(record) ||
            record.ResourceDonation is {State:not (ResourceDonationState.Returned or ResourceDonationState.Closed)} ||
            record.OriginalVirtualIoBind is not null && !record.VirtualIoBindContained ||
            record.OriginalVirtualIoClose is not null && !record.VirtualIoCloseContained ||
            record.OriginalVirtualCreate is not null && !record.VirtualCreateContained && !record.VirtualCreateNoEffect && !record.VirtualCreateNeverInvoked ||
            record.OriginalVirtualArtifactOwner is not null && !record.VirtualArtifactContained ||
            record.OriginalVirtualTransitionOwner is not null && !record.VirtualTransitionContained && !record.VirtualTransitionNoEffect && !record.VirtualTransitionNeverInvoked ||
            record.SettlementOutcome is not { } raw || record.TerminalStatus!=raw.Status ||
            record.OriginalResponse is not { } response || !response.Owner.ReadConsumedPublication(response,raw))return;
        var key=Key(record.Handle);
        if(!OwnsOriginalRecord(record) || _records.TryGetValue(key,out var routed) && !ReferenceEquals(routed,record))return;
        _ownedRecords.Remove(key);
        if(_records.TryGetValue(key,out routed) && ReferenceEquals(routed,record))_records.Remove(key);
        // Captured original publications still retain this record for exact reconciliation.
    }
    private static bool HasUnclosedPossibleEffect(Record record) =>
        record.OriginalSendPending ||
        record.DonationReturnInProgress ||
        record.SettlementInProgress is not null ||
        record.ServiceAccepted && record.TerminalStatus is null ||
        record.AcceptedResponseCancelled && !record.VirtualIoTransferReadReleased && !record.VirtualIoTransferContained && !record.VirtualIoTransferNoEffect && !record.VirtualIoTransferNeverInvoked && !record.VirtualIoBindContained && !record.VirtualIoBindNeverInvoked && !record.VirtualIoCloseContained && !record.VirtualIoCloseNeverInvoked && !record.VirtualCreateContained && !record.VirtualCreateNeverInvoked && !record.VirtualCreateNoEffect && !record.ComputeCopyContained && !record.VirtualArtifactContained && !record.VirtualTransitionContained && !record.VirtualTransitionNeverInvoked && !record.VirtualTransitionNoEffect ||
        record.OriginalComputeCopy is not null && !record.ComputeCopyContained ||
        record.FailedSettlementMayHaveEffect ||
        record.ResourceDonation?.State == ResourceDonationState.Quarantined;

    private static bool IsVirtualControlTransition(uint message) => message is
        SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_ConfigureAsync or
        SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_ParkAsync or
        SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_ResumeAsync or
        SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_DestroyAsync or
        SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_InjectEventAsync or
        SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseGuestRegionMappingAsync;

    internal KernelResult BindVirtualTransition(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original,VirtualDomainHandle domain)
    {
        lock(_gate)
        {
            var found=ResolveForCaller(invocation,caller);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(original is null || !IsVirtualControlTransition(record.MessageId) || !record.ServiceAccepted || record.TerminalStatus is not null ||
                record.VirtualTransitionContained || record.VirtualTransitionNeverInvoked || record.VirtualTransitionNoEffect ||
                record.OriginalVirtualTransitionOwner is { } prior && (!ReferenceEquals(prior,original) || record.VirtualTransitionDomain!=domain))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot admit this domain transition.");
            record.OriginalVirtualTransitionOwner=original;record.VirtualTransitionDomain=domain;
            return KernelResult.Ok();
        }
    }

    // Trusted local validation or producer's exact NotAccepted consequence;
    // neither permits freeing the independently existing domain/backing.
    internal KernelResult ConsumeVirtualTransitionNoEffect(EndpointSessionInvocationHandle invocation,ProcessHandle caller,
        object? original,bool neverInvoked=false)
    {
        lock(_gate)
        {
            var found=ResolveForCaller(invocation,caller);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(!IsVirtualControlTransition(record.MessageId) || !record.ServiceAccepted || record.VirtualTransitionContained ||
                !ReferenceEquals(record.OriginalVirtualTransitionOwner,original) || !neverInvoked && original is null ||
                original is null && (record.TerminalStatus is not null || record.SettlementInProgress is not null))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original transition no-effect owner changed.");
            if(neverInvoked)record.VirtualTransitionNeverInvoked=true;else record.VirtualTransitionNoEffect=true;
            return KernelResult.Ok();
        }
    }

    // Only the exact domain owner calls this inside validated provider closure
    // and local mapping/backing cleanup, before its original record retires.
    internal KernelResult ConsumeVirtualTransitionDomainClosure(ProcessHandle caller,object original,VirtualDomainHandle domain)
    {
        lock(_gate)
        {
            var records=_ownedRecords.Values.Where(r=>r.VirtualTransitionDomain==domain).ToArray();
            foreach(var record in records)
            {
                var current=ResolveForCaller(record.Handle,caller);
                if(!current.IsSuccess || !ReferenceEquals(current.Value,record) || !ReferenceEquals(record.OriginalVirtualTransitionOwner,original))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original domain transition invocation route changed.");
            }
            foreach(var record in records)record.VirtualTransitionContained=true;
            return KernelResult.Ok();
        }
    }

    internal KernelResult BindVirtualCreation(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(original is null || record.MessageId is not (SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync or
                SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateNestedAsync) ||
                !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualCreateContained || record.VirtualCreateNeverInvoked || record.VirtualCreateNoEffect ||
                record.OriginalVirtualCreate is { } prior && !ReferenceEquals(prior,original))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot admit this virtual creation.");
            if(record.CapturedVirtualCreate is { } captured && !ReferenceEquals(captured,original))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original creation anchor changed.");
            record.CapturedVirtualCreate=original;record.OriginalVirtualCreate=original;return KernelResult.Ok();
        }
    }
    // Called only by the Create owner's validation branches or original pre-effect retirement.
    // This is a never-invoked consequence, not provider closure or unknown-effect containment.
    internal KernelResult ConsumeVirtualCreationNeverInvoked(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object? original=null)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(record.MessageId is not (SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync or
                SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateNestedAsync) ||
                !record.ServiceAccepted || original is null && (record.TerminalStatus is not null || record.SettlementInProgress is not null) ||
                !ReferenceEquals(record.OriginalVirtualCreate,original) || record.VirtualCreateContained || record.VirtualCreateNoEffect)
                return KernelResult.Fail(KernelError.InvalidTransition,"Create no-effect requires its own never-invoked validation branch.");
            record.VirtualCreateNeverInvoked=true;
            return KernelResult.Ok();
        }
    }
    internal KernelResult ConsumeVirtualCreationClosure(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            if(original is null || !ReferenceEquals(resolved.Value!.OriginalVirtualCreate,original))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original creation invocation owner changed.");
            resolved.Value.VirtualCreateContained=true;return KernelResult.Ok();
        }
    }
    // Trusted teardown consumption after original channel closure, before a failed reply action.
    internal KernelResult RetireNoEffectVirtualCreationReply(EndpointSessionInvocationHandle invocation,ProcessHandle caller)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(!(record.VirtualCreateNeverInvoked || record.VirtualCreateNoEffect) || record.OriginalVirtualCreate is null ||
                record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect ||
                record.ResourceDonation is not null)
                return KernelResult.Fail(KernelError.InvalidTransition,"Original never-invoked creation and closed reply lifetime required.");
            if(record.TerminalStatus is null)
            {
                record.TerminalStatus=ResponsePublicationStatus.Cancelled;
                record.AcceptedResponseCancelled=true;
            }
            return KernelResult.Ok();
        }
    }
    internal KernelResult ConsumeVirtualCreationNoEffect(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(original is null || !ReferenceEquals(record.OriginalVirtualCreate,original) || record.VirtualCreateContained || record.VirtualCreateNeverInvoked)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Exact original provider no-effect owner required.");
            record.VirtualCreateNoEffect=true;return KernelResult.Ok();
        }
    }

    internal KernelResult BindVirtualIoClose(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original,VirtualIoBinding binding)
    {
        lock(_gate)
        {
            var found=ResolveForCaller(invocation,caller);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(original is null || record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseIoAsync ||
                !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualIoCloseContained || record.VirtualIoCloseNeverInvoked ||
                record.OriginalVirtualIoClose is { } prior && (!ReferenceEquals(prior,original) || record.VirtualIoCloseBinding!=binding))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot bind this IO Close lifetime.");
            record.OriginalVirtualIoClose=original;record.VirtualIoCloseBinding=binding;
            return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeVirtualIoCloseNeverInvoked(EndpointSessionInvocationHandle invocation,ProcessHandle caller)
    {
        lock(_gate)
        {
            var found=ResolveForCaller(invocation,caller);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseIoAsync || !record.ServiceAccepted ||
                record.OriginalVirtualIoClose is not null || record.VirtualIoCloseBinding is not null || record.TerminalStatus is not null || record.SettlementInProgress is not null)
                return KernelResult.Fail(KernelError.InvalidTransition,"Original IO Close already bound or settled.");
            record.VirtualIoCloseNeverInvoked=true;return KernelResult.Ok();
        }
    }

    // Exact composite IO/device/backing cleanup calls this before local retirement.
    // Routing and same owner are validated for all original invocations before mutation.
    internal KernelResult ConsumeVirtualIoCloseClosure(ProcessHandle caller,object original,VirtualIoBinding binding)
    {
        lock(_gate)
        {
            var records=_ownedRecords.Values.Where(r=>r.VirtualIoCloseBinding==binding).ToArray();
            foreach(var record in records)
            {
                var current=ResolveForCaller(record.Handle,caller);
                if(!current.IsSuccess || !ReferenceEquals(current.Value,record) || !ReferenceEquals(record.OriginalVirtualIoClose,original))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original IO Close invocation route/owner changed.");
            }
            foreach(var record in records)
            {
                record.VirtualIoCloseContained=true;
                TryRetireConsumedVirtualIoInvocation(record);
            }
            return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeVirtualIoBindNeverInvoked(EndpointSessionInvocationHandle invocation,ProcessHandle caller)
    {
        lock(_gate)
        {
            var found=ResolveForCaller(invocation,caller);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync || !record.ServiceAccepted ||
                record.OriginalVirtualIoBind is not null || record.VirtualIoBindContained || record.TerminalStatus is not null || record.SettlementInProgress is not null)
                return KernelResult.Fail(KernelError.InvalidTransition,"Original IO Bind already admitted or settled.");
            record.VirtualIoBindNeverInvoked=true;return KernelResult.Ok();
        }
    }

    internal KernelResult BindVirtualIoAdmission(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(original is null || record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync ||
                !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualIoBindContained || record.VirtualIoBindNeverInvoked ||
                record.OriginalVirtualIoBind is { } prior && !ReferenceEquals(prior,original))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot admit this IO Bind.");
            record.OriginalVirtualIoBind=original;return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeVirtualIoAdmissionClosure(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            if(original is null || !ReferenceEquals(resolved.Value!.OriginalVirtualIoBind,original))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original IO Bind invocation owner changed.");
            resolved.Value.VirtualIoBindContained=true;
            TryRetireConsumedVirtualIoInvocation(resolved.Value);return KernelResult.Ok();
        }
    }

    internal KernelResult BindVirtualArtifact(EndpointSessionInvocationHandle invocation, ProcessHandle caller,
        object originalDomain, GuestRegionMappingHandle mapping)
        => BindVirtualGuestEffect(invocation,caller,originalDomain,mapping,false);

    internal KernelResult BindVirtualGuestMapping(EndpointSessionInvocationHandle invocation,ProcessHandle caller,
        object originalDomain,GuestRegionMappingHandle mapping)
        => BindVirtualGuestEffect(invocation,caller,originalDomain,mapping,true);

    private KernelResult BindVirtualGuestEffect(EndpointSessionInvocationHandle invocation,ProcessHandle caller,
        object originalDomain,GuestRegionMappingHandle mapping,bool mappingAdmission)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            var protocol=record.MessageId;
            if(originalDomain is null || !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualArtifactContained ||
                (mappingAdmission ? protocol!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_MapGuestRegionAsync :
                protocol != SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindExecutableArtifactAsync &&
                protocol != SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindExecutablePackageAsync &&
                protocol != SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseGuestRegionMappingAsync &&
                protocol != SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_StartAsync) ||
                record.OriginalVirtualArtifactOwner is { } prior && (!ReferenceEquals(prior,originalDomain) || record.VirtualArtifactMapping!=mapping))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot admit this artifact mapping.");
            record.OriginalVirtualArtifactOwner=originalDomain;record.VirtualArtifactMapping=mapping;
            return KernelResult.Ok();
        }
    }

    // Called only by exact guest + parent mapping cleanup, after artifact release
    // and quantitative mapping closure. Correlation is not a closure producer.
    internal KernelResult ConsumeVirtualArtifactMappingClosure(ProcessHandle caller,object originalDomain,GuestRegionMappingHandle mapping)
    {
        lock(_gate)
        {
            var records=_ownedRecords.Values.Where(record=>record.VirtualArtifactMapping==mapping).ToArray();
            foreach(var record in records)
            {
                var resolved=ResolveForCaller(record.Handle,caller);
                if(!resolved.IsSuccess || !ReferenceEquals(resolved.Value,record) || !ReferenceEquals(record.OriginalVirtualArtifactOwner,originalDomain))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original artifact invocation route changed.");
            }
            foreach(var record in records)record.VirtualArtifactContained=true;
            return KernelResult.Ok();
        }
    }

    // Existing invocation owns its correlation; the kernel IO owner supplies the
    // same original fact before effect and only consumes it inside exact IO closure.
    internal KernelResult ConsumeVirtualIoTransferNeverInvoked(EndpointSessionInvocationHandle invocation,ProcessHandle caller)
    {
        lock(_gate)
        {
            var found=ResolveForCaller(invocation,caller);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync || !record.ServiceAccepted ||
                record.OriginalVirtualIoTransfer is not null || record.VirtualIoTransferContained || record.TerminalStatus is not null || record.SettlementInProgress is not null)
                return KernelResult.Fail(KernelError.InvalidTransition,"Original IO transfer already admitted or settled.");
            record.VirtualIoTransferNeverInvoked=true;return KernelResult.Ok();
        }
    }

    internal KernelResult BindVirtualIoTransfer(EndpointSessionInvocationHandle invocation, ProcessHandle caller, object original)
    {
        if(original is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original IO transfer owner is required.");
        lock (_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(record.MessageId != SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync ||
                !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualIoTransferReadReleased || record.VirtualIoTransferContained || record.VirtualIoTransferNoEffect || record.VirtualIoTransferNeverInvoked ||
                record.OriginalVirtualIoTransfer is { } prior && !ReferenceEquals(prior,original))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot admit this IO transfer.");
            record.OriginalVirtualIoTransfer=original;
            return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeVirtualIoTransferNoEffect(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var found=ResolveForCaller(invocation,caller);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            if(original is null || !ReferenceEquals(found.Value!.OriginalVirtualIoTransfer,original))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original transfer no-effect owner changed.");
            found.Value.VirtualIoTransferNoEffect=true;
            TryRetireConsumedVirtualIoInvocation(found.Value);return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeVirtualIoTransferClosure(EndpointSessionInvocationHandle invocation, ProcessHandle caller, object original,bool snapshotOnly=false,
        Func<KernelResult>? consumeOriginalCharge=null)
    {
        if(original is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original IO transfer owner is required.");
        lock (_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(!ReferenceEquals(record.OriginalVirtualIoTransfer,original))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original invocation IO transfer owner changed.");
            // Internal quantitative owner commit only, no provider/user callbacks.
            // Keep exact invocation identity pinned until charge consumption succeeds.
            if(consumeOriginalCharge is not null)
            {
                var consumed=consumeOriginalCharge();
                if(!consumed.IsSuccess)return consumed;
            }
            if(snapshotOnly)record.VirtualIoTransferReadReleased=true;else record.VirtualIoTransferContained=true;
            TryRetireConsumedVirtualIoInvocation(record);
            return KernelResult.Ok();
        }
    }

    internal KernelResult BindComputeCopy(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        if (original is null) return KernelResult.Fail(KernelError.PlatformFaulted, "Original compute copy owner is required.");
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.MessageId != SingPlus.Sip.Compute.IComputeServiceProtocol.Message_CopyAsync ||
                !record.Delivered || record.TerminalStatus is not null || record.ComputeCopyContained ||
                record.OriginalComputeCopy is { } prior && !ReferenceEquals(prior, original))
                return KernelResult.Fail(KernelError.InvalidTransition, "Original invocation cannot admit this compute copy.");
            record.OriginalComputeCopy = original;
            return KernelResult.Ok();
        }
    }

    // Only the trusted composition consumes its retained original after DSC1,
    internal KernelResult ValidateComputeCopyOwner(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            return ReferenceEquals(resolved.Value!.OriginalComputeCopy, original)
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformFaulted, "Original invocation compute copy owner changed before cleanup.");
        }
    }

    internal bool HasComputeResponseSettlement(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original, bool cancelledOnly = false)
    {
        lock (_gate)
        {
            var resolved=ResolveForService(invocation,service);
            if (!resolved.IsSuccess) return false;
            var record=resolved.Value!;
            if (!(ReferenceEquals(record.OriginalComputeCopy,original) && record.TerminalStatus is not null &&
                (!cancelledOnly || record.TerminalStatus==ResponsePublicationStatus.Cancelled) &&
                record.SettlementInProgress is null && !record.FailedSettlementMayHaveEffect &&
                record.SettlementOutcome is { } outcome && outcome.Status==record.TerminalStatus &&
                outcome.RequestSequence==invocation.InvocationId.Value && outcome.MessageId==record.MessageId)) return false;
            if (record.OriginalResponse is not { } response) return false;
            if (outcome.Status==ResponsePublicationStatus.Published)
            {
                var published=response.Owner.ReadPublication(response);
                return published.IsSuccess && ReferenceEquals(published.Value,outcome);
            }
            if (response.Owner.IsOriginalFailedTransferCompletion(response,outcome)) return true;
            var cancellation=response.Owner.ReadOriginalCancellation(response);
            if(cancellation.IsSuccess && ReferenceEquals(cancellation.Value,outcome)) return true;
            var channelCancellation=response.Owner.ReadOriginalChannelCancellation(response);
            return channelCancellation.IsSuccess && ReferenceEquals(channelCancellation.Value,outcome);
        }
    }

    internal bool HasBoundComputeCopy(EndpointSessionInvocationHandle invocation, ProcessHandle service)
    {
        lock (_gate)
        {
            var resolved=ResolveForService(invocation,service);
            return resolved.IsSuccess && resolved.Value!.OriginalComputeCopy is not null;
        }
    }

    internal KernelResult<ResponseEnvelope> ObserveComputeChannelCancellation(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        lock(_gate)
        {
            var found=ResolveForService(invocation,service);
            if(!found.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(!ReferenceEquals(record.OriginalComputeCopy,original) || record.OriginalResponse is not { } response ||
                record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original compute response remains unresolved.");
            var cancellation=response.Owner.ReadOriginalChannelCancellation(response);
            if(!cancellation.IsSuccess)return cancellation;
            var raw=cancellation.Value!;
            if(record.TerminalStatus is not null && (record.TerminalStatus!=raw.Status || !ReferenceEquals(record.SettlementOutcome,raw)))
                return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original compute terminal response changed.");
            record.SettlementOutcome=raw;record.TerminalStatus=raw.Status;
            return cancellation; // Response cancellation only; native/Region/borrow obligations remain.
        }
    }

    // Only the trusted composition consumes its retained original after DSC1,
    // mappings, borrow and destination custody have all finished exact cleanup.
    internal KernelResult ConsumeComputeCopyClosure(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        if (original is null) return KernelResult.Fail(KernelError.PlatformFaulted, "Original compute copy owner is required.");
        lock (_gate)
        {
            var resolved = ResolveForService(invocation, service);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!ReferenceEquals(record.OriginalComputeCopy, original))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original invocation compute copy owner changed.");
            record.ComputeCopyContained = true;
            return KernelResult.Ok();
        }
    }

    private KernelResult<Record> ResolveForCaller(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller)
    {
        var resolved = Resolve(handle);
        if (!resolved.IsSuccess) return resolved;
        return resolved.Value!.Caller == caller
            ? resolved
            : KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Caller does not own the endpoint session invocation.");
    }

    private KernelResult<Record> ResolveForService(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service)
    {
        var resolved = Resolve(handle);
        if (!resolved.IsSuccess) return resolved;
        return resolved.Value!.Service == service
            ? resolved
            : KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Service does not own the endpoint session invocation peer.");
    }

    internal KernelResult ReadPublishedOutcome(EndpointSessionInvocationHandle handle,ProcessHandle service,object originalPayload)
    {
        lock(_gate)
        {
            if(!_ownedRecords.TryGetValue(Key(handle),out var record) || record.Handle!=handle || record.Service!=service ||
                !OwnsOriginalRecord(record) || _records.TryGetValue(Key(handle),out var routed) && !ReferenceEquals(routed,record))
                return KernelResult.Fail(KernelError.WrongSessionOwner,"Original settlement owner absent/foreign.");
            if(record.TerminalStatus is null && record.SettlementInProgress is null && record.SettlementNeverStarted && !record.FailedSettlementMayHaveEffect)
                return KernelResult.Fail(KernelError.ResponseNotDelivered,"Original response action never started; response-only replay is allowed.");
            if(record.TerminalStatus!=ResponsePublicationStatus.Published || record.SettlementOutcome is not { } outcome ||
                outcome.RequestSequence!=handle.InvocationId.Value || outcome.MessageId!=record.MessageId ||
                outcome.Status!=ResponsePublicationStatus.Published || !ReferenceEquals(outcome.Payload,originalPayload))
                return KernelResult.Fail(KernelError.ResponseNotPending,"Exact original publication remains unresolved; no repeat.");
            return KernelResult.Ok(); // Publication metadata only, never external closure.
        }
    }

    internal sealed class OriginalPublication
    {
        internal OriginalPublication(EndpointSessionInvocationRegistry owner,object record,ProcessHandle service,object payload)
        { Owner=owner;Record=record;Service=service;Payload=payload;Original=this; }
        internal readonly EndpointSessionInvocationRegistry Owner;
        internal readonly object Record,Payload;
        internal readonly ProcessHandle Service;
        internal readonly OriginalPublication Original;
    }

    internal KernelResult<OriginalPublication> CapturePublication(EndpointSessionInvocationHandle handle,ProcessHandle service,object payload)
    {
        lock(_gate)
        {
            var found=ResolveForService(handle,service);
            return found.IsSuccess?KernelResult<OriginalPublication>.Ok(new(this,found.Value!,service,payload)):
                KernelResult<OriginalPublication>.Fail(found.Error,found.Message!);
        }
    }

    // Response lifetime only. Provider, Region and budget facts remain owned here
    // and are independently consumed by their original cleanup producers.
    internal KernelResult BindOriginalVirtualizationInputRefusal(EndpointSessionInvocationHandle invocation,ProcessHandle service,ChannelEnvelope request)
    {
        lock(_gate)
        {
            var found=ResolveForService(invocation,service);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(!record.Delivered || !ReferenceEquals(record.OriginalDeliveredRequest,request) || record.ServiceAccepted ||
                record.TerminalStatus is not null || record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect ||
                record.OriginalServiceRequest is { } captured && !ReferenceEquals(captured,request) || record.OriginalVirtualIoBind is not null ||
                record.OriginalVirtualIoTransfer is not null || record.OriginalVirtualIoClose is not null || record.VirtualIoCloseBinding is not null ||
                record.OriginalVirtualCreate is not null || record.OriginalVirtualArtifactOwner is not null || record.OriginalVirtualTransitionOwner is not null || record.OriginalComputeCopy is not null)
                return KernelResult.Fail(KernelError.InvalidTransition,"Exact original virtualization request is no longer before acceptance.");
            switch(record.MessageId)
            {
                case SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync: record.VirtualCreateNeverInvoked=true;break;
                case SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync: record.VirtualIoBindNeverInvoked=true;break;
                case SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync: record.VirtualIoTransferNeverInvoked=true;break;
                case SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseIoAsync: record.VirtualIoCloseNeverInvoked=true;break;
                default:return KernelResult.Fail(KernelError.UnsupportedPayload,"No original input-refusal producer for this message.");
            }
            record.OriginalServiceRequest=request; // Capture host original before response callbacks.
            return KernelResult.Ok();
        }
    }
    private static bool HasVirtualizationPreAcceptanceRefusal(Record record) => !record.ServiceAccepted && record.OriginalDeliveredRequest is not null &&
        ReferenceEquals(record.OriginalDeliveredRequest,record.OriginalServiceRequest) &&
        (record.MessageId switch
        {
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync => record.VirtualCreateNeverInvoked,
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync => record.VirtualIoBindNeverInvoked,
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync => record.VirtualIoTransferNeverInvoked,
            SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseIoAsync => record.VirtualIoCloseNeverInvoked,
            _ => false
        });

    internal KernelResult BindOriginalServiceRequest(EndpointSessionInvocationHandle invocation,ProcessHandle service,ChannelEnvelope request)
    {
        lock(_gate)
        {
            var found=ResolveForService(invocation,service);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(!record.ServiceAccepted || record.TerminalStatus is not null || request.Sequence!=invocation.InvocationId.Value ||
                request.MessageId!=record.MessageId || record.OriginalDeliveredRequest is { } delivered && !ReferenceEquals(delivered,request) ||
                record.OriginalServiceRequest is { } prior && !ReferenceEquals(prior,request))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original accepted request cannot be replaced.");
            record.OriginalServiceRequest=request;
            return KernelResult.Ok();
        }
    }

    internal KernelResult ReadOriginalCancelledLifetime(OriginalPublication original,EndpointSessionInvocationHandle invocation,ProcessHandle service,ChannelEnvelope request)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) || record.Service!=original.Service ||
                record.Handle!=invocation || record.Service!=service ||
                !ReferenceEquals(record.OriginalServiceRequest,request) || !(record.ServiceAccepted || HasVirtualizationPreAcceptanceRefusal(record)))
                return KernelResult.Fail(KernelError.WrongSessionOwner,"Original cancelled invocation absent/copied/foreign.");
            var key=(record.Handle.Session.SessionId,record.Handle.Session.Generation,record.Handle.InvocationId);
            if(_records.TryGetValue(key,out var routed) && !ReferenceEquals(routed,record) ||
                _ownedRecords.TryGetValue(key,out var owned) && !ReferenceEquals(owned,record))
                return KernelResult.Fail(KernelError.WrongSessionOwner,"Original cancelled invocation route substituted.");
            var cancellation=record.OriginalResponse?.Owner.ReadOriginalCancellation(record.OriginalResponse);
            if(cancellation is { IsSuccess:true,Value:{ } rawCancellation } && record.TerminalStatus==ResponsePublicationStatus.Cancelled && record.SettlementInProgress is null &&
                !record.FailedSettlementMayHaveEffect && record.SettlementOutcome is { } outcome &&
                outcome.Status==ResponsePublicationStatus.Cancelled && outcome.RequestSequence==record.Handle.InvocationId.Value &&
                outcome.MessageId==record.MessageId && ReferenceEquals(rawCancellation,outcome))
            {
                TryRetireConsumedVirtualIoInvocation(record);
                return KernelResult.Ok();
            }
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,"Original response lifetime remains unresolved.");
        }
    }

    internal OriginalPublication[] CaptureCancelledNestedCreations(ProcessHandle caller,ProcessHandle service,EndpointSessionHandle session)
    {
        lock(_gate)
            return _ownedRecords.Values.Where(r=>r.Caller==caller && r.Service==service && r.Handle.Session==session &&
                r.ServiceAccepted && r.TerminalStatus==ResponsePublicationStatus.Cancelled &&
                r.MessageId is (SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateNestedAsync or
                    SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync) &&
                !r.VirtualCreateCleanupCompleted && !r.VirtualCreateNeverInvoked && !r.VirtualCreateNoEffect)
                .Select(r=>new OriginalPublication(this,r,service,null!)).ToArray();
    }

    internal KernelResult CompleteCancelledVirtualCreation(OriginalPublication original)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Owner,this) || !ReferenceEquals(original.Original,original) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) ||
                !_ownedRecords.TryGetValue(Key(record.Handle),out var owned) || !ReferenceEquals(owned,record) ||
                !ReferenceEquals(record.OriginalVirtualCreate,record.CapturedVirtualCreate) ||
                !(record.VirtualCreateContained || record.VirtualCreateNoEffect || record.VirtualCreateNeverInvoked))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Exact cancelled creation cleanup completion unavailable.");
            record.VirtualCreateCleanupCompleted=true;return KernelResult.Ok();
        }
    }

    internal KernelResult<object?> ReadUnpublishedVirtualCreation(OriginalPublication original)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) || record.Service!=original.Service ||
                !_ownedRecords.TryGetValue(Key(record.Handle),out var owned) || !ReferenceEquals(owned,record) ||
                !_records.TryGetValue(Key(record.Handle),out var routed) || !ReferenceEquals(routed,record) ||
                record.CapturedVirtualCreate is null || !ReferenceEquals(record.OriginalVirtualCreate,record.CapturedVirtualCreate) ||
                record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect ||
                !(record.TerminalStatus is null && record.SettlementNeverStarted ||
                  record.TerminalStatus==ResponsePublicationStatus.Cancelled && record.AcceptedResponseCancelled &&
                  record.SettlementOutcome is { Status:ResponsePublicationStatus.Cancelled }))
                return KernelResult<object?>.Fail(KernelError.SessionDraining,"Original never-published creation custody unavailable.");
            return KernelResult<object?>.Ok(record.MessageId is SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateNestedAsync or
                SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync
                ? record.OriginalVirtualCreate : null);
        }
    }

    internal KernelResult ReadPublication(OriginalPublication original)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) || record.Service!=original.Service)
                return KernelResult.Fail(KernelError.WrongSessionOwner,"Original publication absent/copied/foreign.");
            if(record.TerminalStatus==ResponsePublicationStatus.Published && record.SettlementOutcome is { } outcome &&
                outcome.Status==ResponsePublicationStatus.Published && outcome.RequestSequence==record.Handle.InvocationId.Value &&
                outcome.MessageId==record.MessageId && ReferenceEquals(outcome.Payload,original.Payload))
                return KernelResult.Ok(); // Surviving fact after legitimate routing retirement; no permission/reclaim.
            return ReadPublishedOutcome(record.Handle,original.Service,original.Payload);
        }
    }

    // Pure retention lookup after the existing response owner settled. No provider/charge closure.
    internal KernelResult<object?> ReadSettledVirtualIoTransfer(OriginalPublication original)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) || record.Service!=original.Service ||
                record.SettlementInProgress is not null || record.SettlementOutcome is not { } outcome ||
                outcome.RequestSequence!=record.Handle.InvocationId.Value || outcome.MessageId!=record.MessageId)
                return KernelResult<object?>.Fail(KernelError.ResponseNotPending,"Original settled response retention unavailable.");
            var key=Key(record.Handle);
            if(_records.TryGetValue(key,out var routed) && !ReferenceEquals(routed,record) ||
                _ownedRecords.TryGetValue(key,out var owned) && !ReferenceEquals(owned,record))
                return KernelResult<object?>.Fail(KernelError.WrongSessionOwner,"Original settled response route substituted.");
            if(record.TerminalStatus==ResponsePublicationStatus.Published && outcome.Status==ResponsePublicationStatus.Published && ReferenceEquals(outcome.Payload,original.Payload) ||
                record.TerminalStatus==ResponsePublicationStatus.Cancelled && outcome.Status==ResponsePublicationStatus.Cancelled &&
                    (record.AcceptedResponseCancelled || HasVirtualizationPreAcceptanceRefusal(record)))
                return KernelResult<object?>.Ok(record.OriginalVirtualIoTransfer);
            return KernelResult<object?>.Fail(KernelError.ResponseNotPending,"Original response is not exactly settled.");
        }
    }
    internal KernelResult BindOriginalResponse(EndpointSessionInvocationHandle handle,ProcessHandle service,ResponseRegistry.OriginalPublication original)
    {
        lock(_gate)
        {
            var found=ResolveForService(handle,service);
            if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
            var record=found.Value!;
            if(record.OriginalResponse is { } prior)
                return ReferenceEquals(prior.Owner,original.Owner) && ReferenceEquals(prior.Completion,original.Completion) &&
                    prior.Responder==original.Responder && prior.Endpoint==original.Endpoint && prior.Sequence==original.Sequence?
                    KernelResult.Ok():KernelResult.Fail(KernelError.InvalidTransition,"Original response producer changed; no repeat.");
            record.OriginalResponse=original;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<ResponseRegistry.OriginalPublication> ReadFailedOriginalResponse(
        EndpointSessionInvocationHandle handle,ProcessHandle service)
    {
        lock(_gate)
        {
            var found=ResolveForService(handle,service);
            if(!found.IsSuccess || found.Value!.SettlementInProgress is not null ||
                found.Value.TerminalStatus is not null && found.Value.TerminalStatus!=ResponsePublicationStatus.Cancelled ||
                !found.Value.FailedSettlementMayHaveEffect && !found.Value.SettlementNeverStarted && found.Value.TerminalStatus is null ||
                found.Value.OriginalResponse is not { } original)
                return KernelResult<ResponseRegistry.OriginalPublication>.Fail(KernelError.ResponseNotPending,"Original failed response settlement unavailable.");
            return KernelResult<ResponseRegistry.OriginalPublication>.Ok(original);
        }
    }

    internal KernelResult CompleteFailedOriginalResponse(EndpointSessionInvocationHandle handle,ProcessHandle service,
        ResponseRegistry.OriginalPublication original,ResponseEnvelope cancelled)
    {
        lock(_gate)
        {
            var found=ReadFailedOriginalResponse(handle,service);
            if(!found.IsSuccess || !ReferenceEquals(found.Value,original) || cancelled.Status!=ResponsePublicationStatus.Cancelled ||
                !original.Owner.IsOriginalFailedTransferCompletion(original,cancelled) ||
                cancelled.RequestSequence!=handle.InvocationId.Value || cancelled.Payload is not null)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original cancelled response continuity changed.");
            var record=ResolveForService(handle,service).Value!;
            record.SettlementOutcome=cancelled;
            record.TerminalStatus=ResponsePublicationStatus.Cancelled;
            record.FailedSettlementMayHaveEffect=false; // Independent provider/copy/donation pins remain.
            record.SettlementNeverStarted=false; // Nested same-original response effect is independently closed.
            TryRetireConsumedVirtualIoInvocation(record);
            return KernelResult.Ok();
        }
    }
    internal KernelResult ReconcileResponse(OriginalPublication original,ResponseEnvelope raw,bool exactProducerPayload=false)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Owner,this) || !ReferenceEquals(original.Original,original) || original.Record is not Record record ||
                !ReferenceEquals(record.OriginalSelf,record) || record.Service!=original.Service || record.SettlementInProgress is not null ||
                raw.RequestSequence!=record.Handle.InvocationId.Value || raw.MessageId!=record.MessageId || raw.Status!=ResponsePublicationStatus.Published ||
                !ReferenceEquals(raw.Payload,original.Payload) && !exactProducerPayload || record.SettlementOutcome is { } prior && !ReferenceEquals(prior,raw))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original response settlement continuation mismatch.");
            record.SettlementOutcome=raw;record.TerminalStatus=ResponsePublicationStatus.Published;
            TryRetireConsumedVirtualIoInvocation(record);
            // Independent uncertainty/donation/closure pins are not consumed by publication metadata.
            return KernelResult.Ok();
        }
    }
    internal ResponseRegistry.OriginalPublication? OriginalResponse(OriginalPublication original)
        => ReferenceEquals(original.Owner,this) && ReferenceEquals(original.Original,original) && original.Record is Record record &&
            ReferenceEquals(record.OriginalSelf,record) ? record.OriginalResponse : null;

    internal KernelResult ReconcileOriginalChannelCancellation(OriginalPublication original,ResponseEnvelope cancelled)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
                original.Record is not Record record || !ReferenceEquals(record.OriginalSelf,record) || record.Service!=original.Service ||
                record.OriginalResponse is not { } response || !response.Owner.ReadOriginalChannelCancellation(response).IsSuccess ||
                !ReferenceEquals(response.Owner.ReadOriginalChannelCancellation(response).Value,cancelled) ||
                record.SettlementInProgress is not null || record.TerminalStatus==ResponsePublicationStatus.Published)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original channel cancellation continuity differs.");
            record.TerminalStatus=ResponsePublicationStatus.Cancelled;
            record.SettlementOutcome=cancelled;
            record.FailedSettlementMayHaveEffect=false;
            record.SettlementNeverStarted=false;
            record.AcceptedResponseCancelled=record.ServiceAccepted;
            TryRetireConsumedVirtualIoInvocation(record);
            return KernelResult.Ok(); // Provider/Region/donation closure pins remain independent.
        }
    }

    private KernelResult<Record> Resolve(EndpointSessionInvocationHandle handle)
    {
        if (handle.Generation.Value != 1)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Endpoint session invocation generation is stale.");
        if (!_records.TryGetValue(Key(handle), out var record))
            return KernelResult<Record>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation was not found or is no longer tracked.");
        return record.Handle == handle && OwnsOriginalRecord(record)
            ? KernelResult<Record>.Ok(record)
            : KernelResult<Record>.Fail(KernelError.PlatformFaulted, "Original invocation owner continuity was lost.");
    }

    private bool OwnsOriginalRecord(Record record) =>
        ReferenceEquals(record.OriginalSelf,record) && _ownedRecords.TryGetValue(Key(record.Handle), out var original) && ReferenceEquals(original, record);

    private static (EndpointSessionId, EndpointSessionGeneration, EndpointSessionInvocationId) Key(EndpointSessionInvocationHandle handle) =>
        (handle.Session.SessionId, handle.Session.Generation, handle.InvocationId);
}






