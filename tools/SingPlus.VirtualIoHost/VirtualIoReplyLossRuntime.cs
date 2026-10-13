using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;

public interface IVirtualIoReplyLossRuntime : IHybridCpuChildDomainRuntimeV3,
    IHybridCpuOriginalDomainCloseRuntimeV1, IHybridCpuOriginalChildCloseRuntimeV1,
    IHybridCpuOriginalChildCreateRuntimeV1, IHybridCpuOriginalVirtualIoRuntimeV1,
    IHybridCpuOriginalVirtualIoContainmentRuntimeV1, IHybridCpuOriginalParentDeviceNoEffectRuntimeV1,
    IHybridCpuOriginalVirtualIoTransferRuntimeV1 { }

// Fault observation only: the external runtime still produces and owns bytes/backing.
public class VirtualIoReplyLossRuntime : DispatchProxy
{
    public HybridCpuExternalRuntime Inner = null!;
    public bool LoseTransferResponses;
    public bool LoseCloseResponses;
    public bool LoseBindResponses;
    public bool LoseContainmentResponses;
    public int BindCalls, TransferCalls, CloseCalls, ContainmentCalls;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if(LoseTransferResponses && method!.Name=="ResolveOriginalVirtualIoTransfer")
            throw new IOException("Original transfer query lost");
        if(LoseCloseResponses && method!.Name=="ResolveOriginalVirtualIoClosure")
            throw new IOException("Original IO closure query lost");
        if(LoseBindResponses && method!.Name=="ResolveOriginalVirtualIo")throw new IOException("Original IO Bind query lost");
        if(LoseContainmentResponses && method!.Name=="ResolveOriginalVirtualIoBackingContainment")throw new IOException("Original Bind containment query lost");
        var result=method!.Invoke(Inner,arguments);
        if(method.Name=="BindOriginalVirtualIo")
        {BindCalls++;if(LoseBindResponses)throw new IOException("Original IO Bind reply lost after backing effect");}
        if(method.Name=="ContainOriginalVirtualIoBacking")
        {ContainmentCalls++;if(LoseContainmentResponses)throw new IOException("Original backing containment reply lost after effect");}
        if(method.Name=="CloseOriginalVirtualIo")
        {
            CloseCalls++;
            if(LoseCloseResponses)throw new IOException("Original IO closure reply lost after effect");
        }
        if(method.Name=="TransferOriginalVirtualIo")
        {
            TransferCalls++;
            if(LoseTransferResponses)throw new IOException("Original transfer reply lost after effect");
        }
        return result;
    }
}
