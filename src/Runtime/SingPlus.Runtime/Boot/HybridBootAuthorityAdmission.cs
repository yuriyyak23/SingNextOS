namespace SingPlus.Runtime;

public static class HybridBootAuthorityAdmission
{
    public static KernelResult<FreshCxlAdmissionSnapshot> RevalidateWithAuthorityOwner(
        HybridBootTakeoverResult takeover,
        CxlAuthorityBridge authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!takeover.IsSuccess || takeover.Value is null)
            return KernelResult<FreshCxlAdmissionSnapshot>.Fail(KernelError.PlatformDenied, "Boot takeover did not produce fresh admission evidence.");
        if (takeover.Value.FirmwareAperture == FirmwareApertureDisposition.Quarantined)
            return KernelResult<FreshCxlAdmissionSnapshot>.Fail(KernelError.PlatformFaulted, "Firmware boot aperture retirement is ambiguous and remains quarantined.");
        var current = authority.RevalidateFreshBootEndpoint(takeover.Value.Endpoint);
        if (!current.IsSuccess)
            return KernelResult<FreshCxlAdmissionSnapshot>.Fail(current.Error, current.Message!);
        if (current.Value != takeover.Value.Endpoint)
            return KernelResult<FreshCxlAdmissionSnapshot>.Fail(KernelError.StaleGeneration, "Provider endpoint changed during boot admission.");
        return KernelResult<FreshCxlAdmissionSnapshot>.Ok(takeover.Value);
    }
}
