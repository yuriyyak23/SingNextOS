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

/// <summary>
/// Label sideband for a protected Type-2 request. The sideband is descriptive
/// metadata only and never substitutes for ordinary operation or region authority.
/// </summary>
public sealed record CxlProtectedAcceleratorRequest(
    CxlAcceleratorRequest Request,
    ulong ProviderGeneration,
    IReadOnlyList<ProtectedAcceleratorLabelSidebandV1> LabelSideband);

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
    /// <summary>Requests cancellation; success alone does not prove closure or permit reclaim.</summary>
    PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission);
    /// <summary>Success confirms closure of the exact submitted provider operation, independently of cancellation.</summary>
    PlatformAuthorityResult Release(CxlAcceleratorSubmission submission);
}

/// <summary>Optional provider boundary that explicitly carries protected-value labels.</summary>
public interface ICxlType2ProtectedAcceleratorProvider : ICxlType2AcceleratorProvider
{
    PlatformAuthorityResult<CxlAcceleratorSubmission> SubmitProtected(
        CxlProtectedAcceleratorRequest request);
}
