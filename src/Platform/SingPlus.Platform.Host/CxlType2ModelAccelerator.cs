using SingPlus.Contracts;

namespace SingPlus.Platform.Host;

/// <summary>Synchronous staged Type-2 model with no transport, ISA, or compiler dependency.</summary>
public sealed class CxlType2ModelAccelerator : ICxlType2ProtectedAcceleratorProvider
{
    private sealed class SubmissionRecord(CxlAcceleratorSubmission receipt)
    {
        public CxlAcceleratorSubmission Receipt { get; } = receipt;
        public bool Cancelled { get; set; }
        public bool Released { get; set; }
    }
    private readonly object _gate = new();
    private const int MaximumRetainedSubmissions = 4096;
    // One provider-owned lifetime record retains the exact closure fact for retries.
    // Instance-local model evidence, neither physical containment nor persistence.
    private readonly Dictionary<CxlAcceleratorSubmissionId, SubmissionRecord> _submissions = [];
    private ulong _nextId = 1;
    public int SubmitCount { get; private set; }
    public bool DeviceAvailable { get; set; } = true;
    public bool VisibilitySatisfied { get; set; } = true;
    public bool CancellationFails { get; set; }
    public bool ReleaseFails { get; set; }
    public bool SubmitAcceptanceAmbiguous { get; set; }
    public bool ReturnMalformedSubmission { get; set; }
    public bool SubmitThrowsAfterAcceptance { get; set; }
    public IReadOnlyList<ProtectedAcceleratorLabelSidebandV1> LastProtectedSideband { get; private set; } =
        Array.Empty<ProtectedAcceleratorLabelSidebandV1>();

    public PlatformAuthorityResult<CxlAcceleratorSubmission> SubmitProtected(
        CxlProtectedAcceleratorRequest protectedRequest)
    {
        ArgumentNullException.ThrowIfNull(protectedRequest);
        CxlAcceleratorRequest request = protectedRequest.Request;
        ProtectedAcceleratorLabelSidebandV1[] sideband = protectedRequest.LabelSideband?.ToArray() ?? [];
        try
        {
            foreach (ProtectedAcceleratorLabelSidebandV1? value in sideband) value.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Denied,
                $"Protected Type-2 label sideband is malformed: {exception.Message}");
        }
        string expectedOperationId = $"external-operation:{request.OperationBinding.Operation.OperationId.Value}";
        if (protectedRequest.ProviderGeneration == 0 || sideband.Length < 2 ||
            sideband.Any(value => value.OperationId != expectedOperationId ||
                value.ProviderGeneration != protectedRequest.ProviderGeneration ||
                value.OperationGeneration != request.OperationBinding.Operation.Generation.Value ||
                value.AuthorizesAccess || value.AuthorizesExecution))
            return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Denied,
                "Protected Type-2 labels are not bound to the submitted operation and provider generations.");
        LastProtectedSideband = Array.AsReadOnly(sideband);
        return Submit(request);
    }

    public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request)
    {
        if (!DeviceAvailable) return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.NotAccepted, "Type-2 device rejected submission before acceptance.");
        if (request.PublicationPath != ComputePublicationPath.Staged)
            return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Unsupported, "The model provider supports staged publication only.");
        if (request.OperationBinding.BindingId.Value == 0 || request.RegionUses.Count != 2 ||
            request.RegionUses[0].Mode != RegionUseMode.ReadOnly || request.RegionUses[1].Mode != RegionUseMode.StagedOutput)
            return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Denied, "Type-2 descriptor is malformed or has incompatible RegionUse modes.");
        lock (_gate)
        {
            if (_submissions.Count >= MaximumRetainedSubmissions)
                return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.NotAccepted, "Model receipt retention is full before acceptance.");
            if (_nextId == ulong.MaxValue)
                return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.NotAccepted, "Model submission identity is exhausted before acceptance.");
            CxlAcceleratorSubmission submission = new CxlAcceleratorSubmission(new(_nextId++), new(1), request.OperationBinding,
                request.EndpointId, request.DeviceGeneration, request.FabricBinding);
            _submissions.Add(submission.SubmissionId, new(submission));
            SubmitCount++;
            if (SubmitThrowsAfterAcceptance)
                throw new InvalidOperationException("Injected provider exception after Type-2 acceptance.");
            if (SubmitAcceptanceAmbiguous)
                return Fail<CxlAcceleratorSubmission>(PlatformAuthorityStatus.Unavailable, "Acceptance response was lost after provider effect.");
            return PlatformAuthorityResult<CxlAcceleratorSubmission>.Ok(ReturnMalformedSubmission
                ? submission with { OperationBinding = submission.OperationBinding with { Generation = submission.OperationBinding.Generation + 1 } }
                : submission);
        }
    }

    public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission)
    {
        lock (_gate)
            return TryExact(submission, out SubmissionRecord? record) && !record.Released
            ? PlatformAuthorityResult<CxlAcceleratorCompletion>.Ok(new(submission,
                record.Cancelled ? ExternalOperationCompletionDisposition.Cancelled :
                DeviceAvailable ? ExternalOperationCompletionDisposition.Completed : ExternalOperationCompletionDisposition.Faulted))
            : Fail<CxlAcceleratorCompletion>(PlatformAuthorityStatus.Stale, "Type-2 submission identity is stale.");
    }

    public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement)
    {
        lock (_gate)
            return TryExact(submission, out SubmissionRecord? record) && !record.Released
            ? PlatformAuthorityResult<CxlAcceleratorVisibility>.Ok(new(submission, requirement,
                DeviceAvailable && VisibilitySatisfied && !record.Cancelled))
            : Fail<CxlAcceleratorVisibility>(PlatformAuthorityStatus.Stale, "Type-2 submission identity is stale.");
    }

    public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission)
    {
        lock (_gate)
        {
            if (!TryExact(submission, out SubmissionRecord? record)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Submission is stale.");
            if (record.Released) return PlatformAuthorityResult.Ok();
            if (CancellationFails) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Model cancellation closure is ambiguous.");
            record.Cancelled = true;
            return PlatformAuthorityResult.Ok();
        }
    }
    public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission)
    {
        lock (_gate)
        {
            if (!TryExact(submission, out SubmissionRecord? record)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Submission is stale.");
            if (record.Released) return PlatformAuthorityResult.Ok();
            if (ReleaseFails) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Model release closure is ambiguous.");
            record.Released = true;
            return PlatformAuthorityResult.Ok();
        }
    }
    // Called only under the provider gate; identity alone cannot establish closure.
    private bool TryExact(CxlAcceleratorSubmission submission, out SubmissionRecord record) =>
        _submissions.TryGetValue(submission.SubmissionId, out record!) && record.Receipt == submission;
    private static PlatformAuthorityResult<T> Fail<T>(PlatformAuthorityStatus status, string message) => PlatformAuthorityResult<T>.Fail(status, message);
}
