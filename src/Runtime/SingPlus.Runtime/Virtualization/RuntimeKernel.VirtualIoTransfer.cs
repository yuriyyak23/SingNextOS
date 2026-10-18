using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;
internal sealed class KernelVirtualIoTransferRequest(VirtualIoBinding binding,ulong offset,bool write,int length,byte[]? data=null,EndpointSessionInvocationHandle? sessionInvocation=null)
{
    internal EndpointSessionInvocationHandle? SessionInvocation {get;}=sessionInvocation;
    internal VirtualIoBinding Binding {get;}=binding;
    internal ulong Offset {get;}=offset;
    internal bool Write {get;}=write;
    internal int Length {get;}=length;
    internal bool ValidInput {get;}=length is >0 and <=4096 && (write ? data is not null && data.Length==length : data is null);
    internal byte[]? Input {get;private set;}=length is >0 and <=4096 && write && data is not null && data.Length==length ? data.ToArray() : null;
    internal void ReleaseInput(){if(Input is { } bytes)Array.Clear(bytes);Input=null;}
}
public sealed partial class RuntimeKernel
{
    // A rejection here precedes service acceptance and every provider call.
    // Success is only a shape/owner filter; effect admission is checked again by the actual owner.
    internal KernelResult ValidateSessionVirtualIoInput(ProcessHandle owner, uint messageId, object? payload)
    {
        if(messageId==SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CreateAsync &&
            payload is SingPlus.Sip.Virtualization.CreateVirtualDomainRequest)
        {
            var process=Processes.Resolve(owner);
            if(!process.IsSuccess)return KernelResult.Fail(process.Error,process.Message!);
            lock(_platformMemoryUseGate)
                if(PlatformAuthority.HasActiveAuthority(PlatformIdentity(process.Value!)) || _pendingPlatformDomainBinds.Contains(owner))
                    return KernelResult.Fail(KernelError.PlatformDenied,"Create requires a fresh root admission; existing root cannot be reused.");
        }
        if (messageId == SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_BindIoAsync &&
            payload is SingPlus.Sip.Virtualization.BindVirtualIoRequest bind)
        {
            if (bind.Access == 0 || (bind.Access & ~(SingPlus.Sip.Virtualization.VirtualIoAccess.Read | SingPlus.Sip.Virtualization.VirtualIoAccess.Write)) != 0 ||
                bind.MaximumTransferBytes is <= 0 or > 1048576)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Unknown or invalid IO semantics.");
            lock (_platformMemoryUseGate)
                if (!_processPlatformDeviceLeases.TryGetValue(owner, out var devices) ||
                    devices.Count(d => d.LeaseId.Value == bind.DeviceLeaseId && d.Generation.Value == bind.DeviceGeneration) != 1)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original caller device lease unavailable.");
        }
        else if (messageId == SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_TransferIoAsync &&
                 payload is SingPlus.Sip.Virtualization.TransferVirtualIoRequest transfer)
            return ValidateHandle(new(transfer.IoId, transfer.Generation));
        else if (messageId == SingPlus.Sip.Virtualization.IVirtualizationServiceProtocol.Message_CloseIoAsync &&
                 payload is SingPlus.Sip.Virtualization.CloseVirtualIoRequest close)
            return ValidateHandle(new(close.IoId, close.Generation));
        return KernelResult.Ok();

        KernelResult ValidateHandle(VirtualIoBinding binding)
        {
            lock (_virtualIoGate)
                return _virtualIo.TryGetValue(binding.Id, out var io) && HasOriginalVirtualIo(io) && io.Binding == binding && io.Owner == owner
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.StaleGeneration, "Original caller IO handle unavailable.");
        }
    }

    private sealed class KernelIoTransferFact(KernelVirtualIoTransferRequest request,VirtualIoRecord io,long revision)
    {
        internal readonly KernelVirtualIoTransferRequest Request=request;
        internal readonly VirtualIoRecord Io=io;
        internal readonly long Revision=revision;
        internal KernelIoTransferFact? Self;
        internal readonly EndpointSessionInvocationHandle? SessionInvocation=request.SessionInvocation;
        internal bool ChargeExpected;
        internal BudgetReservationHandle? Reservation;
        internal object? ChargeOwner;
        internal PlatformOriginalVirtualIoTransferRequest? Platform;
        internal PlatformOriginalVirtualIoTransferReceipt? Raw;
        internal bool Consumed;
        internal bool SessionEffectBound;
        internal bool SessionClosureConsumed;
        internal bool SourceChanged;
        internal bool LocalNoEffect;
        internal bool PreparationStarted;
        internal bool OwnPrepareNoEffect;
        internal KernelResult? LocalRefusal;
        internal SingPlus.Sip.Virtualization.VirtualIoTransferResponse? SessionResponse;
        internal bool SnapshotReleaseRequested;
        internal bool SessionSnapshotDiscarded;
        internal bool SessionResponseRetired;
    }
    internal KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse> BindSessionVirtualIo(ProcessHandle owner,EndpointSessionInvocationHandle invocation,SingPlus.Sip.Virtualization.BindVirtualIoRequest request)
    {
        KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse> Refuse(KernelError error,string message)
        {
            var consumed=SessionInvocations.ConsumeVirtualIoBindNeverInvoked(invocation,owner);
            return consumed.IsSuccess ? KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Fail(error,message) :
                KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Fail(consumed.Error,consumed.Message!);
        }
        if(request.Access==0 || (request.Access & ~(SingPlus.Sip.Virtualization.VirtualIoAccess.Read | SingPlus.Sip.Virtualization.VirtualIoAccess.Write))!=0 ||
            request.MaximumTransferBytes is <=0 or >1048576)
            return Refuse(KernelError.PlatformFaulted,"Unknown or invalid IO semantics.");
        PlatformDeviceLease device;
        lock(_platformMemoryUseGate)
        {
            var candidates=_processPlatformDeviceLeases.TryGetValue(owner,out var devices)?devices.Where(d=>d.LeaseId.Value==request.DeviceLeaseId && d.Generation.Value==request.DeviceGeneration).ToArray():[];
            if(candidates.Length!=1)return Refuse(KernelError.StaleGeneration,"Original caller device lease unavailable.");
            device=candidates[0];
        }
        var rights=PlatformDeviceRights.None;
        if(request.Access.HasFlag(SingPlus.Sip.Virtualization.VirtualIoAccess.Read))rights|=PlatformDeviceRights.Read;
        if(request.Access.HasFlag(SingPlus.Sip.Virtualization.VirtualIoAccess.Write))rights|=PlatformDeviceRights.Write;
        var bound=BindVirtualIo(owner,request.Domain,device,new(rights,request.MaximumTransferBytes),sessionInvocation:invocation);
        return bound.IsSuccess?KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Ok(new(bound.Value.Id,bound.Value.Generation)):
            KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Fail(bound.Error,bound.Message!);
    }
    internal KernelResult CloseSessionVirtualIo(ProcessHandle owner,EndpointSessionInvocationHandle invocation,SingPlus.Sip.Virtualization.CloseVirtualIoRequest request)
    {
        var binding=new VirtualIoBinding(request.IoId,request.Generation);
        lock(_virtualIoGate)
        {
            if(!_virtualIo.TryGetValue(binding.Id,out var io) || !HasOriginalVirtualIo(io) || io.Binding!=binding || io.Owner!=owner)
            {
                var never=SessionInvocations.ConsumeVirtualIoCloseNeverInvoked(invocation,owner);
                return never.IsSuccess ? KernelResult.Fail(KernelError.StaleGeneration,"Original caller IO closure handle unavailable.") : never;
            }
            var bound=SessionInvocations.BindVirtualIoClose(invocation,owner,io,binding);
            if(!bound.IsSuccess)return bound;
        }
        return CloseVirtualIo(binding);
    }
    internal KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse> TransferSessionVirtualIo(ProcessHandle owner,EndpointSessionInvocationHandle invocation,SingPlus.Sip.Virtualization.TransferVirtualIoRequest request)
    {
        var binding=new VirtualIoBinding(request.IoId,request.Generation);
        KernelVirtualIoTransferRequest original;
        lock(_virtualIoGate)
        {
            if(!_virtualIo.TryGetValue(binding.Id,out var io) || !HasOriginalVirtualIo(io) || io.Binding!=binding || io.Owner!=owner || !HasKernelIoTransferHistory(io))
            {
                var never=SessionInvocations.ConsumeVirtualIoTransferNeverInvoked(invocation,owner);
                return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(never.IsSuccess ? KernelError.StaleGeneration : never.Error,
                    never.IsSuccess ? "Original session IO handle unavailable." : never.Message!);
            }
            var prior=io.Transfers.Where(f=>f.SessionInvocation==invocation).ToArray();
            if(prior.Length>1)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.PlatformFaulted,"Session transfer identity duplicated.");
            original=prior.Length==1?prior[0].Request:new(binding,request.Offset,request.Write,request.Length,request.Write?request.Data.ToArray():request.Data.Length==0?null:request.Data.ToArray(),invocation);
        }
        var result=TransferVirtualIo(owner,original);
        if(!result.IsSuccess)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(result.Error,result.Message!);
        try
        {
            var raw=result.Value!;
            KernelIoTransferFact? fact;
            lock(_virtualIoGate)fact=_virtualIo[binding.Id].Transfers.SingleOrDefault(f=>ReferenceEquals(f.Request,original));
            if(fact is null)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.PlatformFaulted,"Original response transfer absent.");
            if(fact.SessionResponseRetired)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.ResponseNotPending,"Original session response custody already retired.");
            if(fact.SessionResponse is null)
            {
                var data=!request.Write && raw.Completed?new SingPlus.Sip.Native.BoundedBytes(raw.CopyReadData()):default;
                lock(_virtualIoGate)
                {
                    if(!HasOriginalVirtualIo(fact.Io) || !HasKernelIoTransferHistory(fact.Io) || !ReferenceEquals(fact.Raw,raw) || fact.SourceChanged || fact.Io.SourceRevision!=fact.Revision)
                        return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.PlatformFaulted,"Original response source changed after copy.");
                    fact.SessionResponse=new(raw.Completed,raw.NoEffect,data);
                }
            }
            if(!request.Write && raw.Completed && PlatformAuthority.SupportsOriginalVirtualIoSnapshotRelease)
            {
                fact.SnapshotReleaseRequested=true;
                var released=ReleaseSessionReadSnapshot(fact);
                if(!released.IsSuccess)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(released.Error,released.Message!);
            }
            return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Ok(fact.SessionResponse.Value);
        }
        catch(InvalidOperationException){return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.PlatformFaulted,"Read snapshot closed before session publication.");}
    }
    internal KernelResult RetireSessionVirtualIoResponse(EndpointSessionInvocationRegistry.OriginalPublication original)
    {
        var settled=SessionInvocations.ReadSettledVirtualIoTransfer(original);
        if(!settled.IsSuccess)return KernelResult.Fail(settled.Error,settled.Message!);
        if(settled.Value is null)return KernelResult.Ok();
        lock(_virtualIoGate)
        {
            if(settled.Value is not KernelIoTransferFact fact || !ReferenceEquals(fact.Self,fact) ||
                !HasKernelIoTransferHistory(fact.Io) || !fact.Io.Transfers.Any(f=>ReferenceEquals(f,fact)))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original session response transfer changed.");
            fact.SessionSnapshotDiscarded=true; // No further kernel snapshot copy, independent of native closure.
            fact.SessionResponse=null; // Drop only the kernel cache; published detached readers retain their bytes.
            fact.SessionResponseRetired=true;
            return KernelResult.Ok();
        }
    }
    internal KernelResult ProgressSessionReadSnapshotCleanup(EndpointSessionInvocationHandle invocation)
    {
        KernelIoTransferFact[] pending;
        lock(_virtualIoGate)pending=_virtualIoOwners.SelectMany(io=>io.Transfers).Where(f=>f.SessionInvocation==invocation && !f.Request.Write && !f.Consumed).ToArray();
        foreach(var fact in pending)
        {
            if(!PlatformAuthority.SupportsOriginalVirtualIoSnapshotRelease)continue;
            var io=fact.Io;
            lock(_virtualIoGate)
            {
                if(!HasOriginalVirtualIo(io) || !HasKernelIoTransferHistory(io) || io.TransferInFlight ||
                    !ReferenceEquals(fact.Self,fact) || fact.SourceChanged || io.SourceRevision!=fact.Revision || fact.Platform is null)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original canceled read owner unavailable.");
                io.TransferInFlight=true;
            }
            try
            {
                // Pure query of the retained request: this path cannot admit/repeat a transfer.
                var known=PlatformAuthority.ResolveVirtualIoTransfer(io.Platform,fact.Platform!);
                if(!known.IsSuccess)return KernelResult.Fail(known.Error,known.Message!);
                lock(_virtualIoGate)
                {
                    if(!HasOriginalVirtualIo(io) || !HasKernelIoTransferHistory(io) || fact.SourceChanged || io.SourceRevision!=fact.Revision ||
                        !ReferenceEquals(known.Value!.Request,fact.Platform) || fact.Raw is not null && !ReferenceEquals(fact.Raw,known.Value))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original canceled read result changed.");
                    fact.Raw=known.Value;
                    fact.SessionSnapshotDiscarded=true; // Cancellation custody, never response publication or data copy.
                    fact.SnapshotReleaseRequested=true;
                }
            }
            finally{lock(_virtualIoGate)io.TransferInFlight=false;}
            var released=fact.Raw!.NoEffect ? ConsumeKernelIoTransferNoEffect(fact) : ReleaseSessionReadSnapshot(fact);
            if(!released.IsSuccess)return released;
        }
        return KernelResult.Ok();
    }
    private KernelResult ReleaseSessionReadSnapshot(KernelIoTransferFact fact)
    {
        if(fact.Platform is not { } platform || fact.Raw is not {Completed:true,NoEffect:false} raw || fact.Request.Write || fact.SessionResponse is null && !fact.SessionSnapshotDiscarded)
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original copied read snapshot unavailable.");
        lock(_virtualIoGate)
        {
            if(!HasOriginalVirtualIo(fact.Io) || !HasKernelIoTransferHistory(fact.Io) || fact.Io.TransferInFlight || fact.Io.CloseInFlight ||
                !ReferenceEquals(fact.Self,fact) || !fact.Io.Transfers.Any(f=>ReferenceEquals(f,fact)) ||
                !ReferenceEquals(fact.Platform,platform) || !ReferenceEquals(fact.Raw,raw) || fact.SourceChanged ||
                fact.Io.SourceRevisionExhausted || fact.Io.SourceRevision!=fact.Revision)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original snapshot cleanup owner busy or changed.");
            fact.Io.TransferInFlight=true;
        }
        try
        {
        return PlatformAuthority.ReleaseOriginalVirtualIoSnapshot(fact.Io.Platform,platform,raw,()=>
        {
            lock(_virtualIoGate)
            {
                if(!HasOriginalVirtualIo(fact.Io) || !HasKernelIoTransferHistory(fact.Io) || !ReferenceEquals(fact.Self,fact) || !fact.Io.Transfers.Any(f=>ReferenceEquals(f,fact)) || !ReferenceEquals(fact.Platform,platform) || !ReferenceEquals(fact.Raw,raw) || fact.SourceChanged || fact.Io.SourceRevisionExhausted || fact.Io.SourceRevision!=fact.Revision)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original read release owner changed.");
                var charge=fact.Reservation is { } reservation ? Budgets.ConsumeOriginalVirtualIoCharge(fact.Io.Owner,reservation,fact,fact.ChargeOwner) : KernelResult.Ok();
                if(!charge.IsSuccess)return charge;
                if(fact.SessionInvocation is { } invocation)
                {
                    var consumed=SessionInvocations.ConsumeVirtualIoTransferClosure(invocation,fact.Io.Owner,fact,snapshotOnly:true);
                    if(!consumed.IsSuccess)return consumed;
                    fact.SessionClosureConsumed=true;
                }
                fact.Consumed=true;return KernelResult.Ok();
            }
        });
        }
        finally{lock(_virtualIoGate)fact.Io.TransferInFlight=false;}
    }
    private static bool HasKernelIoTransferHistory(VirtualIoRecord io) => (ulong)io.Transfers.Count==io.TransfersIssued &&
        io.Transfers.All(f=>ReferenceEquals(f.Self,f) && ReferenceEquals(f.Io,io) && f.Request.Binding==io.Binding &&
            f.ChargeExpected==(f.Reservation is not null) && f.ChargeExpected==(f.ChargeOwner is not null)) &&
        io.Transfers.Distinct(ReferenceEqualityComparer.Instance).Count()==io.Transfers.Count &&
        io.Transfers.Select(f=>f.Request).Distinct(ReferenceEqualityComparer.Instance).Count()==io.Transfers.Count;
    internal KernelResult<PlatformOriginalVirtualIoTransferReceipt> TransferVirtualIo(ProcessHandle owner,KernelVirtualIoTransferRequest request)
    {
        KernelResult<PlatformOriginalVirtualIoTransferReceipt> Fail(KernelError error,string message)=>KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Fail(error,message);
        KernelResult<PlatformOriginalVirtualIoTransferReceipt> Refuse(KernelError error,string message)
        {
            lock(_virtualIoGate)
            {
                // Reentry may reject a request already retained by another call.
                // Even damaged routing/history cannot turn that original into
                // this call's disposable input or a never-invoked fact.
                if(request is not null && _virtualIoOwners.Any(io=>io.Transfers.Any(f=>ReferenceEquals(f.Request,request))))
                    return Fail(error,message);
                request?.ReleaseInput();
                var never=request?.SessionInvocation is { } invocation ? SessionInvocations.ConsumeVirtualIoTransferNeverInvoked(invocation,owner) : KernelResult.Ok();
                return never.IsSuccess ? Fail(error,message) : Fail(never.Error,never.Message!);
            }
        }
        if(request is null || !request.ValidInput)
            return Refuse(KernelError.PlatformFaulted,"Invalid bounded transfer input.");
        KernelIoTransferFact? cleanupOnly=null;
        lock(_virtualIoGate)
        {
            if(_virtualIo.TryGetValue(request.Binding.Id,out var originalIo) && HasOriginalVirtualIo(originalIo) && HasKernelIoTransferHistory(originalIo) && originalIo.Owner==owner && originalIo.Binding==request.Binding)
                cleanupOnly=originalIo.Transfers.SingleOrDefault(f=>ReferenceEquals(f.Request,request) && f.LocalNoEffect);
        }
        if(cleanupOnly is not null)
        {
            var cleaned=ConsumeKernelIoTransferNoEffect(cleanupOnly);
            if(cleaned.IsSuccess && cleanupOnly.Raw is {NoEffect:true} noEffect)return KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(noEffect);
            return cleaned.IsSuccess ? Fail(cleanupOnly.LocalRefusal!.Value.Error,cleanupOnly.LocalRefusal.Value.Message!) : Fail(cleaned.Error,cleaned.Message!);
        }
        VirtualIoRecord io;long revision;
        lock(_virtualIoGate)
        {
            if(!_virtualIo.TryGetValue(request.Binding.Id,out io!) || !HasOriginalVirtualIo(io) || io.Binding!=request.Binding || io.Owner!=owner || io.State!=VirtualIoState.Active || io.TransferInFlight || io.SourceRevisionExhausted)
                return Refuse(KernelError.PlatformBindingDraining,"Original IO is not admitting transfer.");
            if(!io.Rights.HasFlag(request.Write?PlatformDeviceRights.Write:PlatformDeviceRights.Read))return Refuse(KernelError.PlatformFaulted,"IO transfer right absent.");
            revision=io.SourceRevision;
        }
        var domain=_virtualDomains.ResolveAdmission(owner,io.Domain);if(!domain.IsSuccess)return Refuse(domain.Error,domain.Message!);
        var process=Processes.Resolve(owner);if(!process.IsSuccess)return Refuse(process.Error,process.Message!);
        var source=PlatformAuthority.DeviceEffectSource(io.Platform.ParentDevice,PlatformIdentity(process.Value!));if(!source.IsSuccess)return Refuse(source.Error,source.Message!);
        KernelIoTransferFact? fact=null;PlatformOriginalVirtualIoTransferReceipt? result=null;bool createdHere=false;
        var operation=UseVirtualDeliverySource(owner,io.Domain,source.Value,domain.Value!,io.Platform.Child,
            request.Write?CapabilityRights.Write:CapabilityRights.Read,io.Platform.ParentDevice.Device.ResourceId,domain.Value!.State,
            (fresh,commit)=>
            {
                var admitted=fresh(()=>commit(()=>
                {
                    lock(_virtualIoGate)
                    {
                        if(!Current() || io.TransferInFlight || !HasKernelIoTransferHistory(io))return KernelResult.Fail(KernelError.PlatformFaulted,"IO transfer source changed.");
                        var prior=io.Transfers.Where(x=>ReferenceEquals(x.Request,request)).ToArray();
                        if(prior.Length>1)return KernelResult.Fail(KernelError.PlatformFaulted,"Transfer identity duplicated.");
                        if(prior.Length==1)
                        {
                            fact=prior[0];
                            if(fact.Revision!=revision)fact.SourceChanged=true;
                            if(fact.SourceChanged)return KernelResult.Fail(KernelError.PlatformFaulted,"Original transfer observed source change; replay denied.");
                        }
                        else
                        {
                            if(io.Transfers.Count>=256)return KernelResult.Fail(KernelError.CapacityExhausted,"Transfer inventory full.");
                            io.Transfers.EnsureCapacity(io.Transfers.Count+1);
                            var reserved=request.Write?KernelResult<BudgetReservationSnapshot?>.Ok(null):Budgets.ReserveIfAttached(owner,[new(ServiceBudgetDimension.OwnedMemoryBytes,(ulong)request.Length)],BudgetReservationLifetime.LocalResource,AdmissionQosHint.None);
                            if(!reserved.IsSuccess)return KernelResult.Fail(reserved.Error,reserved.Message!);
                            fact=new(request,io,revision){Reservation=reserved.Value?.Reservation,ChargeExpected=reserved.Value is not null};fact.Self=fact;io.Transfers.Add(fact);io.TransfersIssued++;
                            createdHere=true;
                            if(fact.Reservation is { } reservation){var charge=Budgets.BindOriginalVirtualIoCharge(owner,reservation,fact);if(!charge.IsSuccess)return KernelResult.Fail(charge.Error,charge.Message!);fact.ChargeOwner=charge.Value;}
                        }
                        if(fact.SessionInvocation is { } invocation && !fact.SessionEffectBound)
                        {
                            var bound=SessionInvocations.BindVirtualIoTransfer(invocation,owner,fact);
                            if(!bound.IsSuccess)return bound;
                            fact.SessionEffectBound=true;
                        }
                        io.TransferInFlight=true;return KernelResult.Ok();
                    }
                }));
                if(!admitted.IsSuccess)
                {
                    if(createdHere && fact is not null)fact.OwnPrepareNoEffect=true;
                    return admitted;
                }
                try
                {
                    if(fact!.Platform is null)
                    {
                        if(fact.PreparationStarted)return KernelResult.Fail(KernelError.PlatformFaulted,"Original prepared transfer request unavailable; no replacement.");
                        fact.PreparationStarted=true;
                        var prepared=PlatformAuthority.PrepareVirtualIoTransfer(io.Platform,request.Offset,request.Write,request.Length,request.Input);
                        if(!prepared.IsSuccess){fact.OwnPrepareNoEffect=true;return KernelResult.Fail(prepared.Error,prepared.Message!);}
                        fact.Platform=prepared.Value;request.ReleaseInput();
                    }
                    var before=fresh(()=>commit(()=>{lock(_virtualIoGate)return Current()?KernelResult.Ok():KernelResult.Fail(KernelError.PlatformFaulted,"Transfer source lost before effect.");}));
                    if(!before.IsSuccess)return before;
                    var transferred=PlatformAuthority.InvokeVirtualIoTransfer(io.Platform,fact.Platform!);
                    if(!transferred.IsSuccess)return KernelResult.Fail(transferred.Error,transferred.Message!);
                    lock(_virtualIoGate)
                    {
                        if(fact.Raw is not null && !ReferenceEquals(fact.Raw,transferred.Value))return KernelResult.Fail(KernelError.PlatformFaulted,"Original transfer raw changed.");
                        fact.Raw=transferred.Value;
                    }
                    return fresh(()=>commit(()=>
                    {
                        lock(_virtualIoGate)
                        {
                            if(fact.Revision!=revision)fact.SourceChanged=true;
                            if(fact.SourceChanged || !Current() || !HasKernelIoTransferHistory(io) || !io.Transfers.Any(x=>ReferenceEquals(x,fact)) || !ReferenceEquals(fact.Raw!.Request,fact.Platform))return KernelResult.Fail(KernelError.PlatformFaulted,"Transfer publication source changed.");
                            result=fact.Raw;return KernelResult.Ok();
                        }
                    }));
                }
                finally{lock(_virtualIoGate)io.TransferInFlight=false;}
            },deviceSource:true);
        if(!operation.IsSuccess && fact is not null)
        {
            fact.LocalRefusal ??=operation;
            // The bridge owns the actual invocation boundary; an invoked unknown
            // fails this fence and remains charged. A failed Prepare never invokes.
            _=ConsumeKernelIoTransferNoEffect(fact);
        }
        if(operation.IsSuccess && fact is not null && result is {NoEffect:true})
        {
            var cleaned=ConsumeKernelIoTransferNoEffect(fact);
            if(!cleaned.IsSuccess)return Fail(cleaned.Error,cleaned.Message!);
        }
        return operation.IsSuccess?KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(result!):
            fact is null ? Refuse(operation.Error,operation.Message!) : Fail(operation.Error,operation.Message!);
        bool Current()=>HasOriginalVirtualIo(io) && io.Owner==owner && !io.SourceRevisionExhausted && io.SourceRevision==revision && io.State==VirtualIoState.Active;
    }

    private KernelResult ConsumeKernelIoTransferNoEffect(KernelIoTransferFact fact)
    {
        KernelResult Consume()
        {
            lock(_virtualIoGate)
            {
                var io=fact.Io;
                if(!HasOriginalVirtualIo(io) || !ReferenceEquals(fact.Self,fact) || !HasKernelIoTransferHistory(io) ||
                    io.TransferInFlight || !io.Transfers.Any(f=>ReferenceEquals(f,fact)) || fact.Raw is {NoEffect:false})
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original staged transfer cleanup owner changed.");
                fact.LocalNoEffect=true; // Actual invocation fence already committed, separate from local reclaim.
                fact.Request.ReleaseInput();
                var charge=fact.Reservation is { } reservation ? Budgets.ConsumeOriginalVirtualIoCharge(io.Owner,reservation,fact,fact.ChargeOwner) : KernelResult.Ok();
                if(!charge.IsSuccess)return charge;
                if(fact.SessionEffectBound && !fact.SessionClosureConsumed && fact.SessionInvocation is { } invocation)
                {
                    var consumed=SessionInvocations.ConsumeVirtualIoTransferNoEffect(invocation,io.Owner,fact);
                    if(!consumed.IsSuccess)return consumed;
                    fact.SessionClosureConsumed=true;
                }
                fact.Consumed=true;return KernelResult.Ok();
            }
        }
        return fact.Platform is { } platform ? fact.Raw is { } raw
            ? PlatformAuthority.ConsumeOriginalVirtualIoTransferNoEffect(fact.Io.Platform,platform,raw,Consume)
            : PlatformAuthority.ConsumeUninvokedVirtualIoTransfer(fact.Io.Platform,platform,Consume) :
            fact.OwnPrepareNoEffect ? Consume() : KernelResult.Fail(KernelError.ExternalEffectUncontained,"Original preparation/no-effect fact unavailable.");
    }
}





