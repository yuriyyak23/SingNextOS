using SingPlus.Contracts;

namespace SingPlus.Platform.Host;

/// <summary>Synchronous staged Type-2 model with no transport, ISA, or compiler dependency.</summary>
public sealed class CxlType2ModelAccelerator : ICxlType2AcceleratorProvider
{
    private readonly Dictionary<CxlAcceleratorSubmissionId, CxlAcceleratorSubmission> _live = [];
    private ulong _nextId = 1;
    public int SubmitCount { get; private set; }
    public bool DeviceAvailable { get; set; } = true;
    public bool VisibilitySatisfied { get; set; } = true;
    public bool CancellationFails { get; set; }
    public bool ReleaseFails { get; set; }
    public bool SubmitAcceptanceAmbiguous { get; set; }
    public bool ReturnMalformedSubmission { get; set; }
    public bool SubmitThrowsAfterAcceptance { get; set; }

    public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request)
    {
        if (!DeviceAvailable) return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.NotAccepted, "Type-2 device rejected submission before acceptance.");
        if (request.PublicationPath != ComputePublicationPath.Staged)
            return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Unsupported, "The model provider supports staged publication only.");
        if (request.OperationBinding.BindingId.Value == 0 || request.RegionUses.Count != 2 ||
            request.RegionUses[0].Mode != RegionUseMode.ReadOnly || request.RegionUses[1].Mode != RegionUseMode.StagedOutput)
            return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Denied, "Type-2 descriptor is malformed or has incompatible RegionUse modes.");
        var submission = new CxlAcceleratorSubmission(new(_nextId++), new(1), request.OperationBinding,
            request.EndpointId, request.DeviceGeneration, request.FabricBinding);
        _live.Add(submission.SubmissionId, submission);
        SubmitCount++;
        if (SubmitThrowsAfterAcceptance)
            throw new InvalidOperationException("Injected provider exception after Type-2 acceptance.");
        if (SubmitAcceptanceAmbiguous)
            return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Unavailable, "Acceptance response was lost after provider effect.");
        return PlatformAuthorityResult<CxlAcceleratorSubmission>.Ok(ReturnMalformedSubmission
            ? submission with { OperationBinding = submission.OperationBinding with { Generation = submission.OperationBinding.Generation + 1 } }
            : submission);
    }

    public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission) =>
        IsExact(submission)
            ? PlatformAuthorityResult<CxlAcceleratorCompletion>.Ok(new(submission,
                DeviceAvailable ? ExternalOperationCompletionDisposition.Completed : ExternalOperationCompletionDisposition.Faulted))
            : Fail<CxlAcceleratorCompletion>(PlatformAuthorityStatus.Stale, "Type-2 submission identity is stale.");

    public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement) =>
        IsExact(submission)
            ? PlatformAuthorityResult<CxlAcceleratorVisibility>.Ok(new(submission, requirement, DeviceAvailable && VisibilitySatisfied))
            : Fail<CxlAcceleratorVisibility>(PlatformAuthorityStatus.Stale, "Type-2 submission identity is stale.");

    public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission)
    {
        if (!IsExact(submission)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Submission is stale.");
        if (CancellationFails) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Model cancellation closure is ambiguous.");
        _live.Remove(submission.SubmissionId);
        return PlatformAuthorityResult.Ok();
    }
    public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission)
    {
        if (!IsExact(submission)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Submission is stale.");
        if (ReleaseFails) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Model release closure is ambiguous.");
        _live.Remove(submission.SubmissionId);
        return PlatformAuthorityResult.Ok();
    }
    private bool IsExact(CxlAcceleratorSubmission submission) => _live.TryGetValue(submission.SubmissionId, out var current) && current == submission;
    private static PlatformAuthorityResult<T> Fail<T>(PlatformAuthorityStatus status, string message) => PlatformAuthorityResult<T>.Fail(status, message);
}
