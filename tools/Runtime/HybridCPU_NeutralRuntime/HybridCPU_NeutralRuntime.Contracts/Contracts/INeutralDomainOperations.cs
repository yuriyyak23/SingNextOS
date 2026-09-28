namespace YAKSys_Hybrid_CPU.Core;

public interface INeutralDomainOperations
{
    NeutralDomainBindResult Bind(NeutralDomainProfile profile);
    NeutralDomainCloseResult Close(NeutralDomainBindingLease lease);
    NeutralExecutionTransitionResult TransitionExecution(NeutralDomainBindingLease lease, NeutralExecutionTransition transition);
}
