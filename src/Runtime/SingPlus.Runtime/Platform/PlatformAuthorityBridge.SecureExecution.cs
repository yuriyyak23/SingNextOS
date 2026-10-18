using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal ISecureExecutionProvider? SecureExecutionProvider =>
        _provider is ISecureExecutionProvider provider && provider.SecureExecutionContractVersion == 1 &&
        provider.ProductionSecureExecution && _featureManifest.Supports(PlatformFeatureFamily.SecureDomains,
            PlatformSecureComputeContract.ContractVersion, PlatformFeatureAvailability.ProductionSecure)
            ? provider : null;

    internal KernelResult<SecureExecutionContext> ResolveSecureExecutionContext(
        VirtualDomainHandle virtualDomain, SecureDomainHandle secureDomain,
        PlatformChildBinding child, SecureDomainBinding secureBinding,
        ulong localPolicyGeneration, ulong localProtectionGeneration)
    {
        lock (_secureDomainLifecycleGate)
        {
            var parent = ValidateDomain(child.ParentBinding, child.ParentBinding.Subject);
            if (!parent.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(parent.Error, parent.Message!);
            var virtualRecord = ResolveChild(child);
            if (!virtualRecord.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(virtualRecord.Error, virtualRecord.Message!);
            var secureRecord = ResolveSecure(secureBinding);
            if (!secureRecord.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(secureRecord.Error, secureRecord.Message!);
            if (secureRecord.Value!.Quarantined)
                return KernelResult<SecureExecutionContext>.Fail(KernelError.PlatformFaulted, "Secure provider lease is quarantined.");
            if (!TryGetCanonicalRootRecord(child.ParentBinding.BindingId, out var originalRoot) ||
                !ReferenceEquals(virtualRecord.Value!.CreateRoot, originalRoot) ||
                !ReferenceEquals(secureRecord.Value.CreateSource, originalRoot))
                return KernelResult<SecureExecutionContext>.Fail(KernelError.PlatformFaulted,
                    "Secure composition requires the same original canonical root of both consumers.");
            var parentLease = originalRoot.ProviderLease;
            var childLease = virtualRecord.Value!.ProviderLease;
            var secureLease = secureRecord.Value.ProviderLease;
            if (child.ParentBinding != secureBinding.Parent || childLease.ParentDomainLease != parentLease ||
                secureLease.Parent != parentLease || childLease.LeaseId.Value == 0 || childLease.Generation.Value == 0 ||
                secureLease.DomainId.Value == 0 || secureLease.Generation.Value == 0 ||
                (childLease.Intent.Authority.ChildAuthority & ~childLease.Intent.Authority.ParentAuthority) != 0)
                return KernelResult<SecureExecutionContext>.Fail(KernelError.WrongPlatformDomain, "Composition requires exact parent and non-amplifying provider leases.");
            return KernelResult<SecureExecutionContext>.Ok(new(child.ParentBinding, virtualDomain, secureDomain,
                child, secureBinding, parentLease, childLease, secureLease, localPolicyGeneration,
                localProtectionGeneration, secureRecord.Value.ProvenPropertiesKey));
        }
    }

    internal KernelResult CommitSecureExecutionContext(SecureExecutionContext expected, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            var current = ResolveSecureExecutionContext(expected.Virtual, expected.Secure, expected.Child,
                expected.SecureBinding, expected.LocalPolicyGeneration, expected.LocalProtectionGeneration);
            return current.IsSuccess && current.Value == expected ? commit()
                : KernelResult.Fail(KernelError.StaleGeneration, "Exact secure composition bridge context changed.");
        }
    }
}
