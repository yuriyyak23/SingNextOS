using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed record CxlSecureComputeReadiness(
    bool Ready,
    CxlSecurityStateSnapshot? SecurityState,
    string Reason);

/// <summary>Combines evidence with existing authority without turning evidence into authority.</summary>
public sealed class CxlSecurityAuthority(CxlAuthorityBridge authority, ICxlSecurityStateProvider security)
{
    public KernelResult<CxlSecureComputeReadiness> Evaluate(
        RegionOwner principal,
        RegionUseHandle regionUse,
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric,
        CxlSecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        const CxlSecurityProperties all = CxlSecurityProperties.LinkEncryptionAvailable | CxlSecurityProperties.IdeEnabled |
            CxlSecurityProperties.DeviceAuthenticated | CxlSecurityProperties.FirmwareMeasured | CxlSecurityProperties.TrustedExecutionCapable;
        if ((policy.RequiredProperties & ~all) != 0 || policy.ExpectedGeneration.Value == 0)
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.InvalidMessage, "CXL security policy is invalid.");
        var device = authority.ValidateDeviceAuthority(subject, deviceLease, endpoint);
        if (!device.IsSuccess) return KernelResult<CxlSecureComputeReadiness>.Fail(device.Error, device.Message!);
        var binding = authority.RevalidateBeforeEffect(principal, regionUse, endpoint, fabric);
        if (!binding.IsSuccess) return KernelResult<CxlSecureComputeReadiness>.Fail(binding.Error, binding.Message!);

        PlatformAuthorityResult<CxlSecurityStateSnapshot> state;
        try { state = security.QuerySecurityState(endpoint.EndpointId, endpoint.DeviceGeneration); }
        catch (Exception exception)
        {
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.PlatformFaulted,
                $"CXL security provider failed during evidence query: {exception.Message}");
        }
        var finalDevice = authority.ValidateDeviceAuthority(subject, deviceLease, endpoint);
        if (!finalDevice.IsSuccess)
            return KernelResult<CxlSecureComputeReadiness>.Fail(finalDevice.Error, finalDevice.Message!);
        var finalBinding = authority.RevalidateBeforeEffect(principal, regionUse, endpoint, fabric);
        if (!finalBinding.IsSuccess)
            return KernelResult<CxlSecureComputeReadiness>.Fail(finalBinding.Error, finalBinding.Message!);
        if (!state.IsSuccess)
        {
            if (!policy.RequiredForOperation)
                return KernelResult<CxlSecureComputeReadiness>.Ok(new(false, null, "Security evidence is unavailable; non-secure staged operation remains eligible."));
            return KernelResult<CxlSecureComputeReadiness>.Fail(Map(state.Status), state.Message ?? "Required security evidence is unavailable.");
        }
        if (state.Value is null || state.Value.EndpointId != endpoint.EndpointId ||
            state.Value.DeviceGeneration != endpoint.DeviceGeneration)
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.StaleGeneration,
                "CXL security evidence does not identify the exact live endpoint generation.");
        if (state.Value.EvidenceGeneration != policy.ExpectedGeneration)
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.StaleGeneration, "Security evidence generation is stale.");
        if (policy.RequireHardwareAttestation && state.Value.Assurance != CxlSecurityEvidenceAssurance.HardwareAttested)
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.PlatformUnsupported, "Model or emulated security evidence cannot satisfy hardware-attestation policy.");
        if (policy.TrustObligation is { } obligation)
        {
            var projected = CxlDeviceTrustEvidenceAdapter.Project(
                state.Value, deviceLease, policy.CurrentPolicyGeneration);
            if (!projected.IsSuccess)
                return KernelResult<CxlSecureComputeReadiness>.Fail(projected.Error, projected.Message!);
            DeviceTrustPredicateDecisionV1 decision;
            try { decision = DeviceTrustPredicateEvaluatorV1.Evaluate(obligation, projected.Value); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.InvalidMessage, exception.Message);
            }
            if (!decision.IsSatisfied)
                return KernelResult<CxlSecureComputeReadiness>.Fail(
                    IsStale(decision.Code) ? KernelError.StaleGeneration : KernelError.PlatformDenied,
                    $"Device trust predicate was denied: {decision.Code}.");
        }
        var ready = state.Value.Health == CxlSecurityHealth.Ready &&
                    (state.Value.Properties & policy.RequiredProperties) == policy.RequiredProperties;
        if (!ready && policy.RequiredForOperation)
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.PlatformDenied, "Required CXL security predicate is not satisfied.");
        return KernelResult<CxlSecureComputeReadiness>.Ok(new(ready, state.Value,
            ready ? "Security predicate satisfied." : "Security predicate not required; ordinary staged path remains eligible."));
    }

    private static KernelError Map(PlatformAuthorityStatus status) => status switch
    {
        PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
        PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
        _ => KernelError.PlatformDenied
    };

    private static bool IsStale(DeviceTrustDecisionCodeV1 code) => code is
        DeviceTrustDecisionCodeV1.StaleProviderTrustGeneration or
        DeviceTrustDecisionCodeV1.StaleDeviceGeneration or
        DeviceTrustDecisionCodeV1.StaleAssignmentGeneration or
        DeviceTrustDecisionCodeV1.StaleResetGeneration or
        DeviceTrustDecisionCodeV1.StalePolicyGeneration;
}
