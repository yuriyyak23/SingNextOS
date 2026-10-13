using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

public interface IGuestCreateReplyLossRuntime : INeutralDomainRuntime,INeutralOriginalDomainCloseProvider,INeutralOriginalBindOutcomeProvider { }
public class GuestCreateReplyLossRuntime : DispatchProxy
{
    public NeutralDomainRuntimeFacade Inner=null!;
    public bool Lose=true;
    public int Binds;
    protected override object? Invoke(MethodInfo? method,object?[]? arguments)
    {
        if(Lose && method!.Name=="ResolveOriginalDomainBind")throw new IOException("Original root Bind query lost");
        var result=method!.Invoke(Inner,arguments);
        if(method.Name=="Bind"){Binds++;if(Lose)throw new IOException("Original root Bind reply lost after effect");}
        return result;
    }
}
