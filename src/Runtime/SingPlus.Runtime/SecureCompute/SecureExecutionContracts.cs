using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

// Kernel-private composition contract. No SIP capability or provider identity is minted.
internal readonly record struct SecureExecutionBinding(ulong Id, ulong Generation);
internal readonly record struct SecureExecutionContext(
    PlatformDomainBinding Parent, VirtualDomainHandle Virtual, SecureDomainHandle Secure,
    PlatformChildBinding Child, PlatformAuthorityBridge.SecureDomainBinding SecureBinding,
    PlatformProviderDomainLease ParentLease, PlatformProviderChildDomainLease ChildLease,
    PlatformProviderSecureDomainLease SecureLease, ulong LocalPolicyGeneration,
    ulong LocalProtectionGeneration, string ProvenProperties);
internal readonly record struct SecureExecutionRequest(
    SecureExecutionBinding Binding, SecureExecutionContext Context);
internal readonly record struct SecureExecutionReceipt(
    SecureExecutionRequest Request, ulong Correlation, ulong ProviderGeneration,
    ulong PolicyGeneration, ulong ProtectionGeneration, uint ContractVersion,
    bool ProductionSecure);
internal readonly record struct SecureExecutionClosure(
    SecureExecutionRequest Request, SecureExecutionReceipt? Receipt,
    bool ProviderClosed, bool ProviderEffectContained);

// Admission failures can have effects. Close(request, receipt-or-null) must resolve
// the exact request even when the admission response was lost.
internal interface ISecureExecutionProvider
{
    uint SecureExecutionContractVersion { get; }
    bool ProductionSecureExecution { get; }
    PlatformAuthorityResult<SecureExecutionReceipt> BindSecureExecution(SecureExecutionRequest request);
    PlatformAuthorityResult<SecureExecutionReceipt> RevalidateSecureExecution(SecureExecutionReceipt receipt);
    PlatformAuthorityResult<SecureExecutionClosure> CloseSecureExecution(
        SecureExecutionRequest request, SecureExecutionReceipt? receipt);
}
