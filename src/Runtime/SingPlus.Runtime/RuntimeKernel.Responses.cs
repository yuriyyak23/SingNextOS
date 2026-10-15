using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private ResponseRegistry? _responseRegistry;
    private ResponseRegistry Responses => _responseRegistry ??= new ResponseRegistry(Regions, Channels, TransferResponseRegionForIpc);

    public KernelResult<ResponseEnvelope> PublishResponse(
        ProcessHandle responder,
        ChannelEndpointHandle endpoint,
        ulong requestSequence,
        object? payload = null)
    {
        List<BudgetTraceObservation> observations = [];
        try { return PublishResponseCore(responder, endpoint, requestSequence, payload, observations); }
        finally { EmitBudgetObservations(observations); }
    }

    private KernelResult<ResponseEnvelope> PublishResponseCore(
        ProcessHandle responder,
        ChannelEndpointHandle endpoint,
        ulong requestSequence,
        object? payload,
        List<BudgetTraceObservation> observations)
    {
        lock (_platformMemoryUseGate)
        {
        ResponseRegistry.OriginalPublication? initial=null;
        lock(_requestResponseCorrelationGate)
        {
            if(payload is SingPlus.Sip.ITransferableOwnedPayload owned)
            {
                var captured=Responses.CapturePublication(responder,endpoint,requestSequence);
                var charges=Budgets.OriginalRegionCharges(owned.Handle);
                var descriptor=Regions.Snapshot().SingleOrDefault(item=>item.Handle==owned.Handle);
                if(captured.IsSuccess && descriptor is not null && charges.Length==1 && charges[0].Owner==responder &&
                    descriptor.Owner.ProcessGeneration==responder.Generation && charges[0].RegionOwner is { } accounting)
                {
                    var exact=Regions.CaptureTransferPayload(owned.Handle,descriptor.Owner);
                    var binding=Budgets.ValidateOriginalRegionChargeBinding(charges[0].Fact,responder,charges[0].Reservation,
                        owned.Handle,accounting,descriptor.Owner.DomainId);
                    if(exact.IsSuccess && ReferenceEquals(exact.Value,owned) && binding.IsSuccess)
                    {
                        var retained=Responses.RetainInitialOwnership(new(captured.Value!,owned,owned.Handle,descriptor.Owner,accounting,charges));
                        if(!retained.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(retained.Error,retained.Message!);
                        initial=captured.Value;
                    }
                }
            }
        }
        KernelResult<ResponseEnvelope> RefuseInitial(KernelError error,string message)
        {
            if(initial is not null)
                lock(_requestResponseCorrelationGate)Responses.RetainInitialRefusal(initial,error);
            return KernelResult<ResponseEnvelope>.Fail(error,message);
        }
        var responderProcess = Processes.Resolve(responder);
        if (!responderProcess.IsSuccess)
            return RefuseInitial(responderProcess.Error,responderProcess.Message!);
        var responderEffect = EnsureProcessAcceptsNewEffects(responderProcess.Value!);
        if (!responderEffect.IsSuccess)
            return RefuseInitial(responderEffect.Error,responderEffect.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!);

            var requesterHandle = Responses.ResolveRequester(endpoint, requestSequence, responder);
            if (!requesterHandle.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(requesterHandle.Error, requesterHandle.Message!);

            var requesterProcess = Processes.Resolve(requesterHandle.Value);
            if (!requesterProcess.IsSuccess)
                return RefuseInitial(requesterProcess.Error,requesterProcess.Message!);
            var requesterEffect = EnsureProcessAcceptsNewEffects(requesterProcess.Value!);
            if (!requesterEffect.IsSuccess)
                return RefuseInitial(requesterEffect.Error,requesterEffect.Message!);

            var original=Responses.CapturePublication(responder,endpoint,requestSequence);
            if(!original.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(original.Error,original.Message!);
            return Responses.Publish(
                responderProcess.Value!,
                requesterProcess.Value!,
                endpoint,
                requestSequence,
                payload,
                (source, target, region, admission, noEffect, prepared) =>
                {
                    var result=TransferRegionForIpcCore(source,target,region,admission,observations,noEffect,prepared,
                        originalResponse:original.Value);
                    Responses.RetainTransferComposition(original.Value!,prepared,result);
                    return result;
                });
        }
        }
    }

    public KernelResult<ResponseEnvelope> CancelResponse(
        ProcessHandle responder,
        ChannelEndpointHandle endpoint,
        ulong requestSequence)
    {
        lock (_platformMemoryUseGate)
        {
        var responderProcess = Processes.Resolve(responder);
        if (!responderProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(responderProcess.Error, responderProcess.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!);

            return Responses.Cancel(responder, endpoint, requestSequence);
        }
        }
    }

    // Cleanup-only continuation of the service's retained original response.
    internal KernelResult<ResponseEnvelope> CleanupFailedSessionOwnershipResponse(ProcessHandle service,
        EndpointSessionInvocationHandle invocation,object originalInput)
        => CleanupFailedSessionOwnershipResponse(service,invocation,originalInput,out _);

    internal KernelResult<ResponseEnvelope> CleanupFailedSessionOwnershipResponse(ProcessHandle service,
        EndpointSessionInvocationHandle invocation,object originalInput,out RegionHandle closedRegion)
    {
        closedRegion=default;
        List<BudgetTraceObservation> observations=[];
        try
        {
        lock(_platformMemoryUseGate)
        lock(_requestResponseCorrelationGate)
        {
            var original=SessionInvocations.ReadFailedOriginalResponse(invocation,service);
            if(!original.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(original.Error,original.Message!);
            var read=Responses.ReadFailedTransfer(original.Value!,originalInput);
            if(!read.IsSuccess)
            {
                var initial=Responses.ReadInitialRefusal(original.Value!,originalInput);
                if(!initial.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(read.Error,read.Message!);
                var initialFact=initial.Value!;
                var initialClosed=Regions.CleanupWithdrawnPayload(initialFact.Payload,initialFact.Owner,initialFact.RegionOwner);
                if(!initialClosed.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(initialClosed.Error,initialClosed.Message!);
                var initialEnded=Regions.HasEndedOriginalAccountingLifetime(initialFact.RegionOwner,initialFact.Region);
                if(!initialEnded.IsSuccess || !initialEnded.Value)return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Initial original Region remains pinned.");
                var sourceProcess=Processes.Resolve(service);
                if(sourceProcess.IsSuccess)sourceProcess.Value!.RemoveRegion(initialFact.Region);
                foreach(var charge in initialFact.Charges)
                {
                    var binding=Budgets.ValidateOriginalRegionChargeBinding(charge.Fact,charge.Owner,charge.Reservation,initialFact.Region,initialFact.RegionOwner,initialFact.Owner.DomainId);
                    if(!binding.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(binding.Error,binding.Message!);
                    var initialPrior=Budgets.Query(charge.Reservation);
                    var consumed=Budgets.ReleaseOriginalRegionCharge(charge.Fact,charge.Owner,charge.Reservation);
                    if(!consumed.IsSuccess || consumed.Value!.State!=BudgetReservationState.Released)
                        return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Initial original charge remains pinned.");
                    RetireExactRegionBudgetRoute(initialFact.Region,charge.Owner,charge.Reservation);
                    if(initialPrior.IsSuccess && initialPrior.Value!.State!=BudgetReservationState.Released)
                        observations.Add(new(charge.Owner,TraceEventKind.BudgetReleased,charge.Reservation));
                }
                var cancelledInitial=Responses.CompleteFailedTransfer(original.Value!,originalInput);
                if(!cancelledInitial.IsSuccess)return cancelledInitial;
                var completedInitial=SessionInvocations.CompleteFailedOriginalResponse(invocation,service,original.Value!,cancelledInitial.Value!);
                if(completedInitial.IsSuccess)closedRegion=initialFact.Region;
                return completedInitial.IsSuccess ? cancelledInitial : KernelResult<ResponseEnvelope>.Fail(completedInitial.Error,completedInitial.Message!);
            }
            var fact=read.Value!;
            var noEffect=fact.BudgetRefusal is not null || fact.PreRegionRefusal is not null || fact.Raw is { IsSuccess:false };
            var raw=noEffect ? fact.SourceRegion : fact.Raw!.Value.Value;
            var resourceOwner=noEffect ? new RegionOwner(fact.SourceDomain,service.Generation) : fact.TargetOwner;
            var closed=Regions.CleanupWithdrawnPayload(noEffect ? fact.Prepared.Source : (SingPlus.Sip.ITransferableOwnedPayload)fact.Prepared.Projection,
                resourceOwner,fact.RegionOwner);
            var ended=Regions.HasEndedOriginalAccountingLifetime(fact.RegionOwner,raw);
            var cleanup=closed;
            if((!ended.IsSuccess || !ended.Value) && cleanup.IsSuccess)
                cleanup=KernelResult.Fail(KernelError.PlatformFaulted,"Original response Region remains pinned.");
            var target=Processes.Resolve(noEffect ? service : fact.Target);
            if(closed.IsSuccess && target.IsSuccess)target.Value!.RemoveRegion(raw);
            if(ended.IsSuccess && ended.Value && fact.TargetReservation is { } reservation)
            {
                if(fact.TargetBudgetOwner is null)return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original target response charge absent.");
                var prior=Budgets.Query(reservation);
                var consumed=Budgets.ConsumeClosedTransferReservation(fact.TargetBudgetOwner,fact.Target,reservation,
                    fact.Prepared,raw,fact.RegionOwner,fact.TargetOwner.DomainId);
                if(!consumed.IsSuccess)cleanup=consumed;
                else
                {
                    RetireExactRegionBudgetRoute(raw,fact.Target,reservation);
                    if(prior.IsSuccess && prior.Value!.State!=BudgetReservationState.Released)
                        observations.Add(new(fact.Target,TraceEventKind.BudgetReleased,reservation));
                }
            }
            foreach(var charge in fact.SourceCharges)
            {
                if(charge.RegionOwner is null || !ReferenceEquals(charge.RegionOwner,fact.RegionOwner))
                    return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original source response charge absent.");
                var sourceEnded=Regions.HasEndedOriginalAccountingLifetime(fact.RegionOwner,fact.SourceRegion);
                if(!sourceEnded.IsSuccess || !sourceEnded.Value)return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original source Region lifetime remains pinned.");
                var binding=Budgets.ValidateOriginalRegionChargeBinding(charge.Fact,charge.Owner,charge.Reservation,
                    fact.SourceRegion,fact.RegionOwner,fact.SourceDomain);
                if(!binding.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(binding.Error,binding.Message!);
                var prior=Budgets.Query(charge.Reservation);
                var consumed=Budgets.ReleaseOriginalRegionCharge(charge.Fact,charge.Owner,charge.Reservation);
                if(!consumed.IsSuccess || consumed.Value!.State!=BudgetReservationState.Released)
                    return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original source response charge remains pinned.");
                RetireExactRegionBudgetRoute(fact.SourceRegion,charge.Owner,charge.Reservation);
                if(prior.IsSuccess && prior.Value!.State!=BudgetReservationState.Released)
                    observations.Add(new(charge.Owner,TraceEventKind.BudgetReleased,charge.Reservation));
            }
            if(!cleanup.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(cleanup.Error,cleanup.Message!);
            var cancelled=Responses.CompleteFailedTransfer(original.Value!,originalInput);
            if(!cancelled.IsSuccess)return cancelled;
            var completed=SessionInvocations.CompleteFailedOriginalResponse(invocation,service,original.Value!,cancelled.Value!);
            if(completed.IsSuccess)closedRegion=raw;
            return completed.IsSuccess ? cancelled : KernelResult<ResponseEnvelope>.Fail(completed.Error,completed.Message!);
        }
        }
        finally { EmitBudgetObservations(observations); }
    }

    public KernelResult<ResponseEnvelope> ReceiveResponse(
        ProcessHandle requester,
        ChannelEndpointHandle endpoint)
    {
        var requesterProcess = Processes.Resolve(requester);
        if (!requesterProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(requesterProcess.Error, requesterProcess.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!);

            return Responses.Receive(requester, endpoint);
        }
    }

    public ValueTask<KernelResult<ResponseEnvelope>> WaitForResponseAsync(
        ProcessHandle requester,
        ChannelEndpointHandle endpoint,
        ulong requestSequence)
    {
        var requesterProcess = Processes.Resolve(requester);
        if (!requesterProcess.IsSuccess)
            return ValueTask.FromResult(
                KernelResult<ResponseEnvelope>.Fail(requesterProcess.Error, requesterProcess.Message!));

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
            {
                return ValueTask.FromResult(
                    KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!));
            }

            // ResponseRegistry.WaitAsync records the exact correlated waiter before its
            // first incomplete await. Holding the same gate used by publish/cancel makes
            // waiter registration and terminal completion a single ordered lifecycle.
            return Responses.WaitAsync(requester, endpoint, requestSequence, _requestResponseCorrelationGate);
        }
    }
}
