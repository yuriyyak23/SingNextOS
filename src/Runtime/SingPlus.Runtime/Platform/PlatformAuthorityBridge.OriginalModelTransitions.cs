using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class OriginalModelTransitionFact
    {
        public OriginalModelTransitionFact(VirtualDomainRecord owner,PlatformOriginalVirtualDomainTransitionRequest request,
            PlatformBackendEpoch epoch,Func<KernelResult>? noEffectConsumer)
        {OriginalSelf=this;Owner=owner;Request=request;Epoch=epoch;NoEffectConsumer=noEffectConsumer;}
        public readonly OriginalModelTransitionFact OriginalSelf;
        public readonly VirtualDomainRecord Owner;
        public readonly PlatformOriginalVirtualDomainTransitionRequest Request;
        public readonly PlatformBackendEpoch Epoch;
        public readonly Func<KernelResult>? NoEffectConsumer;
        public PlatformOriginalVirtualDomainTransitionObservation? Raw;
        public bool Invoked,OwnUnknown;
    }

    private KernelResult<PlatformAuthorityResult> InvokeOriginalModelTransition(OriginalModelTransitionFact fact,
        IPlatformOriginalVirtualDomainTransitionProvider provider)
    {
        try
        {
            PlatformOriginalVirtualDomainTransitionObservation? raw;
            try{raw=provider.TransitionOriginalVirtualDomain(fact.Request);}
            catch(Exception exception) when(exception is not StackOverflowException)
            {raw=provider.ResolveOriginalVirtualDomainTransition(fact.Request);} // Query, never a second effect.
            lock(_secureDomainLifecycleGate)fact.Raw ??=raw; // Before query/source callbacks.
            var canonical=provider.ResolveOriginalVirtualDomainTransition(fact.Request);
            var source=CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
                return ExactOriginalModelTransition(fact,canonical,source) ? KernelResult<PlatformAuthorityResult>.Ok(fact.Raw!.Result) :
                    KernelResult<PlatformAuthorityResult>.Fail(KernelError.PlatformFaulted,"Original model transition outcome unknown/copied/foreign; retained.");
        }
        catch(Exception exception) when(exception is not StackOverflowException)
        {return KernelResult<PlatformAuthorityResult>.Fail(KernelError.PlatformFaulted,"Original model transition lookup unavailable; no effect replay.");}
    }

    private bool ExactOriginalModelTransition(OriginalModelTransitionFact fact,
        PlatformOriginalVirtualDomainTransitionObservation? canonical,PlatformProviderIncarnation source) =>
        ReferenceEquals(fact.OriginalSelf,fact) && fact.Invoked && fact.Request.IsOriginal &&
        ReferenceEquals(fact.Owner.OriginalTransition,fact) && fact.Owner.TransitionIntent==fact.Request.Transition &&
        fact.Request.Lease==fact.Owner.ProviderLease && fact.Owner.CreatedInEpoch==fact.Epoch && BackendEpoch==fact.Epoch && !_backendEpochExhausted &&
        source.Value!=0 && source==fact.Request.Incarnation && source==fact.Owner.ProviderIncarnation &&
        _virtualDomainBindings.TryGetValue(fact.Owner.Binding.BindingId,out var route) && ReferenceEquals(route,fact.Owner) &&
        _originalVirtualDomainOwners.TryGetValue(fact.Owner.Binding.BindingId,out var original) && ReferenceEquals(original,fact.Owner) &&
        fact.Raw is {IsOriginal:true,NoFurtherTransitionUse:true} raw && ReferenceEquals(raw,canonical) &&
        ReferenceEquals(raw.Request,fact.Request);

    private KernelResult ResolveOriginalModelTransitionForCleanup(VirtualDomainBinding binding)
    {
        VirtualDomainRecord owner;
        OriginalModelTransitionFact fact;
        lock(_secureDomainLifecycleGate)
        {
            if(!_originalVirtualDomainOwners.TryGetValue(binding.BindingId,out owner!) || owner.Binding!=binding ||
                !_virtualDomainBindings.TryGetValue(binding.BindingId,out var route) || !ReferenceEquals(route,owner))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original model cleanup route absent/foreign.");
            if(!owner.TransitionMayHaveEffect)return KernelResult.Ok();
            if(owner.OriginalTransition is not {Invoked:true,OwnUnknown:true} original || !ReferenceEquals(original.Owner,owner) ||
                !ReferenceEquals(original.OriginalSelf,original) || owner.TransitionInFlight || owner.CloseInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original uncertain model transition unavailable/in flight; retained.");
            fact=original;owner.TransitionInFlight=true;
        }
        try
        {
            if(_provider is not IPlatformOriginalVirtualDomainTransitionProvider {SupportsOriginalVirtualDomainTransitions:true} provider ||
                _provider is not IPlatformProviderIncarnationSource)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Legacy model unknown has no original resolver.");
            var before=CurrentProviderIncarnation();
            var raw=provider.ResolveOriginalVirtualDomainTransition(fact.Request);
            lock(_secureDomainLifecycleGate)fact.Raw ??=raw;
            var canonical=provider.ResolveOriginalVirtualDomainTransition(fact.Request);
            var after=CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
                if(before!=after || !ExactOriginalModelTransition(fact,canonical,after))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original model transition cleanup unknown/foreign/source changed.");
            var consumed=fact.Raw!.NoEffect ? fact.NoEffectConsumer?.Invoke() ?? KernelResult.Ok() : KernelResult.Ok();
            if(!consumed.IsSuccess)return consumed;
            after=CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
            {
                if(before!=after || !ExactOriginalModelTransition(fact,canonical,after) || !owner.TransitionMayHaveEffect || !fact.OwnUnknown)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original model cleanup changed after consequence delivery.");
                owner.TransitionObservation=fact.Raw.Result;
                owner.TransitionMayHaveEffect=false;fact.OwnUnknown=false;
                // Cleanup only. Quarantine stays; no local state/capability publication.
                return KernelResult.Ok();
            }
        }
        catch(Exception exception) when(exception is not StackOverflowException)
        {return KernelResult.Fail(KernelError.PlatformFaulted,"Original model cleanup query unavailable; retained.");}
        finally{lock(_secureDomainLifecycleGate)owner.TransitionInFlight=false;}
    }
}
