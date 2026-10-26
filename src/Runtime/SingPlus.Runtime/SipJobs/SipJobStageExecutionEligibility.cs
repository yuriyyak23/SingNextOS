namespace SingPlus.Runtime;

internal enum SipJobStageExecutionClass
{
    ManagedDefault = 0,
}

internal sealed record SipJobStageExecutionEligibilityDescriptor(
    uint Version,
    string StageId,
    SipJobStageExecutionClass ExecutionClass,
    string ProviderNeutralContractId,
    uint ProviderNeutralContractVersion);

internal enum SipJobStageExecutionEligibilityError
{
    None = 0,
    UnknownVersion,
    Malformed,
    UnknownExecutionClass,
    FutureGatedRequiresProviderContract,
}

internal readonly record struct SipJobStageExecutionEligibilityResult(
    SipJobStageExecutionEligibilityError Error,
    bool MayUseManagedRuntime,
    bool MayUseProvider,
    string? Detail)
{
    internal bool IsSuccess => Error == SipJobStageExecutionEligibilityError.None;
}

internal static class SipJobStageExecutionEligibilityVerifier
{
    internal const uint Version = 1;
    internal const string NoProviderContract = "None";

    internal static SipJobStageExecutionEligibilityResult Verify(
        SipJobStageExecutionEligibilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Version != Version)
            return Fail(SipJobStageExecutionEligibilityError.UnknownVersion, "Unknown stage execution eligibility version.");
        if (string.IsNullOrWhiteSpace(descriptor.StageId) || descriptor.StageId != descriptor.StageId.Trim() ||
            string.IsNullOrWhiteSpace(descriptor.ProviderNeutralContractId) ||
            descriptor.ProviderNeutralContractId != descriptor.ProviderNeutralContractId.Trim())
            return Fail(SipJobStageExecutionEligibilityError.Malformed, "Execution eligibility identities must be canonical.");
        if (!Enum.IsDefined(descriptor.ExecutionClass))
            return Fail(SipJobStageExecutionEligibilityError.UnknownExecutionClass, "Unknown semantic execution class.");

        if (descriptor.ProviderNeutralContractId != NoProviderContract || descriptor.ProviderNeutralContractVersion != 0)
            return Fail(SipJobStageExecutionEligibilityError.FutureGatedRequiresProviderContract,
                "No current provider-neutral contract losslessly represents a SipJob scheduling hint.");

        return new(SipJobStageExecutionEligibilityError.None, MayUseManagedRuntime: true,
            MayUseProvider: false, null);
    }

    private static SipJobStageExecutionEligibilityResult Fail(
        SipJobStageExecutionEligibilityError error, string detail) =>
        new(error, MayUseManagedRuntime: false, MayUseProvider: false, detail);
}
