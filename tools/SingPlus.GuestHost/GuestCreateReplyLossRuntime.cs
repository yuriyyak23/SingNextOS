using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

public interface IGuestCreateReplyLossRuntime : INeutralDomainRuntime,INeutralOriginalDomainCloseProvider,INeutralOriginalBindOutcomeProvider,INeutralOriginalMapOutcomeProvider { }
public class GuestCreateReplyLossRuntime : DispatchProxy
{
    public NeutralDomainRuntimeFacade Inner=null!;
    public bool Lose=true;
    public int Binds;
    public bool LoseMap;
    public int Maps;
    protected override object? Invoke(MethodInfo? method,object?[]? arguments)
    {
        if(Lose && method!.Name=="ResolveOriginalDomainBind")throw new IOException("Original root Bind query lost");
        if(LoseMap && method!.Name=="ResolveOriginalOwnedRegionMap")throw new IOException("Original parent Map query lost");
        var result=method!.Invoke(Inner,arguments);
        if(method.Name=="Bind"){Binds++;if(Lose)throw new IOException("Original root Bind reply lost after effect");}
        if(method.Name=="MapOwnedRegion"){Maps++;if(LoseMap)throw new IOException("Original parent Map reply lost after effect");}
        return result;
    }
}
