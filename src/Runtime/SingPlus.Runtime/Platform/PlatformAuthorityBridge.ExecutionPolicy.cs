using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>
/// Locally published scheduler-policy state for one exact platform-domain binding.
/// The feature descriptor states whether the provider only modeled the request or
/// supplied stronger admission/execution evidence. This snapshot is not authority.
/// </summary>
public readonly record struct PlatformExecutionPolicyRegistration(
    PlatformDomainBinding DomainBinding,
    PlatformExecutionPolicy Policy,
    PlatformFeatureDescriptor Feature);

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformExecutionPolicyRegistration> ConfigureExecutionPolicy(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformExecutionPolicy policy)
    {
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        var policyValidation = PlatformExecutionPolicyContract.ValidatePolicy(policy);
        if (!policyValidation.IsSuccess)
        {
            return FromProviderFailure<PlatformExecutionPolicyRegistration>(
                policyValidation.Status,
                policyValidation.Message);
        }

        var feature = _featureManifest.Resolve(PlatformFeatureFamily.ExecutionPolicy);
        if (feature.ContractVersion < PlatformExecutionPolicyContract.ContractVersion ||
            !PlatformExecutionPolicyContract.IsSupportedAvailability(feature.Availability))
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                KernelError.PlatformUnsupported,
                $"The platform provider does not expose execution-policy contract v{PlatformExecutionPolicyContract.ContractVersion} as ModelOnly, RuntimeAdmission, or Executable.");
        }

        if (_provider is not IPlatformExecutionPolicyProvider policyProvider)
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                KernelError.PlatformUnsupported,
                "The bound platform provider does not implement the execution-policy contract.");
        }

        var record = _domains[binding.BindingId];
        if (record.ExecutionPolicy is { } existing)
        {
            return existing.Policy == policy
                ? KernelResult<PlatformExecutionPolicyRegistration>.Ok(existing)
                : KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                    KernelError.PlatformBindingActive,
                    "A different immutable execution policy is already registered for this platform-domain binding.");
        }

        var providerResult = policyProvider.ConfigureExecutionPolicy(
            record.ProviderLease,
            policy);
        if (!providerResult.IsSuccess)
        {
            if (RequiresDomainQuarantine(providerResult.Status))
                QuarantineDomain(record);

            return FromProviderFailure<PlatformExecutionPolicyRegistration>(
                providerResult.Status,
                providerResult.Message);
        }

        var resultValidation = PlatformExecutionPolicyContract.ValidateResult(
            record.ProviderLease,
            policy,
            providerResult.Value!);
        if (!resultValidation.IsSuccess)
        {
            QuarantineDomain(record);
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                KernelError.PlatformFaulted,
                resultValidation.Message ??
                "The platform provider returned malformed execution-policy evidence.");
        }

        var registration = new PlatformExecutionPolicyRegistration(
            binding,
            policy,
            feature);
        record.ExecutionPolicy = registration;
        return KernelResult<PlatformExecutionPolicyRegistration>.Ok(registration);
    }
}
