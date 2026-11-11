using SingPlus.Platform;
namespace SingPlus.Runtime;
public sealed partial class PlatformAuthorityBridge
{
    private sealed class BridgeTransferDispatch
    {
        public BridgeTransferDispatch(BridgeIoTransferFact owner){Self=this;Owner=owner;Request=owner.Request;}
        public readonly BridgeTransferDispatch Self;
        public readonly BridgeIoTransferFact Owner;
        public readonly PlatformOriginalVirtualIoTransferRequest Request;
    }
    private sealed class BridgeTransferFence
    {
        public BridgeTransferFence(BridgeIoTransferFact owner,PlatformOriginalVirtualIoTransferReceipt? raw)
        {Self=this;Owner=owner;Request=owner.Request;Raw=raw;}
        public readonly BridgeTransferFence Self;
        public readonly BridgeIoTransferFact Owner;
        public readonly PlatformOriginalVirtualIoTransferRequest Request;
        public readonly PlatformOriginalVirtualIoTransferReceipt? Raw;
    }
    private sealed class BridgeIoTransferFact
    {
        public BridgeIoTransferFact(PlatformOriginalVirtualIoTransferRequest request,PlatformBackendEpoch epoch,PlatformProviderIncarnation incarnation)
        {Self=this;Request=request;Epoch=epoch;Incarnation=incarnation;}
        public readonly BridgeIoTransferFact Self;
        public readonly PlatformOriginalVirtualIoTransferRequest Request;
        public readonly PlatformBackendEpoch Epoch;
        public readonly PlatformProviderIncarnation Incarnation;
        public bool Invoked;
        public BridgeTransferDispatch? Dispatch,OriginalDispatch;
        public bool NeverInvokedRetired;
        public bool NoEffectConsumed;
        public BridgeTransferFence? Fence,OriginalFence;
        public bool SourceChanged;
        public PlatformOriginalVirtualIoTransferReceipt? Raw;
        public bool SnapshotReleaseInvoked;
        public PlatformOriginalVirtualIoSnapshotRelease? SnapshotReleaseRaw;
    }
    private static bool HasOriginalTransferDispatch(BridgeIoTransferFact fact) =>
        fact.OriginalDispatch is { } original && ReferenceEquals(fact.Dispatch,original) &&
        ReferenceEquals(original.Self,original) && ReferenceEquals(original.Owner,fact) && ReferenceEquals(original.Request,fact.Request);
    private static bool RetainBridgeTransferFence(BridgeIoTransferFact fact,PlatformOriginalVirtualIoTransferReceipt? raw)
    {
        if(fact.OriginalFence is { } original)
            return ReferenceEquals(fact.Fence,original) && ReferenceEquals(original.Self,original) &&
                ReferenceEquals(original.Owner,fact) && ReferenceEquals(original.Request,fact.Request) && ReferenceEquals(original.Raw,raw);
        if(fact.Fence is not null)return false;
        fact.Fence=fact.OriginalFence=new(fact,raw);return true;
    }
    private static bool HasOriginalBridgeTransfer(VirtualIoBindingRecord io,BridgeIoTransferFact fact) =>
        ReferenceEquals(fact.Self,fact) && (ulong)io.Transfers.Count==io.TransfersIssued &&
        io.Transfers.Count(f=>ReferenceEquals(f,fact))==1 && io.Transfers.All(f=>ReferenceEquals(f.Self,f)) &&
        io.Transfers.Select(f=>f.Request.InvocationId).Distinct().Count()==io.Transfers.Count;
    internal bool SupportsOriginalVirtualIoSnapshotRelease
    {
        get{try{return _provider is IPlatformOriginalVirtualIoSnapshotReleaseProvider {SupportsOriginalVirtualIoSnapshotRelease:true};}catch(Exception e)when(e is not StackOverflowException){return false;}}
    }
    internal KernelResult ReleaseOriginalVirtualIoSnapshot(PlatformVirtualIoBinding binding,PlatformOriginalVirtualIoTransferRequest request,
        PlatformOriginalVirtualIoTransferReceipt transfer,Func<KernelResult> consume)
    {
        if(_provider is not IPlatformOriginalVirtualIoSnapshotReleaseProvider provider)return KernelResult.Fail(KernelError.PlatformUnsupported,"Original snapshot release provider unavailable.");
        VirtualIoBindingRecord io;BridgeIoTransferFact fact;
        lock(_secureDomainLifecycleGate)
        {
            if(!_virtualIoBindings.TryGetValue(binding.BindingId,out io!) || !IsOriginalVirtualIo(io) || io.Binding!=binding || io.TransferInFlight || io.ClosureInFlight ||
                (ulong)io.Transfers.Count!=io.TransfersIssued || io.Transfers.Any(f=>!ReferenceEquals(f.Self,f)))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original snapshot release route/history changed.");
            BridgeIoTransferFact[] found =io.Transfers.Where(f=>ReferenceEquals(f.Request,request)).ToArray();if(found.Length!=1)return KernelResult.Fail(KernelError.PlatformFaulted,"Original snapshot transfer absent.");fact=found[0];
            if(!HasOriginalBridgeTransfer(io,fact) || !ReferenceEquals(fact.Raw,transfer) || !ReferenceEquals(transfer.Request,request) || !transfer.Completed || transfer.NoEffect || request.Write)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original completed read receipt changed.");
            io.TransferInFlight=true;
        }
        try
        {
            PlatformProviderIncarnation source =CurrentProviderIncarnation();bool invoke;
            lock(_secureDomainLifecycleGate)
            {
                if(source!=fact.Incarnation || source.Value==0 || BackendEpoch!=fact.Epoch)fact.SourceChanged=true;
                if(fact.SourceChanged || !HasOriginalBridgeTransfer(io,fact) || !ReferenceEquals(fact.Raw,transfer) || !IsOriginalVirtualIo(io) || io.CloseInvoked)return KernelResult.Fail(KernelError.PlatformFaulted,"Original snapshot release source changed.");
                invoke=!fact.SnapshotReleaseInvoked;if(invoke)fact.SnapshotReleaseInvoked=true;
            }
            if(invoke)
            {
                PlatformOriginalVirtualIoSnapshotRelease? raw=null;
                try{ PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> result =provider.ReleaseOriginalVirtualIoSnapshot(request,transfer);if(result.IsSuccess)raw=result.Value;}catch(Exception e)when(e is not StackOverflowException){}
                lock(_secureDomainLifecycleGate)fact.SnapshotReleaseRaw ??=raw;
            }
            PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> canonical =provider.ResolveOriginalVirtualIoSnapshotRelease(request,transfer);
            if(!canonical.IsSuccess && provider is IPlatformOriginalVirtualIoSnapshotReleaseContinuationProvider resume)
                canonical=resume.ContinueOriginalVirtualIoSnapshotRelease(request,transfer);
            lock(_secureDomainLifecycleGate)fact.SnapshotReleaseRaw ??=canonical.IsSuccess?canonical.Value:null;
            source=CurrentProviderIncarnation();
            // The source observation is an external callback. Refresh the exact
            // lower-owner proof after it, without starting or continuing an effect.
            PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> finalCanonical =
                provider.ResolveOriginalVirtualIoSnapshotRelease(request,transfer);
            lock(_secureDomainLifecycleGate)
            {
                if(source!=fact.Incarnation || source.Value==0 || BackendEpoch!=fact.Epoch)fact.SourceChanged=true;
                if(fact.SourceChanged || !HasOriginalBridgeTransfer(io,fact) || !ReferenceEquals(fact.Raw,transfer) || !IsOriginalVirtualIo(io) || io.CloseInvoked || !canonical.IsSuccess ||
                    !finalCanonical.IsSuccess || !ReferenceEquals(canonical.Value,finalCanonical.Value) ||
                    fact.SnapshotReleaseRaw is not {NoFurtherSnapshotRead:true} raw || !ReferenceEquals(raw,canonical.Value) || !ReferenceEquals(raw.Transfer,transfer))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original snapshot release unknown/copied; charge retained.");
                return consume(); // Trusted local charge/invocation consumption, no provider callback.
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return KernelResult.Fail(KernelError.PlatformFaulted,"Original snapshot release interrupted; retained without replay.");}
        finally{lock(_secureDomainLifecycleGate)io.TransferInFlight=false;}
    }
    internal KernelResult ConsumeOriginalVirtualIoTransferNoEffect(PlatformVirtualIoBinding binding,PlatformOriginalVirtualIoTransferRequest request,
        PlatformOriginalVirtualIoTransferReceipt raw,Func<KernelResult> consume)
    {
        lock(_secureDomainLifecycleGate)
        {
            if(!_virtualIoBindings.TryGetValue(binding.BindingId,out VirtualIoBindingRecord? io) || !IsOriginalVirtualIo(io) || io.Binding!=binding ||
                io.TransferInFlight || io.ClosureInFlight || (ulong)io.Transfers.Count!=io.TransfersIssued ||
                io.Transfers.Any(f=>!ReferenceEquals(f.Self,f)) || io.Transfers.Select(f=>f.Request.InvocationId).Distinct().Count()!=io.Transfers.Count)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original transfer no-effect history changed.");
            BridgeIoTransferFact[] facts =io.Transfers.Where(f=>ReferenceEquals(f.Request,request)).ToArray();
            if(facts.Length!=1 || !HasOriginalTransferDispatch(facts[0]) || !ReferenceEquals(facts[0].Raw,raw) || !ReferenceEquals(raw.Request,request) || !raw.NoEffect || raw.Completed)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained,"Exact original provider transfer no-effect unavailable.");
            if(!RetainBridgeTransferFence(facts[0],raw))return KernelResult.Fail(KernelError.PlatformFaulted,"Original no-effect fence custody changed.");
            facts[0].NoEffectConsumed=true;
            request.ReleaseInput();
            return consume();
        }
    }
    // This existing invocation owner fences future invocation before local
    // quantitative consumption. No provider query/permission/source callback.
    internal KernelResult ConsumeUninvokedVirtualIoTransfer(PlatformVirtualIoBinding binding,PlatformOriginalVirtualIoTransferRequest request,Func<KernelResult> consume)
    {
        lock(_secureDomainLifecycleGate)
        {
            if(!_virtualIoBindings.TryGetValue(binding.BindingId,out VirtualIoBindingRecord? io) || !IsOriginalVirtualIo(io) || io.Binding!=binding ||
                io.TransferInFlight || io.ClosureInFlight || (ulong)io.Transfers.Count!=io.TransfersIssued ||
                io.Transfers.Any(f=>!ReferenceEquals(f.Self,f)) || io.Transfers.Select(f=>f.Request.InvocationId).Distinct().Count()!=io.Transfers.Count)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original transfer history unavailable for never-invoked cleanup.");
            BridgeIoTransferFact[] facts =io.Transfers.Where(f=>ReferenceEquals(f.Request,request)).ToArray();
            if(facts.Length!=1 || facts[0].OriginalDispatch is not null || facts[0].Dispatch is not null || facts[0].Invoked || facts[0].Raw is not null)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained,"Original transfer may have invoked; no no-effect substitution.");
            if(!RetainBridgeTransferFence(facts[0],null))return KernelResult.Fail(KernelError.PlatformFaulted,"Original never-invoked fence custody changed.");
            facts[0].NeverInvokedRetired=true;
            request.ReleaseInput();
            return consume(); // Trusted local consumption only; partial failure retains fence.
        }
    }
    // Caller owns capability admission and quantitative read snapshot reservation.
    // This bridge retains the original invocation before effect; unknown never repeats.
    private bool CanAdmitIoArtifactLifetime(VirtualIoBindingRecord io)
    {
        KernelResult<Dictionary<ChildBindingRecord, long>> lineage =CaptureGuestEffectLineage(io.Admission.Child);
        return lineage.IsSuccess && lineage.Value!.Keys.All(child=>!_originalExecutableArtifactOwners.Values.Any(artifact=>
            ReferenceEquals(artifact.AdmissionSource?.Child,child) && !artifact.Released &&
            (artifact.ReleaseInFlight || artifact.ReleaseInvoked || artifact.ReleaseRequest is not null ||
             artifact.ReleaseObservation is not null || artifact.ReleaseResolutionObservation is not null)));
    }
    internal KernelResult<PlatformOriginalVirtualIoTransferRequest> PrepareVirtualIoTransfer(PlatformVirtualIoBinding binding,ulong offset,bool write,int length,byte[]? data)
    {
        PlatformProviderIncarnation incarnation =CurrentProviderIncarnation();
        lock(_secureDomainLifecycleGate)
        {
            if(_provider is not IPlatformOriginalVirtualIoTransferProvider || !_virtualIoBindings.TryGetValue(binding.BindingId,out VirtualIoBindingRecord? io) ||
                !IsOriginalVirtualIo(io) || io.Binding!=binding || io.CloseInvoked || io.ClosureInFlight || io.TransferInFlight ||
                !CanAdmitIoArtifactLifetime(io) ||
                io.Closure!=PlatformExternalClosureState.Active || io.Transfers.Count>=256 || length is <=0 or >4096 ||
                write && (data is null || data.Length!=length) || !write && data is not null || incarnation.Value==0)
                return KernelResult<PlatformOriginalVirtualIoTransferRequest>.Fail(KernelError.PlatformFaulted,"Original transfer admission unavailable.");
            io.Transfers.EnsureCapacity(io.Transfers.Count+1);
            PlatformOriginalVirtualIoTransferRequest request =new PlatformOriginalVirtualIoTransferRequest(io.ProviderLease,offset,write,length,data);
            io.Transfers.Add(new(request,BackendEpoch,incarnation));io.TransfersIssued++;
            return KernelResult<PlatformOriginalVirtualIoTransferRequest>.Ok(request);
        }
    }
    internal KernelResult<PlatformOriginalVirtualIoTransferReceipt> InvokeVirtualIoTransfer(PlatformVirtualIoBinding binding,PlatformOriginalVirtualIoTransferRequest request)=>ReadOrInvokeVirtualIoTransfer(binding,request,true);
    internal KernelResult<PlatformOriginalVirtualIoTransferReceipt> ResolveVirtualIoTransfer(PlatformVirtualIoBinding binding,PlatformOriginalVirtualIoTransferRequest request)=>ReadOrInvokeVirtualIoTransfer(binding,request,false);
    private KernelResult<PlatformOriginalVirtualIoTransferReceipt> ReadOrInvokeVirtualIoTransfer(PlatformVirtualIoBinding binding,PlatformOriginalVirtualIoTransferRequest request,bool allowInvoke)
    {
        KernelResult<PlatformOriginalVirtualIoTransferReceipt> Fail()=>KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Fail(KernelError.PlatformFaulted,"Original transfer unresolved; no repeat.");
        if(_provider is not IPlatformOriginalVirtualIoTransferProvider provider)return Fail();
        VirtualIoBindingRecord io;BridgeIoTransferFact fact;
        lock(_secureDomainLifecycleGate)
        {
            if(!_virtualIoBindings.TryGetValue(binding.BindingId,out io!) || !IsOriginalVirtualIo(io) || io.Binding!=binding || io.TransferInFlight || io.ClosureInFlight || io.CloseInvoked ||
                (ulong)io.Transfers.Count!=io.TransfersIssued || io.Transfers.Select(x=>x.Request.InvocationId).Distinct().Count()!=io.Transfers.Count)return Fail();
            BridgeIoTransferFact[] found =io.Transfers.Where(x=>ReferenceEquals(x.Request,request)).ToArray();if(found.Length!=1)return Fail();fact=found[0];
            if(!ReferenceEquals(fact.Self,fact) || fact.OriginalFence is not null || fact.Fence is not null || fact.NeverInvokedRetired || fact.NoEffectConsumed)return Fail();
            io.TransferInFlight=true;
        }
        try
        {
            PlatformProviderIncarnation current =CurrentProviderIncarnation();
            bool invoke;
            lock(_secureDomainLifecycleGate)
            {
                if(!Exact(current))return Fail();
                if(fact.OriginalDispatch is not null || fact.Dispatch is not null)
                {
                    if(!HasOriginalTransferDispatch(fact))return Fail();
                    invoke=false; // Only pure same-original query after possible dispatch.
                }
                else
                {
                    if(fact.Invoked || !allowInvoke || !CanAdmitIoArtifactLifetime(io))return Fail();
                    fact.Dispatch=fact.OriginalDispatch=new(fact);
                    fact.Invoked=true;invoke=true;
                }
            }
            if(invoke)
            {
                try{ PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> result =provider.TransferOriginalVirtualIo(request);lock(_secureDomainLifecycleGate)fact.Raw=result.IsSuccess?result.Value:null;}
                catch(Exception e)when(e is not StackOverflowException){}
            }
            PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> query =provider.ResolveOriginalVirtualIoTransfer(request);
            lock(_secureDomainLifecycleGate){if(fact.Raw is null)fact.Raw=query.IsSuccess?query.Value:null;else if(!query.IsSuccess || !ReferenceEquals(fact.Raw,query.Value))return Fail();}
            PlatformProviderIncarnation fresh =CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
            {
                if(!Exact(fresh) || !HasOriginalTransferDispatch(fact) || !query.IsSuccess || fact.Raw is not { } raw || !ReferenceEquals(raw,query.Value) ||
                    !ReferenceEquals(raw.Request,request) || raw.Completed==raw.NoEffect)return Fail();
                return KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(raw);
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return Fail();}
        finally{lock(_secureDomainLifecycleGate)io.TransferInFlight=false;}
        bool Exact(PlatformProviderIncarnation current)
        {
            if(current!=fact.Incarnation || current.Value==0 || BackendEpoch!=fact.Epoch)fact.SourceChanged=true;
            return !fact.SourceChanged && HasOriginalBridgeTransfer(io,fact) && IsOriginalVirtualIo(io) && io.TransferInFlight && !io.ClosureInFlight && !io.CloseInvoked && io.Closure==PlatformExternalClosureState.Active;
        }
    }
}

