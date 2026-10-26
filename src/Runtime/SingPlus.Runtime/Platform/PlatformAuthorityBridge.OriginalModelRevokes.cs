using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class OriginalModelRevokeFact
    {
        public OriginalModelRevokeFact(VirtualDomainRecord owner,PlatformOriginalVirtualDomainRevokeRequest request,PlatformBackendEpoch epoch)
        {OriginalSelf=this;Owner=owner;Request=request;Epoch=epoch;}
        public readonly OriginalModelRevokeFact OriginalSelf;
        public readonly VirtualDomainRecord Owner;
        public readonly PlatformOriginalVirtualDomainRevokeRequest Request;
        public readonly PlatformBackendEpoch Epoch;
        public PlatformOriginalVirtualDomainRevokeObservation? Raw;
        public bool Invoked;
    }
    private bool ExactOriginalModelRevoke(OriginalModelRevokeFact fact,PlatformOriginalVirtualDomainRevokeObservation? canonical,
        PlatformProviderIncarnation source)=>ReferenceEquals(fact.OriginalSelf,fact) && fact.Invoked && fact.Request.IsOriginal &&
        ReferenceEquals(fact.Owner.OriginalClose,fact) && fact.Request.Lease==fact.Owner.ProviderLease &&
        source.Value!=0 && source==fact.Request.Incarnation && source==fact.Owner.ProviderIncarnation &&
        fact.Epoch==fact.Owner.CreatedInEpoch && fact.Epoch==BackendEpoch && !_backendEpochExhausted &&
        _virtualDomainBindings.TryGetValue(fact.Owner.Binding.BindingId,out VirtualDomainRecord? route) && ReferenceEquals(route,fact.Owner) &&
        _originalVirtualDomainOwners.TryGetValue(fact.Owner.Binding.BindingId,out VirtualDomainRecord? original) && ReferenceEquals(original,fact.Owner) &&
        fact.Raw is {IsOriginal:true,NoFurtherDomainUse:true} raw && ReferenceEquals(raw,canonical) && ReferenceEquals(raw.Request,fact.Request);

    private KernelResult InvokeOriginalModelRevoke(OriginalModelRevokeFact fact,IPlatformOriginalVirtualDomainRevokeProvider provider)
    {
        try
        {
            PlatformOriginalVirtualDomainRevokeObservation? raw;
            try{raw=provider.RevokeOriginalVirtualDomain(fact.Request);}
            catch(Exception exception) when(exception is not StackOverflowException){raw=provider.ResolveOriginalVirtualDomainRevoke(fact.Request);}
            lock(_secureDomainLifecycleGate)fact.Raw ??=raw;
            return ReadOriginalModelRevoke(fact,provider);
        }
        catch(Exception exception) when(exception is not StackOverflowException)
        {return KernelResult.Fail(KernelError.PlatformFaulted,"Original model revoke unknown; retained without replay.");}
    }
    private KernelResult ReadOriginalModelRevoke(OriginalModelRevokeFact fact,IPlatformOriginalVirtualDomainRevokeProvider provider)
    {
        PlatformProviderIncarnation before =CurrentProviderIncarnation();
        PlatformOriginalVirtualDomainRevokeObservation? raw =provider.ResolveOriginalVirtualDomainRevoke(fact.Request);
        lock(_secureDomainLifecycleGate)fact.Raw ??=raw;
        PlatformOriginalVirtualDomainRevokeObservation? canonical =provider.ResolveOriginalVirtualDomainRevoke(fact.Request);
        PlatformProviderIncarnation after =CurrentProviderIncarnation();
        lock(_secureDomainLifecycleGate)
        {
            if(before!=after || !ExactOriginalModelRevoke(fact,canonical,after))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original model closure unknown/copied/foreign/source changed.");
            fact.Owner.CloseObservation=PlatformAuthorityResult.Ok();fact.Owner.ProviderClosureConfirmed=true;
            return KernelResult.Ok(); // Closure evidence only; final local cleanup remains separate.
        }
    }
    private KernelResult ResolveOriginalModelRevokeForCleanup(VirtualDomainBinding binding)
    {
        VirtualDomainRecord owner;
        OriginalModelRevokeFact fact;
        lock(_secureDomainLifecycleGate)
        {
            if(!_originalVirtualDomainOwners.TryGetValue(binding.BindingId,out owner!) || owner.Binding!=binding ||
                !_virtualDomainBindings.TryGetValue(binding.BindingId,out VirtualDomainRecord? route) || !ReferenceEquals(route,owner))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original model close route absent/foreign.");
            if(!owner.CloseMayHaveEffect || owner.ProviderClosureConfirmed)return KernelResult.Ok();
            if(owner.OriginalClose is not {Invoked:true} original || !ReferenceEquals(original.OriginalSelf,original) ||
                !ReferenceEquals(original.Owner,owner) || owner.CloseInFlight || owner.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original model revoke absent/in flight; retained.");
            fact=original;owner.CloseInFlight=true;
        }
        try
        {
            return _provider is IPlatformOriginalVirtualDomainRevokeProvider {SupportsOriginalVirtualDomainRevocation:true} provider &&
                _provider is IPlatformProviderIncarnationSource ? ReadOriginalModelRevoke(fact,provider) :
                KernelResult.Fail(KernelError.PlatformFaulted,"Legacy unknown model close has no resolver.");
        }
        catch(Exception exception) when(exception is not StackOverflowException)
        {return KernelResult.Fail(KernelError.PlatformFaulted,"Original model close lookup unavailable; retained.");}
        finally{lock(_secureDomainLifecycleGate)owner.CloseInFlight=false;}
    }
}
