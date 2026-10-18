using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
public interface IIrqReplyLossRuntime : INeutralDomainRuntime,INeutralOriginalIrqProvider,INeutralOriginalIrqSignalProvider,INeutralOriginalMmioRegistersProvider,INeutralOriginalDmaCopyProvider,INeutralOriginalMappingClosureProvider { }
public class IrqReplyLossRuntime : DispatchProxy
{
    public NeutralDomainRuntimeFacade Inner=null!;public bool Lose;public int Binds;
    public bool LoseRegister, LoseMmioClose;public int RegisterReads,RegisterWrites,MmioCloses;
    public bool LoseMmioMap;public int MmioMaps;
    public Action? AfterMmioMap;
    public int DmaCopies;
    public bool LoseDmaCopy;
    public bool MalformedDmaCopy, ForeignDmaMapping;
    public bool LoseCommandReply;
    public bool TeardownCommand;
    public bool CancelDmaBefore,CancelDmaAfter;public Action? AfterDmaCopy;
    public bool PartialDmaMapping, LoseMappingClose;public int MappingCloses;
    public bool PartialDmaGrant,LoseGrantClose;public int GrantCloses;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(Lose && method!.Name=="ResolveIrqBind")throw new IOException("Original IRQ Bind query lost");
        if(LoseRegister && method!.Name=="ResolveMmio32")throw new IOException("Original component register query lost");
        if(LoseMmioClose && method!.Name=="ResolveMmioClose")throw new IOException("Original component MMIO Close query lost");
        if(LoseMmioMap && method!.Name=="ResolveMmioMap")throw new IOException("Original component MMIO Map query lost");
        if(LoseDmaCopy && method!.Name=="ResolveDmaCopy")throw new IOException("Original component DMA query lost");
        if(LoseMappingClose && method!.Name=="ResolveOwnedRegionMappingClose")throw new IOException("Original mapping Close query lost");
        if(LoseGrantClose && method!.Name=="ResolveDmaGrantClose")throw new IOException("Original DMA grant Close query lost");
        var raw=method!.Invoke(Inner,args);
        if(method.Name=="CloseDmaGrant"){GrantCloses++;if(LoseGrantClose)throw new IOException("Original DMA grant Close reply lost after effect");}
        if(MalformedDmaCopy && (method.Name=="CopyDma" || method.Name=="ResolveDmaCopy") && raw is NeutralOriginalDmaCopyObservation observation)
            raw=observation with {NoEffectGuaranteed=true,Completed=true};
        if(method.Name=="CloseOwnedRegionMapping"){MappingCloses++;if(LoseMappingClose)throw new IOException("Original mapping Close reply lost after effect");}
        if(method.Name=="CopyDma"){DmaCopies++;AfterDmaCopy?.Invoke();if(LoseDmaCopy)throw new IOException("Original component DMA reply lost after byte effect");}
        if(method.Name=="MapMmio"){MmioMaps++;AfterMmioMap?.Invoke();if(LoseMmioMap)throw new IOException("Original component MMIO Map reply lost after effect");}
        if(method.Name=="BindInterrupt"){Binds++;if(Lose)throw new IOException("Original IRQ Bind reply lost after native outcome");}
        if(method.Name=="AccessMmio32"){if(((NeutralOriginalMmio32Request)args![0]!).Write)RegisterWrites++;else RegisterReads++;if(LoseRegister)throw new IOException("Original component register reply lost after effect");}
        if(method.Name=="CloseMmio"){MmioCloses++;if(LoseMmioClose)throw new IOException("Original component MMIO Close reply lost after retirement");}
        return raw;
    }
}
