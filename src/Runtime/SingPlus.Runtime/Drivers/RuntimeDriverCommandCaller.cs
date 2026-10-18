using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>Caller correlation for one original command read, not device authority.</summary>
public sealed class RuntimeDriverCommandCaller
{
    private readonly RuntimeKernel _kernel;
    private readonly ComponentIdentity _component;
    private readonly PlatformMmio32Invocation _original;
    private int _attempted;
    private int _cancelled;

    private RuntimeDriverCommandCaller(RuntimeKernel kernel,ComponentIdentity component,PlatformMmio32Invocation original)
    { _kernel=kernel;_component=component;_original=original; }

    public static KernelResult<RuntimeDriverCommandCaller> Create(RuntimeKernel kernel,ComponentIdentity component,string resource,long offset=0)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var original=kernel.CreateComponentDriverMmio32Invocation(component,resource,offset,false);
        return original.IsSuccess
            ? KernelResult<RuntimeDriverCommandCaller>.Ok(new(kernel,component,original.Value!))
            : KernelResult<RuntimeDriverCommandCaller>.Fail(original.Error,original.Message!);
    }

    public KernelResult<uint> ReadForDispatch(CancellationToken cancellation=default)
    {
        if(CancellationObserved(cancellation))return Cancelled();
        if(Interlocked.CompareExchange(ref _attempted,1,0)!=0)
            return KernelResult<uint>.Fail(KernelError.InvalidTransition,"Original command read already attempted; reconcile without another read.");
        // No caller lock spans native callbacks. Existing bridge/native owners
        // retain the effect/raw; this flag only prevents caller redispatch.
        var result=_kernel.AccessComponentDriverMmio32(_component,_original);
        return ForDispatch(result,cancellation);
    }

    public KernelResult<uint> ResolveMetadata() => _kernel.ResolveComponentDriverMmio32(_component,_original);

    public KernelResult<uint> ResolveForDispatch(CancellationToken cancellation=default)
        => ForDispatch(ResolveMetadata(),cancellation);

    private KernelResult<uint> ForDispatch(KernelResult<uint> result,CancellationToken cancellation)
    {
        if(CancellationObserved(cancellation))return Cancelled();
        if(!result.IsSuccess)return result;
        var owner=_original.ComponentOwner!;
        var current=_kernel.ResolveDriverComponentAuthority(_component);
        if(!current.IsSuccess || current.Value.Process!=owner.Process ||
            !ReferenceEquals(current.Value.Resources,owner.DeviceResources))
            return KernelResult<uint>.Fail(KernelError.StaleGeneration,"Original command result is not current component dispatch authority.");
        return result; // Subsequent acquisition/effect still needs its own fresh admission.
    }

    private static KernelResult<uint> Cancelled()=>KernelResult<uint>.Fail(KernelError.CancellationPending,
        "Command dispatch cancelled; original metadata/cleanup remains separate from cancellation.");
    private bool CancellationObserved(CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested)Interlocked.Exchange(ref _cancelled,1);
        return Volatile.Read(ref _cancelled)!=0;
    }
}
