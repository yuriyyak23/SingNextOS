using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalVirtualIoTransferProvider,IPlatformOriginalVirtualIoSnapshotReleaseProvider,IPlatformOriginalVirtualIoSnapshotReleaseContinuationProvider
{
    private sealed class ProviderIoTransferFact(PlatformOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferRequest native,IoRecord owner)
    {
        public readonly PlatformOriginalVirtualIoTransferRequest Request=request;
        public readonly NeutralOriginalVirtualIoTransferRequest Native=native;
        public NeutralOriginalVirtualIoTransferReceipt? Raw;
        public NeutralOriginalVirtualIoTransferReceipt? ValidatedRaw;
        public PlatformOriginalVirtualIoTransferReceipt? Published;
        public PlatformOriginalVirtualIoTransferReceipt? OriginalPublished;
        public bool Invoked;
        public readonly IoRecord Owner=owner;
        public ProviderIoTransferFact? Self;
        public bool SourceChanged;
        public bool ReleaseInvoked;
        public NeutralOriginalVirtualIoSnapshotRelease? ReleaseRaw;
        public NeutralOriginalVirtualIoSnapshotRelease? ReleaseValidated;
        public PlatformOriginalVirtualIoSnapshotRelease? ReleasePublished;
    }
    private static bool HasOriginalProviderTransferHistory(IoRecord io) => (ulong)io.Transfers.Count==io.TransfersIssued &&
        io.Transfers.All(f=>f is not null && ReferenceEquals(f.Self,f) && ReferenceEquals(f.Owner,io) && f.Request.Lease==io.Lease &&
            (f.Raw is null || ReferenceEquals(f.Raw.Request,f.Native))) &&
        io.Transfers.Select(f=>f.Request.InvocationId).Distinct().Count()==io.Transfers.Count;
    private static bool HasPublishedOriginalProviderTransfer(ProviderIoTransferFact fact) => fact.Invoked && !fact.SourceChanged &&
        fact.Raw is { } raw && ReferenceEquals(raw,fact.ValidatedRaw) && ReferenceEquals(raw.Request,fact.Native) &&
        fact.Published is { } published && ReferenceEquals(published,fact.OriginalPublished) && ReferenceEquals(published.Request,fact.Request) &&
        raw.Completed==published.Completed && raw.NoEffect==published.NoEffect && raw.Completed!=raw.NoEffect;
    // Admission for a new byte effect. Stable published nested children may stay
    // live; unresolved sibling factories and ancestor teardown cannot be ignored.
    private bool CanAdmitProviderIoTransfer(IoRecord io) => ProviderNestedIdentity(io.Child) &&
        io.Child.CreateParent is { } root && ValidateDomain(root.Lease).IsSuccess &&
        NestedProviderLineage(io.Child).All(child => ProviderGuestMapHistory(child) && !_pendingNestedSources.Contains(child) &&
            !HasArtifactBindInterlock(child.Lease) &&
            !child.CloseInFlight && !child.TransitionInFlight && !child.GuestMapInFlight &&
            child.FailedClose is null && child.FailedTransition is null &&
            child.OriginalCloseInvocation is not { NoEffectConsumed: false } &&
            child.ClosureReceipt is null && !child.ClosurePublished &&
            ProviderNestedHistory(child) && child.OriginalNested.All(f =>
                !f.InFlight && (!f.ContainInvoked || f.Closure is { NoFurtherAdmission: true, NoFurtherChildUse: true } closure && ReferenceEquals(closure.Request,f.Request)) &&
                (f.Child is not null && ProviderNestedIdentity(f.Child) &&
                    (HasPublishedChildClosure(f.Child) || !f.Child.CloseInFlight && f.Child.FailedClose is null &&
                        f.Child.OriginalCloseInvocation is not { NoEffectConsumed: false }) || f.NoEffectConsumed || f.Closure is not null)));
    public bool SupportsOriginalVirtualIoSnapshotRelease=>_childRuntime is INeutralOriginalVirtualIoSnapshotReleaseProvider {SupportsOriginalVirtualIoSnapshotRelease:true};
    public PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> ReleaseOriginalVirtualIoSnapshot(PlatformOriginalVirtualIoTransferRequest request,PlatformOriginalVirtualIoTransferReceipt transfer)=>ReleaseSnapshot(request,transfer,false);
    public PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> ResolveOriginalVirtualIoSnapshotRelease(PlatformOriginalVirtualIoTransferRequest request,PlatformOriginalVirtualIoTransferReceipt transfer)=>ReleaseSnapshot(request,transfer,true);
    public PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> ContinueOriginalVirtualIoSnapshotRelease(PlatformOriginalVirtualIoTransferRequest request,PlatformOriginalVirtualIoTransferReceipt transfer)=>ReleaseSnapshot(request,transfer,true,true);
    private PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> ReleaseSnapshot(PlatformOriginalVirtualIoTransferRequest request,PlatformOriginalVirtualIoTransferReceipt transfer,bool lookup,bool continuation=false)
    {
        PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> Refuse()=>PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease>.Fail(PlatformAuthorityStatus.Faulted,"Original snapshot release retained; no replay.");
        if(_childRuntime is not INeutralOriginalVirtualIoSnapshotReleaseProvider {SupportsOriginalVirtualIoSnapshotRelease:true} native)return Refuse();
        IoRecord io;ProviderIoTransferFact fact;
        lock(_childCreateCommitGate)
        {
            if(!_originalProviderVirtualIo.TryGetValue(request.Lease.LeaseId,out io!) || !HasOriginalIoSources(io) || !HasOriginalProviderTransferHistory(io) || io.TransferInFlight)return Refuse();
            var found=io.Transfers.Where(f=>ReferenceEquals(f.Request,request)).ToArray();if(found.Length!=1)return Refuse();fact=found[0];
            if(!HasPublishedOriginalProviderTransfer(fact) || !ReferenceEquals(fact.Published,transfer) || !ReferenceEquals(transfer.Request,request) || request.Write || !transfer.Completed || transfer.NoEffect)return Refuse();
            if(continuation && !fact.ReleaseInvoked)return Refuse();
            if(fact.ReleasePublished is { } done)return fact.ReleaseInvoked && fact.ReleaseRaw is {NoFurtherSnapshotRead:true} closed &&
                ReferenceEquals(closed,fact.ReleaseValidated) && ReferenceEquals(closed.Transfer,fact.Raw)
                ? PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease>.Ok(done) : Refuse();
            io.TransferInFlight=true;
        }
        try
        {
            var source=ValidateOriginalNativeMappingSource();bool invoke;
            lock(_childCreateCommitGate)
            {
                if(!source.IsSuccess){fact.SourceChanged=true;return Refuse();}
                if(!HasPublishedOriginalProviderTransfer(fact) || !HasOriginalIoSources(io) || !HasOriginalProviderTransferHistory(io) || io.CloseInFlight || io.CloseInvoked || io.Consumed)return Refuse();
                invoke=!fact.ReleaseInvoked;if(invoke && lookup)return Refuse();if(invoke)fact.ReleaseInvoked=true;
            }
            if(invoke)
            {
                try{var raw=native.ReleaseOriginalVirtualIoSnapshot(fact.Native,fact.Raw!);lock(_childCreateCommitGate)fact.ReleaseRaw=raw.IsSuccess?raw.Value:null;}catch(Exception e)when(e is not StackOverflowException){}
            }
            var canonical=native.ResolveOriginalVirtualIoSnapshotRelease(fact.Native,fact.Raw!);
            if(!canonical.IsSuccess && continuation && native is INeutralOriginalVirtualIoSnapshotReleaseContinuationProvider resume)
                canonical=resume.ContinueOriginalVirtualIoSnapshotRelease(fact.Native,fact.Raw!);
            lock(_childCreateCommitGate)fact.ReleaseRaw ??=canonical.IsSuccess?canonical.Value:null;
            source=ValidateOriginalNativeMappingSource();
            lock(_childCreateCommitGate)
            {
                if(!source.IsSuccess)fact.SourceChanged=true;
                if(!HasPublishedOriginalProviderTransfer(fact) || !HasOriginalIoSources(io) || !HasOriginalProviderTransferHistory(io) || io.CloseInFlight || io.CloseInvoked || io.Consumed ||
                    fact.ReleaseRaw is not {NoFurtherSnapshotRead:true} raw || !ReferenceEquals(raw.Transfer,fact.Raw) || !canonical.IsSuccess || !ReferenceEquals(raw,canonical.Value))return Refuse();
                fact.ReleaseValidated=raw;
                return PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease>.Ok(fact.ReleasePublished=new(transfer));
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return Refuse();}
        finally{lock(_childCreateCommitGate)io.TransferInFlight=false;}
    }
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
                if(lookup || !io.Published || io.Consumed || io.CloseInvoked || io.Transfers.Count>=256 || !CanAdmitProviderIoTransfer(io) ||
                    request.Length is <=0 or >4096 || request.Write && (request.Input is null || request.Input.Length!=request.Length) || !request.Write && request.Input is not null)return Fail();
                io.Transfers.EnsureCapacity(io.Transfers.Count+1);
                fact=new(request,new(io.Neutral,request.Offset,request.Write,request.Length,request.Input),io);fact.Self=fact;
                io.Transfers.Add(fact);io.TransfersIssued++;request.ReleaseInput();
            }
            if(fact.SourceChanged)return Fail();
            if(fact.Published is { } published)return HasPublishedOriginalProviderTransfer(fact) ? PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(published) : Fail();
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
                    if(fact.SourceChanged || !HasOriginalProviderTransferHistory(io) || !HasOriginalIoSources(io) || !io.TransferInFlight || io.CloseInvoked || io.CloseInFlight || io.Consumed || !CanAdmitProviderIoTransfer(io)){fact.SourceChanged=true;return Fail();}
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
                if(fact.SourceChanged || !ReferenceEquals(fact.Raw,raw) || !HasOriginalProviderTransferHistory(io) || !HasOriginalIoSources(io) || io.CloseInvoked || io.CloseInFlight || io.Consumed){fact.SourceChanged=true;return Fail();}
                fact.ValidatedRaw=raw;
                return PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(fact.OriginalPublished=fact.Published=new(request,raw.Completed,raw.NoEffect,raw.CopyReadData));
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
