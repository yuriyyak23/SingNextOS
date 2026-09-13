using SingPlus.Contracts;

namespace SingPlus.Platform;

public readonly record struct CxlAcceleratorSubmissionId(ulong Value);
public readonly record struct CxlAcceleratorSubmissionGeneration(ulong Value);
public readonly record struct CxlFabricBindingRef(
    CxlFabricBindingId BindingId,
    CxlFabricBindingGeneration Generation);

public sealed record CxlAcceleratorRequest(
    ComputeOperationKind Operation,
    ComputePublicationPath PublicationPath,
    OperationBinding OperationBinding,
    IReadOnlyList<RegionUseDescriptor> RegionUses,
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    CxlFabricBindingRef FabricBinding);

public sealed record CxlAcceleratorSubmission(
    CxlAcceleratorSubmissionId SubmissionId,
    CxlAcceleratorSubmissionGeneration Generation,
    OperationBinding OperationBinding,
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    CxlFabricBindingRef FabricBinding);

public readonly record struct CxlAcceleratorCompletion(
    CxlAcceleratorSubmission Submission,
    ExternalOperationCompletionDisposition Disposition);

public readonly record struct CxlAcceleratorVisibility(
    CxlAcceleratorSubmission Submission,
    ExternalVisibilityRequirement Requirement,
    bool Satisfied);

public interface ICxlType2AcceleratorProvider
{
    /// <summary>
    /// Submit returns NotAccepted only when no provider effect occurred. Unavailable means
    /// acceptance may be ambiguous. Implementations must report failures and must not throw.
    /// </summary>
    PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request);
    PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission);
    PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(
        CxlAcceleratorSubmission submission,
        ExternalVisibilityRequirement requirement);
    PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission);
    PlatformAuthorityResult Release(CxlAcceleratorSubmission submission);
}
