using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;
namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformMmioLease> BindPlatformMmioBacking(ProcessHandle subject,PlatformDeviceLease device,
        CapabilityId capability,long offset,long length,PlatformMmioAccess access,OwnedBuffer<byte> buffer)
        => BindPlatformMmioBackingCore(subject,device,capability,offset,length,access,buffer,null);

    private KernelResult<PlatformMmioLease> BindPlatformMmioBackingCore(ProcessHandle subject,PlatformDeviceLease device,
        CapabilityId capability,long offset,long length,PlatformMmioAccess access,OwnedBuffer<byte> buffer,
        (ProcessHandle Principal,CapabilityId Capability)? source,Func<Func<KernelResult>,KernelResult>? sourceCommit=null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        try { if(!PlatformAuthority.SupportsMmioRegisterBacking)return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformUnsupported,"Original managed MMIO backing producer absent."); }
        catch(Exception e) when(e is not StackOverflowException){return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted,e.Message);}
        var resolved=Processes.Resolve(subject);if(!resolved.IsSuccess)return KernelResult<PlatformMmioLease>.Fail(resolved.Error,resolved.Message!);
        var payload=new MmioBufferPayload(buffer,new(resolved.Value!.DomainId,subject.Generation));
        lock(_platformMemoryUseGate)
        {
            var accepting=EnsureProcessAcceptsNewEffects(resolved.Value!);
            if(!accepting.IsSuccess)return KernelResult<PlatformMmioLease>.Fail(accepting.Error,accepting.Message!);
            var acquired=Regions.AcquireMmioUse(payload);
            if(!acquired.IsSuccess)return KernelResult<PlatformMmioLease>.Fail(acquired.Error,acquired.Message!);
            payload.Use=acquired.Value!.Handle;
            try { payload.ReserveBytes(); }
            catch(Exception e) when(e is InvalidOperationException or OutOfMemoryException)
            {
                payload.LocalRollbackPending = true;
                var release=Regions.ReleaseUnadmittedMmioUse(payload);
                return KernelResult<PlatformMmioLease>.Fail(release.IsSuccess?(e is OutOfMemoryException?KernelError.CapacityExhausted:KernelError.RegionUseConflict):release.Error,release.IsSuccess?e.Message:release.Message!);
            }
        }
        KernelResult<PlatformMmioLease> result;
        try { result=BindPlatformMmioCore(subject,device,capability,offset,length,access,source,sourceCommit:sourceCommit,backing:payload); }
        catch(Exception e) when(e is not StackOverflowException){result=KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted,e.Message);}
        lock (_platformMemoryUseGate)
        {
            if(!payload.AdmissionStarted && payload.AdmissionOwner is null)
            {
                payload.LocalRollbackPending = true;
                var release=Regions.ReleaseUnadmittedMmioUse(payload);
                if(!release.IsSuccess)return KernelResult<PlatformMmioLease>.Fail(release.Error,release.Message!);
            }
        }
        return result;
    }
}
