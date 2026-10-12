using SingPlus.Platform;
namespace SingPlus.Runtime;
public sealed partial class PlatformAuthorityBridge
{
    private sealed class BridgeIoTransferFact(PlatformOriginalVirtualIoTransferRequest request,PlatformBackendEpoch epoch,PlatformProviderIncarnation incarnation)
    {
        public readonly PlatformOriginalVirtualIoTransferRequest Request=request;
        public readonly PlatformBackendEpoch Epoch=epoch;
        public readonly PlatformProviderIncarnation Incarnation=incarnation;
        public bool Invoked;
        public bool SourceChanged;
        public PlatformOriginalVirtualIoTransferReceipt? Raw;
    }
    // Caller owns capability admission and quantitative read snapshot reservation.
    // This bridge retains the original invocation before effect; unknown never repeats.
    internal KernelResult<PlatformOriginalVirtualIoTransferRequest> PrepareVirtualIoTransfer(PlatformVirtualIoBinding binding,ulong offset,bool write,int length,byte[]? data)
    {
        var incarnation=CurrentProviderIncarnation();
        lock(_secureDomainLifecycleGate)
        {
            if(_provider is not IPlatformOriginalVirtualIoTransferProvider || !_virtualIoBindings.TryGetValue(binding.BindingId,out var io) ||
                !IsOriginalVirtualIo(io) || io.Binding!=binding || io.CloseInvoked || io.ClosureInFlight || io.TransferInFlight ||
                io.Closure!=PlatformExternalClosureState.Active || io.Transfers.Count>=256 || length is <=0 or >4096 ||
                write && (data is null || data.Length!=length) || !write && data is not null || incarnation.Value==0)
                return KernelResult<PlatformOriginalVirtualIoTransferRequest>.Fail(KernelError.PlatformFaulted,"Original transfer admission unavailable.");
            io.Transfers.EnsureCapacity(io.Transfers.Count+1);
            var request=new PlatformOriginalVirtualIoTransferRequest(io.ProviderLease,offset,write,length,data);
            io.Transfers.Add(new(request,BackendEpoch,incarnation));io.TransfersIssued++;
            return KernelResult<PlatformOriginalVirtualIoTransferRequest>.Ok(request);
        }
    }
    internal KernelResult<PlatformOriginalVirtualIoTransferReceipt> InvokeVirtualIoTransfer(PlatformVirtualIoBinding binding,PlatformOriginalVirtualIoTransferRequest request)
    {
        KernelResult<PlatformOriginalVirtualIoTransferReceipt> Fail()=>KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Fail(KernelError.PlatformFaulted,"Original transfer unresolved; no repeat.");
        if(_provider is not IPlatformOriginalVirtualIoTransferProvider provider)return Fail();
        VirtualIoBindingRecord io;BridgeIoTransferFact fact;
        lock(_secureDomainLifecycleGate)
        {
            if(!_virtualIoBindings.TryGetValue(binding.BindingId,out io!) || !IsOriginalVirtualIo(io) || io.Binding!=binding || io.TransferInFlight || io.ClosureInFlight || io.CloseInvoked ||
                (ulong)io.Transfers.Count!=io.TransfersIssued || io.Transfers.Select(x=>x.Request.InvocationId).Distinct().Count()!=io.Transfers.Count)return Fail();
            var found=io.Transfers.Where(x=>ReferenceEquals(x.Request,request)).ToArray();if(found.Length!=1)return Fail();fact=found[0];io.TransferInFlight=true;
        }
        try
        {
            var current=CurrentProviderIncarnation();
            bool invoke;
            lock(_secureDomainLifecycleGate){if(!Exact(current))return Fail();invoke=!fact.Invoked;if(invoke)fact.Invoked=true;}
            if(invoke)
            {
                try{var result=provider.TransferOriginalVirtualIo(request);lock(_secureDomainLifecycleGate)fact.Raw=result.IsSuccess?result.Value:null;}
                catch(Exception e)when(e is not StackOverflowException){}
            }
            var query=provider.ResolveOriginalVirtualIoTransfer(request);
            lock(_secureDomainLifecycleGate){if(fact.Raw is null)fact.Raw=query.IsSuccess?query.Value:null;else if(!query.IsSuccess || !ReferenceEquals(fact.Raw,query.Value))return Fail();}
            var fresh=CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
            {
                if(!Exact(fresh) || fact.Raw is not { } raw || !ReferenceEquals(raw.Request,request) || raw.Completed==raw.NoEffect)return Fail();
                return KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(raw);
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return Fail();}
        finally{lock(_secureDomainLifecycleGate)io.TransferInFlight=false;}
        bool Exact(PlatformProviderIncarnation current)
        {
            if(current!=fact.Incarnation || current.Value==0 || BackendEpoch!=fact.Epoch)fact.SourceChanged=true;
            return !fact.SourceChanged && IsOriginalVirtualIo(io) && io.TransferInFlight && !io.ClosureInFlight && !io.CloseInvoked && io.Closure==PlatformExternalClosureState.Active;
        }
    }
}
