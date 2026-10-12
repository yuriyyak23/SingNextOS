using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace YAKSys_Hybrid_CPU.ExecutableAdapter;
public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalVirtualIoTransferProvider
{
    private sealed class IoTransferFact(NeutralOriginalVirtualIoTransferRequest request,ExternalOriginalVirtualIoTransferRequest native,long revision,OriginalIoFact owner)
    {
        public readonly NeutralOriginalVirtualIoTransferRequest Request = request;
        public readonly ExternalOriginalVirtualIoTransferRequest Native = native;
        public readonly long Revision = revision;
        public readonly OriginalIoFact Owner=owner;
        public IoTransferFact? Self;
        public ExternalOriginalVirtualIoTransferReceipt? Raw;
        public NeutralOriginalVirtualIoTransferReceipt? Published;
        public bool Invoked;
        public bool SourceChanged;
    }
    private static bool HasOriginalTransferHistory(OriginalIoFact io) => (ulong)io.Transfers.Count==io.TransfersIssued &&
        io.Transfers.All(f=>f is not null && ReferenceEquals(f.Self,f) && ReferenceEquals(f.Owner,io) && f.Request.Lease==io.Lease &&
            (f.Raw is null || ReferenceEquals(f.Raw.Request,f.Native))) &&
        io.Transfers.Select(f=>f.Request.InvocationId).Distinct().Count()==io.Transfers.Count;
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
                if(lookup || !io.Published || io.Consumed || io.CloseRequest is not null || io.NativeRequest is null ||
                    io.Child.StateRevisionExhausted || io.Transfers.Count>=256 || nextOperation is 0 or ulong.MaxValue ||
                    request.Length is <=0 or >4096 || request.Write && (request.Input is null || request.Input.Length!=request.Length) || !request.Write && request.Input is not null) return Refuse();
                io.Transfers.EnsureCapacity(io.Transfers.Count+1);
                fact=new(request,new(io.NativeRequest,Op(),request.Offset,request.Write,request.Length,request.Input),io.Child.StateRevision,io);fact.Self=fact;
                io.Transfers.Add(fact);io.TransfersIssued++;request.ReleaseInput();
            }
            if(fact.Published is { } completed) return Ok(completed);
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
                    if(fact.SourceChanged || !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || !io.InFlight || io.Consumed || io.CloseRequest is not null ||
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
                if(fact.SourceChanged || !OriginalIoSources(io) || !HasOriginalTransferHistory(io) || io.Child.StateRevision!=fact.Revision || io.Child.StateRevisionExhausted)
                { fact.SourceChanged=true; return Refuse(); }
                return Ok(fact.Published=new(request,result.Completed,result.NoEffect,result.CopyReadData));
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
