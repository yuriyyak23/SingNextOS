using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;
namespace SingPlus.Runtime;

/// <summary>Caller-retained original register invocation. Never a mapping permission.</summary>
public sealed class PlatformMmio32Invocation
{
    public PlatformMmio32Invocation(PlatformMmioLease mapping,long offset,bool write,uint value=0)
    { Original=this;Mapping=mapping;Offset=offset;Write=write;Value=value; }
    internal PlatformMmio32Invocation Original { get; }
    internal RuntimeKernel? ComponentKernel { get; }
    internal ComponentAdmissionRecord? ComponentOwner { get; }
    internal PlatformMmio32Invocation(PlatformMmioLease mapping,long offset,bool write,uint value,
        RuntimeKernel kernel,ComponentAdmissionRecord owner):this(mapping,offset,write,value)
    { ComponentKernel=kernel;ComponentOwner=owner; }
    public PlatformMmioLease Mapping { get; }
    public long Offset { get; }
    public bool Write { get; }
    public uint Value { get; }
}
public sealed partial class RuntimeKernel
{
    public KernelResult<uint> AccessPlatformMmio32(ProcessHandle subject,PlatformMmio32Invocation invocation)
        => AccessPlatformMmio32Core(subject,invocation,null);
    private KernelResult<uint> AccessPlatformMmio32Core(ProcessHandle subject,PlatformMmio32Invocation invocation,
        Func<Func<KernelResult>,KernelResult>? sourceCommit)
    {
        KernelResult<SingProcess> resolved =Processes.Resolve(subject);if(!resolved.IsSuccess)return KernelResult<uint>.Fail(resolved.Error,resolved.Message!);
        SingProcess process =resolved.Value!;
        KernelResult Authorize(CapabilityId capability,Func<KernelResult> commit)
        {
            lock(_platformMemoryUseGate)
            {
                KernelResult<SingProcess> current =Processes.Resolve(subject);if(!current.IsSuccess)return KernelResult.Fail(current.Error,current.Message!);
                if(!ReferenceEquals(current.Value,process))return KernelResult.Fail(KernelError.StaleGeneration,"Register process incarnation changed.");
                KernelResult accepting =EnsureProcessAcceptsNewEffects(process);if(!accepting.IsSuccess)return accepting;
                PlatformMmioLease map =invocation.Mapping;
                KernelResult Commit()=>CapabilityAuthority.CommitMmioAdmission(capability,process.DomainId,subject.Generation,
                    CapabilityRights.Map|(invocation.Write?CapabilityRights.Write:CapabilityRights.Read),
                    CapabilityResourceIds.MmioRegion(map.DeviceLease.Device.ResourceId,map.Region.ResourceId,map.Region.ByteLength),commit);
                return sourceCommit is null?Commit():sourceCommit(Commit);
            }
        }
        return PlatformAuthority.AccessMmio32(invocation,PlatformIdentity(process),Authorize,Regions);
    }
    public KernelResult<uint> ResolvePlatformMmio32(ProcessHandle subject,PlatformMmio32Invocation invocation)
    {
        KernelResult<SingProcess> resolved =Processes.Resolve(subject);if(!resolved.IsSuccess)return KernelResult<uint>.Fail(resolved.Error,resolved.Message!);
        return PlatformAuthority.ResolveMmio32(invocation,PlatformIdentity(resolved.Value!));
    }
}
