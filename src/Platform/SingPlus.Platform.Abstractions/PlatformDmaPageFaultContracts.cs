namespace SingPlus.Platform;

public enum PlatformProviderDmaPageFaultOutcome : byte
{
    Resolved = 1,
}

/// <summary>
/// Region-relative DMA page-fault request. It carries no address-space identifier,
/// raw address, mapping authority, or permission to widen the admitted grant.
/// </summary>
public readonly record struct PlatformProviderDmaPageFaultRequest(
    PlatformProviderDmaSubmission Submission,
    PlatformProviderDmaGrant Grant,
    PlatformDmaRange FaultRange,
    PlatformMemoryAccess RequestedAccess,
    ulong FaultSequence);

public readonly record struct PlatformProviderDmaPageFaultEvidence(
    PlatformProviderDmaSubmissionId SubmissionId,
    PlatformProviderDmaSubmissionGeneration SubmissionGeneration,
    PlatformProviderDmaGrantId GrantId,
    PlatformProviderLeaseGeneration GrantGeneration,
    PlatformProviderDmaVisibilityCycle PreparedCycle,
    PlatformDmaRange FaultRange,
    PlatformMemoryAccess RequestedAccess,
    ulong FaultSequence,
    PlatformProviderDmaPageFaultOutcome Outcome)
{
    public bool AuthorizesMapping => false;
    public bool AuthorizesMemoryAccess => false;
}

public static class PlatformDmaPageFaultContract
{
    public const uint ContractVersion = 6;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformProviderDmaPageFaultRequest request)
    {
        var grant = request.Grant;
        var submission = request.Submission;
        if (submission.SubmissionId.Value == 0 || submission.Generation.Value == 0 ||
            grant.GrantId.Value == 0 || grant.Generation.Value == 0 ||
            request.FaultSequence == 0 || request.FaultRange.Offset < 0 ||
            request.FaultRange.Length <= 0 ||
            request.FaultRange.Offset > long.MaxValue - request.FaultRange.Length)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "DMA page-fault identities, sequence, and bounded range must be materialized.");
        if (submission.GrantId != grant.GrantId || submission.GrantGeneration != grant.Generation ||
            submission.Range != grant.Range || submission.Direction != grant.Direction)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "DMA page fault does not match the exact submitted grant.");
        var grantEnd = grant.Range.Offset + grant.Range.Length;
        var faultEnd = request.FaultRange.Offset + request.FaultRange.Length;
        if (request.FaultRange.Offset < grant.Range.Offset || faultEnd > grantEnd)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "DMA page-fault range exceeds the exact admitted grant.");
        var allowed = grant.Direction switch
        {
            PlatformDmaDirection.DeviceReadsMemory => PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceWritesMemory => PlatformMemoryAccess.Write,
            PlatformDmaDirection.Bidirectional => PlatformMemoryAccess.Read | PlatformMemoryAccess.Write,
            _ => PlatformMemoryAccess.None,
        };
        if (request.RequestedAccess == PlatformMemoryAccess.None ||
            (request.RequestedAccess & ~allowed) != 0)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "DMA page-fault access exceeds the exact admitted direction.");
        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateEvidence(
        PlatformProviderDmaPageFaultRequest request,
        PlatformProviderDmaPageFaultEvidence evidence)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;
        return evidence.SubmissionId == request.Submission.SubmissionId &&
               evidence.SubmissionGeneration == request.Submission.Generation &&
               evidence.GrantId == request.Grant.GrantId &&
               evidence.GrantGeneration == request.Grant.Generation &&
               evidence.PreparedCycle == request.Submission.PreparedCycle &&
               evidence.FaultRange == request.FaultRange &&
               evidence.RequestedAccess == request.RequestedAccess &&
               evidence.FaultSequence == request.FaultSequence &&
               evidence.Outcome == PlatformProviderDmaPageFaultOutcome.Resolved
            ? PlatformAuthorityResult.Ok()
            : PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "DMA page-fault evidence does not match the exact request tuple.");
    }
}

public interface IPlatformDmaPageFaultProvider
{
    PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence> ResolveDmaPageFault(
        PlatformProviderDmaPageFaultRequest request);
}
