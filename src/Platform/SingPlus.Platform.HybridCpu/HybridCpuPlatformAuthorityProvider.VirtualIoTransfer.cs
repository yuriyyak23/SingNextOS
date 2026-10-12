using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalVirtualIoTransferProvider
{
    private sealed class ProviderIoTransferFact(PlatformOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferRequest native,IoRecord owner)
    {
        public readonly PlatformOriginalVirtualIoTransferRequest Request=request;
        public readonly NeutralOriginalVirtualIoTransferRequest Native=native;
        public NeutralOriginalVirtualIoTransferReceipt? Raw;
        public PlatformOriginalVirtualIoTransferReceipt? Published;
        public bool Invoked;
        public readonly IoRecord Owner=owner;
        public ProviderIoTransferFact? Self;
        public bool SourceChanged;
    }
    private static bool HasOriginalProviderTransferHistory(IoRecord io) => (ulong)io.Transfers.Count==io.TransfersIssued &&
        io.Transfers.All(f=>f is not null && ReferenceEquals(f.Self,f) && ReferenceEquals(f.Owner,io) && f.Request.Lease==io.Lease &&
            (f.Raw is null || ReferenceEquals(f.Raw.Request,f.Native))) &&
        io.Transfers.Select(f=>f.Request.InvocationId).Distinct().Count()==io.Transfers.Count;
    public PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> TransferOriginalVirtualIo(PlatformOriginalVirtualIoTransferRequest request)=>RunProviderIoTransfer(request,false);
    public PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> ResolveOriginalVirtualIoTransfer(PlatformOriginalVirtualIoTransferRequest request)=>RunProviderIoTransfer(request,true);
    private PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> RunProviderIoTransfer(PlatformOriginalVirtualIoTransferRequest request,bool lookup)
    {
        PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> Fail()=>PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt>.Fail(PlatformAuthorityStatus.Faulted,"Original transfer retained; no repeat.");
        if(request is null || !request.ValidInput || _childRuntime is not INeutralOriginalVirtualIoTransferProvider native)return Fail();
        IoRecord io;ProviderIoTransferFact fact;
        lock(_childCreateCommitGate)
        {
            if(!_originalProviderVirtualIo.TryGetValue(request.Lease.LeaseId,out io!) || io.Lease!=request.Lease || !HasOriginalIoSources(io) ||
                io.TransferInFlight || io.BindInFlight || io.CloseInFlight || !HasOriginalProviderTransferHistory(io))return Fail();
            var prior=io.Transfers.SingleOrDefault(f=>f.Request.InvocationId==request.InvocationId);
            if(prior is not null){if(!ReferenceEquals(prior.Request,request))return Fail();fact=prior;}
            else
            {
                if(lookup || !io.Published || io.Consumed || io.CloseInvoked || io.Transfers.Count>=256 || io.Child.CloseInFlight || io.Child.TransitionInFlight ||
                    request.Length is <=0 or >4096 || request.Write && (request.Input is null || request.Input.Length!=request.Length) || !request.Write && request.Input is not null)return Fail();
                io.Transfers.EnsureCapacity(io.Transfers.Count+1);
                fact=new(request,new(io.Neutral,request.Offset,request.Write,request.Length,request.Input),io);fact.Self=fact;
                io.Transfers.Add(fact);io.TransfersIssued++;request.ReleaseInput();
            }
            if(fact.SourceChanged)return Fail();
            if(fact.Published is { } published)return PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(published);
            io.TransferInFlight=true;
        }
        try
        {
            if(!FreshSource())return Fail();
            if(!fact.Invoked)
            {
                if(lookup)return Fail();
                lock(_childCreateCommitGate)
                {
                    if(fact.SourceChanged || !HasOriginalProviderTransferHistory(io) || !HasOriginalIoSources(io) || !io.TransferInFlight || io.CloseInvoked || io.CloseInFlight || io.Consumed || io.Child.CloseInFlight || io.Child.TransitionInFlight){fact.SourceChanged=true;return Fail();}
                    fact.Invoked=true;
                }
                try{var result=native.TransferOriginalVirtualIo(fact.Native);lock(_childCreateCommitGate)fact.Raw=result.IsSuccess?result.Value:null;}
                catch(Exception e)when(e is not StackOverflowException){}
            }
            var queried=native.ResolveOriginalVirtualIoTransfer(fact.Native);
            lock(_childCreateCommitGate){if(fact.Raw is null)fact.Raw=queried.IsSuccess?queried.Value:null;else if(!queried.IsSuccess || !ReferenceEquals(fact.Raw,queried.Value))return Fail();}
            var raw=fact.Raw;
            if(!FreshSource() || raw is null || !ReferenceEquals(raw.Request,fact.Native) || raw.Completed==raw.NoEffect)return Fail();
            lock(_childCreateCommitGate)
            {
                if(fact.SourceChanged || !HasOriginalProviderTransferHistory(io) || !HasOriginalIoSources(io) || io.CloseInvoked || io.CloseInFlight || io.Consumed){fact.SourceChanged=true;return Fail();}
                return PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(fact.Published=new(request,raw.Completed,raw.NoEffect,raw.CopyReadData));
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return Fail();}
        finally{lock(_childCreateCommitGate)io.TransferInFlight=false;}
        bool FreshSource()
        {
            var source=ValidateOriginalNativeMappingSource(); // Callback outside the owner commit lock.
            lock(_childCreateCommitGate)
            {
                if(!source.IsSuccess || !HasOriginalIoSources(io) || !HasOriginalProviderTransferHistory(io))fact.SourceChanged=true;
                return !fact.SourceChanged;
            }
        }
    }
}
