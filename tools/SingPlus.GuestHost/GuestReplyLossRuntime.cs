using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;

public interface IGuestReplyLossRuntime : IHybridCpuChildDomainRuntimeV3, IHybridCpuArtifactReleaseRuntimeV1,
    IHybridCpuOriginalArtifactBindRuntimeV1, IHybridCpuOriginalArtifactBindDispositionRuntimeV1,
    IHybridCpuOriginalDomainCloseRuntimeV1, IHybridCpuOriginalChildCloseRuntimeV1,
    IHybridCpuOriginalGuestUnmapRuntimeV1, IHybridCpuOriginalGuestMapRuntimeV1,
    IHybridCpuOriginalChildCreateRuntimeV1, IHybridCpuOriginalChildExecutionStartRuntimeV1,
    IHybridCpuPrivateExecutableDataQuotaRuntimeV1, IHybridCpuOriginalExecutionTerminalRuntimeV1 { }

// Only reply observation is faulted; the original external execution/release runs.
public class GuestReplyLossRuntime : DispatchProxy
{
    public HybridCpuExternalRuntime Inner = null!;
    public bool LoseReleaseResponses;
    public bool LoseBindResponses;
    public bool LoseStartResponses;
    public bool LoseMapResponses;
    public int MapCalls, UnmapCalls, BindCalls, StartCalls, ReleaseCalls;
    protected override object? Invoke(MethodInfo? method,object?[]? arguments)
    {
        if(LoseReleaseResponses && method!.Name=="ResolveChildExecutableArtifactRelease")
            throw new IOException("Original artifact release query lost");
        if(LoseBindResponses && method!.Name is "ResolveChildExecutableArtifactBind" or "ResolveOriginalArtifactBindDisposition")
            throw new IOException("Original artifact Bind query lost");
        if(LoseStartResponses && method!.Name=="ResolveOriginalChildExecutionStart")throw new IOException("Original Start query lost");
        if(LoseMapResponses && method!.Name=="ResolveOriginalGuestMap")throw new IOException("Original guest Map query lost");
        var result=method!.Invoke(Inner,arguments);
        if(method.Name=="MapOriginalGuest"){MapCalls++;if(LoseMapResponses)throw new IOException("Original guest Map reply lost after effect");}
        if(method.Name=="UnmapOriginalGuest")UnmapCalls++;
        if(method.Name=="BindChildExecutableArtifact")
        {BindCalls++;if(LoseBindResponses)throw new IOException("Original artifact Bind reply lost after materialization");}
        if(method.Name=="StartOriginalChildExecution")
        {StartCalls++;if(LoseStartResponses)throw new IOException("Original Start reply lost after execution");}
        if(method.Name=="ReleaseChildExecutableArtifact")
        {
            ReleaseCalls++;
            if(LoseReleaseResponses)throw new IOException("Original artifact release reply lost after backing closure");
        }
        return result;
    }
}
