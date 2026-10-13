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
        public required EndpointSessionInvocationHandle Handle { get; init; }
        public required ProcessHandle Caller { get; init; }
        public required ProcessHandle Service { get; init; }
        public required uint MessageId { get; init; }
        public CancellationScopeHandle? CancellationScope { get; set; }
        public bool Delivered { get; set; }
        public bool ServiceAccepted { get; set; }
        public bool InFlightCancellationAllowed { get; set; }
        public bool CancellationRequested { get; set; }
        public bool CancellationAccepted { get; set; }
        public bool AcceptedResponseCancelled { get; set; }
        public object? OriginalVirtualIoTransfer { get; set; }
        public bool VirtualIoTransferContained { get; set; }
        public object? OriginalVirtualIoBind { get; set; }
        public bool VirtualIoBindContained { get; set; }
        public object? OriginalVirtualCreate { get; set; }
        public bool VirtualCreateContained { get; set; }
        public bool VirtualCreateNeverInvoked { get; set; }
        public bool VirtualCreateNoEffect { get; set; }
        public object? OriginalComputeCopy { get; set; }
        public bool ComputeCopyContained { get; set; }
        public object? OriginalVirtualArtifactOwner { get; set; }
        public GuestRegionMappingHandle? VirtualArtifactMapping { get; set; }
        public bool VirtualArtifactContained { get; set; }
        public bool FailedSettlementMayHaveEffect { get; set; }
        public bool DonationReturnInProgress { get; set; }
        public bool DonationBindingInProgress { get; set; } // Admission only.
        public ResponsePublicationStatus? SettlementInProgress { get; set; }
        public ResponsePublicationStatus? TerminalStatus { get; set; }
        public ResponseEnvelope? SettlementOutcome { get; set; } // Retained outcome, never permission.
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
            if (record.Delivered || record.TerminalStatus is not null ||
                record.SettlementInProgress is not null || record.FailedSettlementMayHaveEffect)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(KernelError.ResponseNotPending,
                    "Original invocation is no longer eligible for queued delivery.");
            return KernelResult<EndpointSessionInvocationHandle>.Ok(handle);
        }
    }

    internal KernelResult<EndpointSessionInvocationHandle> MarkDelivered(
        EndpointSessionHandle session,
        ProcessHandle service,
        ulong requestSequence)
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
            if (resolved.Value!.TerminalStatus is not null)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
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
        Func<KernelResult<ResponseEnvelope>> publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var reservation = ReserveSettlement(
            handle,
            service,
            ResponsePublicationStatus.Published,
            requireCancellationAccepted: false);
        if (!reservation.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(reservation.Error, reservation.Message!);

        return ExecuteSettlement(reservation.Value!, publication);
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
        Func<KernelResult<ResponseEnvelope>> action)
    {
        var actionStarted = false;
        try
        {
            SettlementReservedHook?.Invoke();
            lock (_settlementAdmissionGate)
            lock (_gate)
            {
                var current = Resolve(reservation.Handle);
                if (!current.IsSuccess || !ReferenceEquals(current.Value, reservation) ||
                    reservation.SettlementInProgress is null || reservation.FailedSettlementMayHaveEffect)
                {
                    reservation.SettlementInProgress = null;
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
                if (actionStarted && reservation.TerminalStatus is null)
                    reservation.FailedSettlementMayHaveEffect = true;
            }
            throw;
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
                record.DonationReturnInProgress || record.FailedSettlementMayHaveEffect && !localCompletionOnly)
                return KernelResult<Record>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal or settlement is in progress.");
            if (!record.Delivered)
                return KernelResult<Record>.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (requireCancellationAccepted == false && record.CancellationAccepted)
                return KernelResult<Record>.Fail(KernelError.InvalidTransition, "The service accepted cancellation and must settle the invocation as Cancelled.");

            record.SettlementInProgress = status;
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
                (HasUnclosedPossibleEffect(record) || record.TerminalStatus is null ||
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
            if (record.DonationReturnInProgress || record.SettlementInProgress is not null ||
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
                    (record.OriginalVirtualIoTransfer is null || record.VirtualIoTransferContained) &&
                    (record.OriginalVirtualIoBind is null || record.VirtualIoBindContained) &&
                    (record.OriginalVirtualCreate is null || record.VirtualCreateContained || record.VirtualCreateNoEffect || record.VirtualCreateNeverInvoked) &&
                    (record.OriginalVirtualArtifactOwner is null || record.VirtualArtifactContained))
                {
                    _ownedRecords.Remove(key);
                    if (_records.TryGetValue(key, out var routed) && ReferenceEquals(routed, record))
                        _records.Remove(key);
                }
            }
            return donations;
        }
    }

    private static bool HasUnclosedPossibleEffect(Record record) =>
        record.DonationReturnInProgress ||
        record.SettlementInProgress is not null ||
        record.ServiceAccepted && record.TerminalStatus is null ||
        record.AcceptedResponseCancelled && !record.VirtualIoTransferContained && !record.VirtualIoBindContained && !record.VirtualCreateContained && !record.VirtualCreateNeverInvoked && !record.VirtualCreateNoEffect && !record.ComputeCopyContained && !record.VirtualArtifactContained ||
        record.OriginalComputeCopy is not null && !record.ComputeCopyContained ||
        record.FailedSettlementMayHaveEffect ||
        record.ResourceDonation?.State == ResourceDonationState.Quarantined;

    internal KernelResult BindVirtualCreation(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(original is null || record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync ||
                !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualCreateContained || record.VirtualCreateNeverInvoked || record.VirtualCreateNoEffect ||
                record.OriginalVirtualCreate is { } prior && !ReferenceEquals(prior,original))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot admit this virtual creation.");
            record.OriginalVirtualCreate=original;return KernelResult.Ok();
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
            if(record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync ||
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

    internal KernelResult BindVirtualIoAdmission(EndpointSessionInvocationHandle invocation,ProcessHandle caller,object original)
    {
        lock(_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(original is null || record.MessageId!=SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync ||
                !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualIoBindContained ||
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
            resolved.Value.VirtualIoBindContained=true;return KernelResult.Ok();
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
    internal KernelResult BindVirtualIoTransfer(EndpointSessionInvocationHandle invocation, ProcessHandle caller, object original)
    {
        if(original is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original IO transfer owner is required.");
        lock (_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(record.MessageId != SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync ||
                !record.ServiceAccepted || record.TerminalStatus is not null || record.VirtualIoTransferContained ||
                record.OriginalVirtualIoTransfer is { } prior && !ReferenceEquals(prior,original))
                return KernelResult.Fail(KernelError.InvalidTransition,"Original invocation cannot admit this IO transfer.");
            record.OriginalVirtualIoTransfer=original;
            return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeVirtualIoTransferClosure(EndpointSessionInvocationHandle invocation, ProcessHandle caller, object original)
    {
        if(original is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original IO transfer owner is required.");
        lock (_gate)
        {
            var resolved=ResolveForCaller(invocation,caller);
            if(!resolved.IsSuccess)return KernelResult.Fail(resolved.Error,resolved.Message!);
            var record=resolved.Value!;
            if(!ReferenceEquals(record.OriginalVirtualIoTransfer,original))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original invocation IO transfer owner changed.");
            record.VirtualIoTransferContained=true;
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
        _ownedRecords.TryGetValue(Key(record.Handle), out var original) && ReferenceEquals(original, record);

    private static (EndpointSessionId, EndpointSessionGeneration, EndpointSessionInvocationId) Key(EndpointSessionInvocationHandle handle) =>
        (handle.Session.SessionId, handle.Session.Generation, handle.InvocationId);
}
