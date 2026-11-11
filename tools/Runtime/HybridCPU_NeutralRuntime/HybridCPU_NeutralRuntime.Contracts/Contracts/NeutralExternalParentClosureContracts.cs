namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralExternalParentCloseRequest(Guid InvocationId, NeutralDomainBindingLease Parent);
public readonly record struct NeutralExternalParentCloseReceipt(NeutralExternalParentCloseRequest Request, bool NoFurtherUse);

/// <summary>Closure of the separately admitted external parent. Unknown/absent
/// facts are pinned; NotInvoked is exact no-effect, never closure. Lookup is pure.</summary>
public interface INeutralExternalParentClosureProvider
{
    NeutralVirtualizationResult<NeutralExternalParentCloseReceipt> CloseExternalParent(NeutralExternalParentCloseRequest request);
    NeutralVirtualizationResult<NeutralExternalParentCloseReceipt> ResolveExternalParentClose(NeutralExternalParentCloseRequest originalRequest);
}
