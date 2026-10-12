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
    }
    internal KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse> BindSessionVirtualIo(ProcessHandle owner,SingPlus.Sip.Virtualization.BindVirtualIoRequest request)
    {
        if(request.Access==0 || (request.Access & ~(SingPlus.Sip.Virtualization.VirtualIoAccess.Read | SingPlus.Sip.Virtualization.VirtualIoAccess.Write))!=0 ||
            request.MaximumTransferBytes is <=0 or >1048576)
            return KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Fail(KernelError.PlatformFaulted,"Unknown or invalid IO semantics.");
        PlatformDeviceLease device;
        lock(_platformMemoryUseGate)
        {
            var candidates=_processPlatformDeviceLeases.TryGetValue(owner,out var devices)?devices.Where(d=>d.LeaseId.Value==request.DeviceLeaseId && d.Generation.Value==request.DeviceGeneration).ToArray():[];
            if(candidates.Length!=1)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Fail(KernelError.StaleGeneration,"Original caller device lease unavailable.");
            device=candidates[0];
        }
        var rights=PlatformDeviceRights.None;
        if(request.Access.HasFlag(SingPlus.Sip.Virtualization.VirtualIoAccess.Read))rights|=PlatformDeviceRights.Read;
        if(request.Access.HasFlag(SingPlus.Sip.Virtualization.VirtualIoAccess.Write))rights|=PlatformDeviceRights.Write;
        var bound=BindVirtualIo(owner,request.Domain,device,new(rights,request.MaximumTransferBytes));
        return bound.IsSuccess?KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Ok(new(bound.Value.Id,bound.Value.Generation)):
            KernelResult<SingPlus.Sip.Virtualization.VirtualIoHandleResponse>.Fail(bound.Error,bound.Message!);
    }
    internal KernelResult CloseSessionVirtualIo(ProcessHandle owner,SingPlus.Sip.Virtualization.CloseVirtualIoRequest request)
    {
        var binding=new VirtualIoBinding(request.IoId,request.Generation);
        lock(_virtualIoGate)
            if(!_virtualIo.TryGetValue(binding.Id,out var io) || !HasOriginalVirtualIo(io) || io.Binding!=binding || io.Owner!=owner)
                return KernelResult.Fail(KernelError.StaleGeneration,"Original caller IO closure handle unavailable.");
        return CloseVirtualIo(binding);
    }
    internal KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse> TransferSessionVirtualIo(ProcessHandle owner,EndpointSessionInvocationHandle invocation,SingPlus.Sip.Virtualization.TransferVirtualIoRequest request)
    {
        var binding=new VirtualIoBinding(request.IoId,request.Generation);
        KernelVirtualIoTransferRequest original;
        lock(_virtualIoGate)
        {
            if(!_virtualIo.TryGetValue(binding.Id,out var io) || !HasOriginalVirtualIo(io) || io.Binding!=binding || io.Owner!=owner || !HasKernelIoTransferHistory(io))
                return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.StaleGeneration,"Original session IO handle unavailable.");
            var prior=io.Transfers.Where(f=>f.SessionInvocation==invocation).ToArray();
            if(prior.Length>1)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.PlatformFaulted,"Session transfer identity duplicated.");
            original=prior.Length==1?prior[0].Request:new(binding,request.Offset,request.Write,request.Length,request.Write?request.Data.ToArray():request.Data.Length==0?null:request.Data.ToArray(),invocation);
        }
        var result=TransferVirtualIo(owner,original);
        if(!result.IsSuccess)return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(result.Error,result.Message!);
        try
        {
            var raw=result.Value!;
            var data=!request.Write && raw.Completed?new SingPlus.Sip.Native.BoundedBytes(raw.CopyReadData()):default;
            return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Ok(new(raw.Completed,raw.NoEffect,data));
        }
        catch(InvalidOperationException){return KernelResult<SingPlus.Sip.Virtualization.VirtualIoTransferResponse>.Fail(KernelError.PlatformFaulted,"Read snapshot closed before session publication.");}
    }
    private static bool HasKernelIoTransferHistory(VirtualIoRecord io) => (ulong)io.Transfers.Count==io.TransfersIssued &&
        io.Transfers.All(f=>ReferenceEquals(f.Self,f) && ReferenceEquals(f.Io,io) && f.Request.Binding==io.Binding &&
            f.ChargeExpected==(f.Reservation is not null) && f.ChargeExpected==(f.ChargeOwner is not null)) &&
        io.Transfers.Distinct(ReferenceEqualityComparer.Instance).Count()==io.Transfers.Count &&
        io.Transfers.Select(f=>f.Request).Distinct(ReferenceEqualityComparer.Instance).Count()==io.Transfers.Count;
    internal KernelResult<PlatformOriginalVirtualIoTransferReceipt> TransferVirtualIo(ProcessHandle owner,KernelVirtualIoTransferRequest request)
    {
        KernelResult<PlatformOriginalVirtualIoTransferReceipt> Fail(KernelError error,string message)=>KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Fail(error,message);
        if(request is null || !request.ValidInput)
            return Fail(KernelError.PlatformFaulted,"Invalid bounded transfer input.");
        VirtualIoRecord io;long revision;
        lock(_virtualIoGate)
        {
            if(!_virtualIo.TryGetValue(request.Binding.Id,out io!) || !HasOriginalVirtualIo(io) || io.Binding!=request.Binding || io.Owner!=owner || io.State!=VirtualIoState.Active || io.TransferInFlight || io.SourceRevisionExhausted)
                return Fail(KernelError.PlatformBindingDraining,"Original IO is not admitting transfer.");
            if(!io.Rights.HasFlag(request.Write?PlatformDeviceRights.Write:PlatformDeviceRights.Read))return Fail(KernelError.PlatformFaulted,"IO transfer right absent.");
            revision=io.SourceRevision;
        }
        var domain=_virtualDomains.ResolveAdmission(owner,io.Domain);if(!domain.IsSuccess)return Fail(domain.Error,domain.Message!);
        var process=Processes.Resolve(owner);if(!process.IsSuccess)return Fail(process.Error,process.Message!);
        var source=PlatformAuthority.DeviceEffectSource(io.Platform.ParentDevice,PlatformIdentity(process.Value!));if(!source.IsSuccess)return Fail(source.Error,source.Message!);
        KernelIoTransferFact? fact=null;PlatformOriginalVirtualIoTransferReceipt? result=null;
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
                if(!admitted.IsSuccess)return admitted;
                try
                {
                    if(fact!.Platform is null)
                    {
                        var prepared=PlatformAuthority.PrepareVirtualIoTransfer(io.Platform,request.Offset,request.Write,request.Length,request.Input);
                        if(!prepared.IsSuccess)return KernelResult.Fail(prepared.Error,prepared.Message!);
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
        return operation.IsSuccess?KernelResult<PlatformOriginalVirtualIoTransferReceipt>.Ok(result!):Fail(operation.Error,operation.Message!);
        bool Current()=>HasOriginalVirtualIo(io) && io.Owner==owner && !io.SourceRevisionExhausted && io.SourceRevision==revision && io.State==VirtualIoState.Active;
    }
}
