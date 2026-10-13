using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

public interface IComputeReplyLossRuntime : INeutralDomainRuntime, INeutralOriginalDomainCloseProvider,
    INeutralOriginalMappingClosureProvider, INeutralOriginalDsc1PreparationProvider { }

// Executable software host fault mode: native model still produces and retains
// original facts; only the caller observation is interrupted.
public class ComputeReplyLossRuntime : DispatchProxy
{
    public NeutralDomainRuntimeFacade Native=null!;
    public bool LoseResponses;
    public int Copies;
    protected override object? Invoke(MethodInfo? method,object?[]? arguments)
    {
        if(method!.Name==nameof(INeutralOriginalDsc1CopyProvider.ResolveDsc1Copy)&&LoseResponses)return null;
        var result=method.Invoke(Native,arguments);
        if(method.Name==nameof(INeutralOriginalDsc1CopyProvider.CopyDsc1))
        {
            Copies++;
            if(LoseResponses)throw new IOException("Original software Copy reply lost after actual byte effect");
        }
        return result;
    }
}
