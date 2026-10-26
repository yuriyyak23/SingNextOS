namespace YAKSys_Hybrid_CPU.Core;
public readonly record struct NeutralOriginalIrqBindRequest(Guid InvocationId,Guid RuntimeIncarnation,Guid DeviceBindInvocationId,NeutralDeviceLease Device,NeutralInterruptSourceIdentity Source);
public readonly record struct NeutralOriginalIrqBindObservation(NeutralOriginalIrqBindRequest Request,bool SourceFenceMatched,bool NoEffectGuaranteed,NeutralInterruptBindResult Observation);
public readonly record struct NeutralOriginalIrqCloseRequest(Guid InvocationId,Guid RuntimeIncarnation,Guid BindInvocationId,NeutralInterruptLease Binding);
public readonly record struct NeutralOriginalIrqCloseObservation(NeutralOriginalIrqCloseRequest Request,bool SourceFenceMatched,bool NoEffectGuaranteed,NeutralInterruptCloseResult Observation);
public readonly record struct NeutralOriginalIrqPollRequest(Guid InvocationId,NeutralOriginalIrqBindRequest Admission,NeutralInterruptLease Binding);
public readonly record struct NeutralOriginalIrqPollObservation(NeutralOriginalIrqPollRequest Request,bool SourceFenceMatched,bool NoEffectGuaranteed,NeutralInterruptPollResult Observation,ulong SourceRevision);
public readonly record struct NeutralOriginalIrqCompleteRequest(Guid InvocationId,NeutralOriginalIrqBindRequest Admission,NeutralInterruptLease Binding,NeutralInterruptDeliverySequence Sequence,Guid PollInvocationId);
public readonly record struct NeutralOriginalIrqCompleteObservation(NeutralOriginalIrqCompleteRequest Request,bool SourceFenceMatched,bool NoEffectGuaranteed,NeutralInterruptCompleteResult Observation,ulong SourceRevision);
public readonly record struct NeutralOriginalIrqContainRequest(Guid InvocationId,NeutralOriginalIrqBindRequest Admission,NeutralInterruptLease Binding);
/// <summary>Exact original native lifetime retired, including pending delivery. No subsequent native signal, poll or completion can use it. Does not settle local events or backing users.</summary>
public readonly record struct NeutralOriginalIrqContainObservation(NeutralOriginalIrqContainRequest Request,bool NoFurtherDeliveryGuaranteed);
/// <summary>Original native IRQ source fence. Lookup consumes surviving facts; absence proves nothing.</summary>
public interface INeutralOriginalIrqProvider : INeutralOriginalDeviceProvider
{
    NeutralOriginalIrqBindObservation BindInterrupt(NeutralOriginalIrqBindRequest request);
    bool ValidateIrqAdmission(NeutralOriginalIrqBindRequest request);
    NeutralOriginalIrqBindObservation? ResolveIrqBind(NeutralOriginalIrqBindRequest request);
    NeutralOriginalIrqPollObservation PollInterrupt(NeutralOriginalIrqPollRequest request);
    NeutralOriginalIrqPollObservation? ResolveIrqPoll(NeutralOriginalIrqPollRequest request);
    bool ValidateIrqPoll(NeutralOriginalIrqPollRequest request);
    NeutralOriginalIrqCompleteObservation CompleteInterruptDelivery(NeutralOriginalIrqCompleteRequest request);
    NeutralOriginalIrqCompleteObservation? ResolveIrqCompletion(NeutralOriginalIrqCompleteRequest request);
    bool ValidateIrqCompletion(NeutralOriginalIrqCompleteRequest request);
    NeutralOriginalIrqCloseObservation CloseInterrupt(NeutralOriginalIrqCloseRequest request);
    NeutralOriginalIrqCloseObservation? ResolveIrqClose(NeutralOriginalIrqCloseRequest request);
    NeutralOriginalIrqContainObservation ContainInterrupt(NeutralOriginalIrqContainRequest request);
    NeutralOriginalIrqContainObservation? ResolveIrqContainment(NeutralOriginalIrqContainRequest request);
}
