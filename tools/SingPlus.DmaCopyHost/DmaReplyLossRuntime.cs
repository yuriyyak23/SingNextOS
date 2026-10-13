using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

// Optional executable host fault mode. The actual neutral model still produces
// bytes and owns all native facts; only caller response observation is lost.
public interface IDmaReplyLossRuntime : INeutralDomainRuntime, INeutralOriginalDomainCloseProvider,
    INeutralOriginalDmaCopyProvider, INeutralOriginalMmioProvider, INeutralOriginalIrqProvider { }

public class DmaReplyLossRuntime : DispatchProxy
{
    public NeutralDomainRuntimeFacade Native = null!;
    public bool LoseResponses;
    public int CopyCalls;
    protected override object? Invoke(MethodInfo? method,object?[]? arguments)
    {
        if(method!.Name==nameof(INeutralOriginalDmaCopyProvider.ResolveDmaCopy)&&LoseResponses)
            throw new IOException("Original DMA query response lost");
        var result=method.Invoke(Native,arguments);
        if(method.Name==nameof(INeutralOriginalDmaCopyProvider.CopyDma))
        {
            CopyCalls++;
            if(LoseResponses)throw new IOException("Original DMA reply lost after byte production");
        }
        return result;
    }
}
