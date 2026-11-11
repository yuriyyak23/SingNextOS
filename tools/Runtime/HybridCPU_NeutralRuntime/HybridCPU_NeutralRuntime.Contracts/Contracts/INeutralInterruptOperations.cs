namespace YAKSys_Hybrid_CPU.Core;

public interface INeutralInterruptOperations
{
    NeutralInterruptBindResult BindInterrupt(NeutralDeviceLease device, NeutralInterruptSourceIdentity source);
    NeutralInterruptSignalResult SignalInterrupt(NeutralInterruptLease lease);
    NeutralInterruptPollResult PollInterrupt(NeutralInterruptLease lease);
    NeutralInterruptCompleteResult CompleteInterruptDelivery(NeutralInterruptLease lease, NeutralInterruptDeliverySequence sequence);
    NeutralInterruptCloseResult CloseInterrupt(NeutralInterruptLease lease);
}
