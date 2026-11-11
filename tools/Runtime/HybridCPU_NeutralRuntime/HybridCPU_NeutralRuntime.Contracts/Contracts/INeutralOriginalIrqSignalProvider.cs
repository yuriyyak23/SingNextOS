namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Opaque original software-source invocation, not Sing permission.</summary>
public sealed class NeutralOriginalIrqSignalRequest
{
    internal NeutralOriginalIrqSignalRequest(Guid invocation,NeutralOriginalIrqBindRequest admission,NeutralInterruptLease binding,ulong revision,object originalOwner)
    { InvocationId=invocation;Admission=admission;Binding=binding;SourceRevision=revision;OriginalOwner=originalOwner; }
    internal object OriginalOwner {get;}
    public Guid InvocationId {get;}
    public NeutralOriginalIrqBindRequest Admission {get;}
    public NeutralInterruptLease Binding {get;}
    public ulong SourceRevision {get;}
}
public sealed class NeutralOriginalIrqSignalObservation
{
    internal NeutralOriginalIrqSignalObservation(NeutralOriginalIrqSignalRequest request,bool signaled,bool noEffect,NeutralInterruptSignalDecision decision,NeutralInterruptDeliverySequence sequence)
    {Original=this;Request=request;IsSignaled=signaled;NoEffectGuaranteed=noEffect;Decision=decision;Sequence=sequence;}
    internal NeutralOriginalIrqSignalObservation Original {get;}
    public NeutralOriginalIrqSignalRequest Request {get;}
    public bool IsSignaled {get;}
    public bool NoEffectGuaranteed {get;}
    public NeutralInterruptSignalDecision Decision {get;}
    public NeutralInterruptDeliverySequence Sequence {get;}
}
/// <summary>Trusted software input source. Names select an unambiguous already admitted
/// canonical IRQ; they confer no authority. Unknown history never authorizes repeat.</summary>
public interface INeutralOriginalIrqSignalProvider
{
    NeutralOriginalIrqSignalRequest? PrepareOriginalIrqSignal(string deviceResourceId,string sourceResourceId);
    NeutralOriginalIrqSignalObservation? SignalOriginalIrq(NeutralOriginalIrqSignalRequest request);
    NeutralOriginalIrqSignalObservation? ResolveOriginalIrqSignal(NeutralOriginalIrqSignalRequest request);
}
