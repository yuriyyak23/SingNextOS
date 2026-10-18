using SingPlus.Platform;

namespace SingPlus.Platform.Host;

public sealed partial class HostPlatformAuthorityProvider
{
    public PlatformAuthorityResult<PlatformExecutionPolicyResult> ConfigureExecutionPolicy(
        PlatformProviderDomainLease domainLease,
        PlatformExecutionPolicy policy)
    {
        var policyValidation = PlatformExecutionPolicyContract.ValidatePolicy(policy);
        if (!policyValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformExecutionPolicyResult>.Fail(
                policyValidation.Status,
                policyValidation.Message!);
        }

        var domainValidation = ValidateDomain(domainLease);
        if (!domainValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformExecutionPolicyResult>.Fail(
                domainValidation.Status,
                domainValidation.Message!);
        }

        var record = _domains[domainLease.LeaseId];
        if (record.ExecutionPolicy is { } existing && existing != policy)
        {
            return PlatformAuthorityResult<PlatformExecutionPolicyResult>.Fail(
                PlatformAuthorityStatus.Denied,
                "A different execution policy is already modeled for this provider domain lease.");
        }

        record.ExecutionPolicy = policy;
        return PlatformAuthorityResult<PlatformExecutionPolicyResult>.Ok(
            new PlatformExecutionPolicyResult(record.Lease, policy));
    }
}
