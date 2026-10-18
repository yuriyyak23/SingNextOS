using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

public interface IMmioReplyLossRuntime : INeutralDomainRuntime,INeutralOriginalMmioRegistersProvider { }
public class MmioReplyLossRuntime : DispatchProxy
{
    public NeutralDomainRuntimeFacade Inner=null!;
    public bool Lose;
    public bool LoseClose;
    public int MmioMaps;
    public int MmioCloses;
    public bool LoseAccess;
    public int Writes,Reads;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(Lose && method!.Name=="ResolveMmioMap")throw new IOException("Original MMIO Map query lost");
        if(LoseClose && method!.Name=="ResolveMmioClose")throw new IOException("Original MMIO Close query lost");
        if(LoseAccess && method!.Name=="ResolveMmio32")throw new IOException("Original register query lost");
        var result=method!.Invoke(Inner,args);
        if(method.Name=="MapMmio"){MmioMaps++;if(Lose)throw new IOException("Original MMIO Map reply lost after native outcome");}
        if(method.Name=="CloseMmio"){MmioCloses++;if(LoseClose)throw new IOException("Original MMIO Close reply lost after native retirement");}
        if(method.Name=="AccessMmio32")
        { if(((NeutralOriginalMmio32Request)args![0]!).Write)Writes++;else Reads++;if(LoseAccess)throw new IOException("Original register reply lost after native effect"); }
        return result;
    }
}
