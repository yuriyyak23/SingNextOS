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

        var state = security.QuerySecurityState(endpoint.EndpointId, endpoint.DeviceGeneration);
        if (!state.IsSuccess)
        {
            if (!policy.RequiredForOperation)
                return KernelResult<CxlSecureComputeReadiness>.Ok(new(false, null, "Security evidence is unavailable; non-secure staged operation remains eligible."));
            return KernelResult<CxlSecureComputeReadiness>.Fail(Map(state.Status), state.Message ?? "Required security evidence is unavailable.");
        }
        if (state.Value!.EvidenceGeneration != policy.ExpectedGeneration)
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.StaleGeneration, "Security evidence generation is stale.");
        if (policy.RequireHardwareAttestation && state.Value.Assurance != CxlSecurityEvidenceAssurance.HardwareAttested)
            return KernelResult<CxlSecureComputeReadiness>.Fail(KernelError.PlatformUnsupported, "Model or emulated security evidence cannot satisfy hardware-attestation policy.");
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
}
