using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>Projects provider sideband into predicate evidence; it is not an authority provider.</summary>
public static class CxlDeviceTrustEvidenceAdapter
{
    public static KernelResult<TrustEvidenceV1> Project(
        CxlSecurityStateSnapshot state,
        PlatformDeviceLease assignment,
        ulong policyGeneration)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (assignment.Generation.Value == 0 || policyGeneration == 0 ||
            state.ProviderTrustGeneration == 0 || state.ResetGeneration == 0 ||
            state.EvidenceGeneration.Value == 0)
            return KernelResult<TrustEvidenceV1>.Fail(KernelError.InvalidMessage,
                "CXL trust evidence has an incomplete generation tuple.");
        var assurance = state.Assurance switch
        {
            CxlSecurityEvidenceAssurance.ModelOnly => DeviceTrustAssuranceV1.ModelOnly,
            CxlSecurityEvidenceAssurance.HardwareAttested => DeviceTrustAssuranceV1.HardwareAttested,
            _ => (DeviceTrustAssuranceV1)0,
        };
        var evidence = new TrustEvidenceV1(1, assignment.Device.ResourceId,
            state.FirmwareMeasurementDigest, assurance, state.ProviderTrustGeneration,
            state.DeviceGeneration.Value, assignment.Generation.Value, state.ResetGeneration,
            policyGeneration, state.EvidenceGeneration.Value);
        try { return KernelResult<TrustEvidenceV1>.Ok(evidence.Validate()); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return KernelResult<TrustEvidenceV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
    }
}
