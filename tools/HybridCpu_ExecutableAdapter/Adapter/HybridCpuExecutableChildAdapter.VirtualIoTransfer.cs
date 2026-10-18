using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace YAKSys_Hybrid_CPU.ExecutableAdapter;
public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalVirtualIoTransferProvider,INeutralOriginalVirtualIoSnapshotReleaseProvider,INeutralOriginalVirtualIoSnapshotReleaseContinuationProvider
{
    private sealed class IoTransferFact(NeutralOriginalVirtualIoTransferRequest request,ExternalOriginalVirtualIoTransferRequest native,long revision,OriginalIoFact owner)
    {
        public readonly NeutralOriginalVirtualIoTransferRequest Request = request;
        public readonly ExternalOriginalVirtualIoTransferRequest Native = native;
        public readonly long Revision = revision;
        public readonly OriginalIoFact Owner=owner;
        public IoTransferFact? Self;
        public ExternalOriginalVirtualIoTransferReceipt? Raw;
        public ExternalOriginalVirtualIoTransferReceipt? ValidatedRaw;
        public NeutralOriginalVirtualIoTransferReceipt? Published;
        public NeutralOriginalVirtualIoTransferReceipt? OriginalPublished;
        public bool Invoked;
        public bool SourceChanged;
        public ExternalOriginalVirtualIoSnapshotReleaseRequest? ReleaseRequest;
        public ExternalOriginalVirtualIoSnapshotReleaseRequest? OriginalReleaseRequest;
        public ExternalOriginalVirtualIoSnapshotRelease? ReleaseRaw;
        public ExternalOriginalVirtualIoSnapshotRelease? ReleaseValidated;
        public bool ReleaseInvoked;
        public NeutralOriginalVirtualIoSnapshotRelease? ReleasePublished;
    }
    private static bool HasOriginalTransferHistory(OriginalIoFact io) => (ulong)io.Transfers.Count==io.TransfersIssued &&
        io.Transfers.All(f=>f is not null && ReferenceEquals(f.Self,f) && ReferenceEquals(f.Owner,io) && f.Request.Lease==io.Lease &&
            (f.Raw is null || ReferenceEquals(f.Raw.Request,f.Native))) &&
        io.Transfers.Select(f=>f.Request.InvocationId).Distinct().Count()==io.Transfers.Count;
    private static bool HasPublishedOriginalTransfer(IoTransferFact fact) => fact.Invoked && !fact.SourceChanged &&
        fact.Raw is { } raw && ReferenceEquals(raw,fact.ValidatedRaw) && ReferenceEquals(raw.Request,fact.Native) &&
        fact.Published is { } published && ReferenceEquals(published,fact.OriginalPublished) && ReferenceEquals(published.Request,fact.Request) &&
        raw.Completed==published.Completed && raw.NoEffect==published.NoEffect && raw.Completed!=raw.NoEffect;
    private bool CanAdmitNestedIoTransfer(OriginalIoFact io) => NestedIdentity(io.Child) &&
        io.Parent is { Closed: false, CloseRequest: null } &&
        NestedLineage(io.Child).All(child => GuestMapHistory(child) && !pendingNestedSources.Contains(child) &&
            !HasPendingGuestAdmission(child.Neutral) &&
            !child.StateRevisionExhausted &&
            (child.State is NeutralChildDomainState.Created or NeutralChildDomainState.Running or NeutralChildDomainState.Parked) &&
            !child.CloseMayHaveEffect && child.OriginalCloseInvocation is not { Invoked: true, NoEffectConsumed: false } &&
            NestedHistory(child) && child.OriginalNested.All(f => !f.InFlight &&
                (!f.ContainInvoked || f.Closure is { NoFurtherAdmission: true, NoFurtherChildUse: true } closure && ReferenceEquals(closure.Request,f.Request)) &&
                (f.Child is not null && NestedIdentity(f.Child) &&
                    (HasRetainedOriginalChildClosure(f.Child) ||
                     !f.Child.CloseMayHaveEffect && f.Child.OriginalCloseInvocation is not { Invoked: true, NoEffectConsumed: false }) ||
                 f.NoEffectConsumed || f.Closure is not null)));
    private static IEnumerable<Child> NestedLineage(Child child)
    { for (Child? current=child; current is not null; current=current.NestedParent) yield return current; }
    public bool SupportsOriginalVirtualIoSnapshotRelease=>childRuntime is IHybridCpuOriginalVirtualIoSnapshotReleaseRuntimeV1;
    public NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> ReleaseOriginalVirtualIoSnapshot(NeutralOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferReceipt transfer)=>ReleaseSnapshot(request,transfer,false);
    public NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> ResolveOriginalVirtualIoSnapshotRelease(NeutralOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferReceipt transfer)=>ReleaseSnapshot(request,transfer,true);
    public NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> ContinueOriginalVirtualIoSnapshotRelease(NeutralOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferReceipt transfer)=>ReleaseSnapshot(request,transfer,true,true);
    private NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> ReleaseSnapshot(NeutralOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferReceipt transfer,bool lookup,bool continuation=false)
    {
        NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> Refuse()=>Fail<NeutralOriginalVirtualIoSnapshotRelease>(NeutralVirtualizationStatus.Ambiguous,"Original snapshot release unknown; retained without replay.");
        if(childRuntime is not IHybridCpuOriginalVirtualIoSnapshotReleaseRuntimeV1 native)return Refuse();
        OriginalIoFact io;IoTransferFact fact;ExternalOriginalVirtualIoSnapshotReleaseRequest releaseRequest;
        lock(sync)
        {
            if(!originalIo.TryGetValue(request.Lease.Handle,out io!) || !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || io.InFlight)return Refuse();
            var found=io.Transfers.Where(f=>ReferenceEquals(f.Request,request)).ToArray();
            if(found.Length!=1)return Refuse();fact=found[0];
            if(!HasPublishedOriginalTransfer(fact) || !ReferenceEquals(fact.Published,transfer) || !ReferenceEquals(transfer.Request,request) || request.Write || !transfer.Completed || transfer.NoEffect)return Refuse();
            if(!ReferenceEquals(fact.ReleaseRequest,fact.OriginalReleaseRequest))return Refuse();
            if(fact.ReleaseRequest is null && (fact.ReleaseInvoked || fact.ReleaseRaw is not null || fact.ReleasePublished is not null))return Refuse();
            if(fact.ReleasePublished is { } done)return fact.ReleaseInvoked && fact.ReleaseRaw is {IsOriginal:true,NoFurtherSnapshotRead:true} closed &&
                ReferenceEquals(closed,fact.ReleaseValidated) && ReferenceEquals(closed.Request,fact.ReleaseRequest) &&
                ReferenceEquals(fact.ReleaseRequest!.Transfer,fact.Raw) ? Ok(done) : Refuse();
            if(lookup && fact.ReleaseRequest is null)return Refuse();
            if(fact.ReleaseRequest is null)fact.OriginalReleaseRequest=fact.ReleaseRequest=new(fact.Raw);
            releaseRequest=fact.ReleaseRequest;io.InFlight=true;
        }
        try
        {
            var features=root.QueryFeatures();
            bool invoke;
            lock(sync)
            {
                if(features.ContractVersion!=externalFeatures.ContractVersion || features.Generation!=externalFeatures.Generation){fact.SourceChanged=true;return Refuse();}
                if(!HasPublishedOriginalTransfer(fact) || !ReferenceEquals(fact.ReleaseRequest,releaseRequest) || !releaseRequest.IsOriginal || !ReferenceEquals(releaseRequest.Transfer,fact.Raw) ||
                    !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || io.Consumed || io.CloseRequest is not null)return Refuse();
                invoke=!fact.ReleaseInvoked;if(invoke && lookup && !continuation)return Refuse();
                if(invoke)fact.ReleaseInvoked=true;
            }
            if(invoke)
            {
                ExternalOriginalVirtualIoSnapshotRelease? raw=null;
                try{raw=native.ReleaseOriginalVirtualIoSnapshot(releaseRequest);}catch(Exception e)when(e is not StackOverflowException){}
                lock(sync)fact.ReleaseRaw ??=raw;
            }
            var canonical=native.ResolveOriginalVirtualIoSnapshotRelease(releaseRequest);
            lock(sync)fact.ReleaseRaw ??=canonical;
            features=root.QueryFeatures();
            lock(sync)
            {
                if(features.ContractVersion!=externalFeatures.ContractVersion || features.Generation!=externalFeatures.Generation)fact.SourceChanged=true;
                if(!HasPublishedOriginalTransfer(fact) || !ReferenceEquals(fact.ReleaseRequest,releaseRequest) || !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || io.Consumed || io.CloseRequest is not null ||
                    fact.ReleaseRaw is not {IsOriginal:true,NoFurtherSnapshotRead:true} raw || !ReferenceEquals(raw,canonical) || !ReferenceEquals(raw.Request,releaseRequest))return Refuse();
                fact.ReleaseValidated=raw;
                return Ok(fact.ReleasePublished=new(transfer));
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return Refuse();}
        finally{lock(sync)io.InFlight=false;}
    }
    public NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> TransferOriginalVirtualIo(NeutralOriginalVirtualIoTransferRequest request) => RunIoTransfer(request,false);
    public NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> ResolveOriginalVirtualIoTransfer(NeutralOriginalVirtualIoTransferRequest request) => RunIoTransfer(request,true);
    private NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> RunIoTransfer(NeutralOriginalVirtualIoTransferRequest request,bool lookup)
    {
        NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> Refuse() => Fail<NeutralOriginalVirtualIoTransferReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original transfer unavailable; no repeat.");
        if(request is null || !request.ValidInput || childRuntime is not IHybridCpuOriginalVirtualIoTransferRuntimeV1 native) return Refuse();
        OriginalIoFact io; IoTransferFact fact;
        lock(sync)
        {
            if(!originalIo.TryGetValue(request.Lease.Handle,out io!) || io.Lease != request.Lease || !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || io.InFlight) return Refuse();
            var prior = io.Transfers.SingleOrDefault(f=>f.Request.InvocationId==request.InvocationId);
            if(prior is not null) { if(!ReferenceEquals(prior.Request,request)) return Refuse(); fact=prior; }
            else
            {
                if(lookup || !io.Published || io.Consumed || io.CloseRequest is not null || io.NativeRequest is null || !CanAdmitNestedIoTransfer(io) ||
                    io.Child.StateRevisionExhausted || io.Transfers.Count>=256 || nextOperation is 0 or ulong.MaxValue ||
                    request.Length is <=0 or >4096 || request.Write && (request.Input is null || request.Input.Length!=request.Length) || !request.Write && request.Input is not null) return Refuse();
                io.Transfers.EnsureCapacity(io.Transfers.Count+1);
                fact=new(request,new(io.NativeRequest,Op(),request.Offset,request.Write,request.Length,request.Input),io.Child.StateRevision,io);fact.Self=fact;
                io.Transfers.Add(fact);io.TransfersIssued++;request.ReleaseInput();
            }
            if(fact.Published is { } completed) return HasPublishedOriginalTransfer(fact) ? Ok(completed) : Refuse();
            io.InFlight=true;
        }
        try
        {
            if(!Fresh()) return Refuse();
            if(!fact.Invoked)
            {
                if(lookup) return Refuse();
                lock(sync)
                {
                    if(fact.SourceChanged || !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || !io.InFlight || io.Consumed || io.CloseRequest is not null || !CanAdmitNestedIoTransfer(io) ||
                        io.Child.StateRevision!=fact.Revision || io.Child.StateRevisionExhausted)
                    { fact.SourceChanged=true; return Refuse(); }
                    fact.Invoked=true;
                }
                try { var raw=native.TransferOriginalVirtualIo(fact.Native); lock(sync) fact.Raw=raw; }
                catch(Exception e) when(e is not StackOverflowException) { }
            }
            var queried=native.ResolveOriginalVirtualIoTransfer(fact.Native);
            lock(sync) { if(fact.Raw is null) fact.Raw=queried; else if(!ReferenceEquals(fact.Raw,queried)) return Refuse(); }
            var result=fact.Raw;
            if(result is null || !ReferenceEquals(result.Request,fact.Native) || result.Completed==result.NoEffect ||
                result.ContractVersion!=externalFeatures.ContractVersion || result.ManifestGeneration!=externalFeatures.Generation || !Fresh()) return Refuse();
            lock(sync)
            {
                if(fact.SourceChanged || !ReferenceEquals(fact.Raw,result) || !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || io.Child.StateRevision!=fact.Revision || io.Child.StateRevisionExhausted)
                { fact.SourceChanged=true; return Refuse(); }
                fact.ValidatedRaw=result;
                return Ok(fact.OriginalPublished=fact.Published=new(request,result.Completed,result.NoEffect,result.CopyReadData));
            }
        }
        catch(Exception e) when(e is not StackOverflowException) { return Refuse(); }
        finally { lock(sync) io.InFlight=false; }
        bool Fresh()
        {
            var features=root.QueryFeatures();
            lock(sync)
            {
                if(features.Generation!=externalFeatures.Generation || features.ContractVersion!=externalFeatures.ContractVersion ||
                    io.Child.StateRevisionExhausted || io.Child.StateRevision!=fact.Revision || !OriginalIoSources(io) || !HasOriginalTransferHistory(io))fact.SourceChanged=true;
                return !fact.SourceChanged;
            }
        }
    }
}
