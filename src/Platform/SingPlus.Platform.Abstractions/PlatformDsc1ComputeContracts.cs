namespace SingPlus.Platform;

public enum PlatformDsc1ElementType : byte
{
    UInt8 = 0,
}

public enum PlatformDsc1CommitSemantics : byte
{
    AllOrNone = 0,
}

/// <summary>
/// The only profile admitted by DSC1 contract v1. It is a semantic byte copy,
/// not a zero-copy promise, physical placement request, or scheduling hint.
/// </summary>
public readonly record struct PlatformDsc1CopyProfile(
    PlatformDsc1ElementType ElementType,
    PlatformDsc1CommitSemantics CommitSemantics)
{
    public static PlatformDsc1CopyProfile UInt8AllOrNone { get; } = new(
        PlatformDsc1ElementType.UInt8,
        PlatformDsc1CommitSemantics.AllOrNone);
}

public readonly record struct PlatformProviderDsc1RegionRange(
    PlatformProviderRegionMappingLease Mapping,
    long Offset,
    long Length);

public readonly record struct PlatformDsc1CopyRequest(
    PlatformProviderDomainLease DomainLease,
    PlatformProviderDsc1RegionRange Source,
    PlatformProviderDsc1RegionRange Destination,
    PlatformDsc1CopyProfile Profile);

public readonly record struct PlatformProviderDsc1Submission(
    PlatformOperationIdentity Operation,
    PlatformDsc1CopyRequest Request);

public enum PlatformDsc1CompletionDisposition : byte
{
    Pending = 0,
    Completed,
    Cancelled,
    Faulted,
}

public readonly record struct PlatformProviderDsc1Completion(
    PlatformProviderDsc1Submission Submission,
    PlatformCompletionReceipt Receipt,
    PlatformDsc1CompletionDisposition Disposition);

/// <summary>
/// A bounded, disjoint, byte-oriented DSC1 Copy contract. Version 1 deliberately
/// excludes arithmetic, reductions, queues, scatter/gather, overlap and any raw
/// execution-topology or opcode vocabulary. A provider advertised as ModelOnly
/// admits and closes typed lifecycle state only: it must not read, write, retain,
/// or otherwise act on region contents. Executable providers require an external
/// custody/visibility contract and are not admitted by <see cref="SupportsModelOnly"/>.
/// </summary>
public static class PlatformDsc1ComputeContract
{
    public const uint ContractVersion = 1;
    public const long MaximumByteLength = 1024 * 1024;

    public static PlatformAuthorityResult ValidateProfile(
        PlatformDsc1CopyProfile profile)
    {
        if (!Enum.IsDefined(profile.ElementType) ||
            !Enum.IsDefined(profile.CommitSemantics))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "The DSC1 Copy profile contains an undefined element or commit semantic.");
        }

        if (profile != PlatformDsc1CopyProfile.UInt8AllOrNone)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "DSC1 contract v1 supports only UInt8 all-or-none Copy.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateRequest(
        PlatformDsc1CopyRequest request)
    {
        var subjectValidation = PlatformDomainContract.ValidateSubject(
            request.DomainLease.Subject);
        if (!subjectValidation.IsSuccess) return subjectValidation;

        if (request.DomainLease.LeaseId.Value == 0 ||
            request.DomainLease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DSC1 Copy requires a materialized provider domain lease.");
        }

        var profileValidation = ValidateProfile(request.Profile);
        if (!profileValidation.IsSuccess) return profileValidation;

        var sourceValidation = ValidateRange(
            request.DomainLease,
            request.Source,
            PlatformMemoryAccess.Read,
            "source");
        if (!sourceValidation.IsSuccess) return sourceValidation;

        var destinationValidation = ValidateRange(
            request.DomainLease,
            request.Destination,
            PlatformMemoryAccess.Write,
            "destination");
        if (!destinationValidation.IsSuccess) return destinationValidation;

        if (request.Source.Length != request.Destination.Length)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DSC1 Copy source and destination ranges must have equal byte lengths.");
        }

        if (request.Source.Mapping.MappingId == request.Destination.Mapping.MappingId ||
            request.Source.Mapping.Region.Handle.RegionId ==
                request.Destination.Mapping.Region.Handle.RegionId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DSC1 Copy v1 requires disjoint source and destination owned regions.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateSubmission(
        PlatformDsc1CopyRequest expectedRequest,
        PlatformProviderDsc1Submission submission)
    {
        var requestValidation = ValidateRequest(expectedRequest);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (submission.Operation.OperationId.Value == 0 ||
            submission.Operation.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider DSC1 submission identity must be materialized.");
        }

        var operationLease = ValidateExactDomainLease(
            expectedRequest.DomainLease,
            submission.Operation.DomainLease,
            "submission operation");
        if (!operationLease.IsSuccess) return operationLease;

        if (submission.Request != expectedRequest)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider DSC1 submission does not commit the exact bounded Copy request.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateCompletion(
        PlatformProviderDsc1Submission expectedSubmission,
        PlatformProviderDsc1Completion completion)
    {
        var submissionValidation = ValidateSubmission(
            expectedSubmission.Request,
            expectedSubmission);
        if (!submissionValidation.IsSuccess) return submissionValidation;

        if (completion.Submission != expectedSubmission)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The DSC1 completion belongs to a different bounded submission.");
        }

        var receiptValidation = PlatformCompletionContract.ValidateReceiptIdentity(
            expectedSubmission.Operation,
            completion.Receipt);
        if (!receiptValidation.IsSuccess) return receiptValidation;

        if (!Enum.IsDefined(completion.Disposition))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The DSC1 completion contains an undefined disposition.");
        }

        var stateMatches = completion.Disposition switch
        {
            PlatformDsc1CompletionDisposition.Pending =>
                PlatformCompletionContract.IsNonTerminal(completion.Receipt.State),
            PlatformDsc1CompletionDisposition.Completed =>
                completion.Receipt.ProvesClosure,
            PlatformDsc1CompletionDisposition.Cancelled =>
                completion.Receipt.ProvesClosure,
            PlatformDsc1CompletionDisposition.Faulted =>
                completion.Receipt.State == PlatformCompletionState.Faulted,
            _ => false,
        };

        return stateMatches
            ? PlatformAuthorityResult.Ok()
            : PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The DSC1 completion disposition does not match its generic completion state.");
    }

    public static bool SupportsModelOnly(PlatformFeatureDescriptor feature) =>
        feature.Family == PlatformFeatureFamily.Dsc1BulkCompute &&
        feature.ContractVersion >= ContractVersion &&
        feature.Availability == PlatformFeatureAvailability.ModelOnly;

    private static PlatformAuthorityResult ValidateRange(
        PlatformProviderDomainLease expectedDomain,
        PlatformProviderDsc1RegionRange range,
        PlatformMemoryAccess requiredAccess,
        string role)
    {
        if (range.Mapping.MappingId.Value == 0 ||
            range.Mapping.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                $"The DSC1 {role} mapping identity must be materialized.");
        }

        var leaseValidation = ValidateExactDomainLease(
            expectedDomain,
            range.Mapping.DomainLease,
            $"{role} mapping");
        if (!leaseValidation.IsSuccess) return leaseValidation;

        var region = range.Mapping.Region;
        if (region.Handle.RegionId.Value == 0 ||
            region.Handle.Generation.Value == 0 ||
            region.Owner.ProcessGeneration == 0 ||
            region.ByteLength <= 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                $"The DSC1 {role} range requires a materialized owned-region identity.");
        }

        if (region.Owner.DomainId != expectedDomain.Subject.DomainId ||
            region.Owner.ProcessGeneration != expectedDomain.Subject.ProcessGeneration)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                $"The DSC1 {role} region owner does not match the provider domain subject.");
        }

        if (range.Mapping.Access == PlatformMemoryAccess.None ||
            (range.Mapping.Access &
                ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) != 0 ||
            (range.Mapping.Access & requiredAccess) != requiredAccess)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                $"The DSC1 {role} mapping lacks the required memory access.");
        }

        if (range.Offset < 0 ||
            range.Length <= 0 ||
            range.Length > MaximumByteLength ||
            range.Offset > region.ByteLength - range.Length)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                $"The DSC1 {role} range must be positive, bounded, non-overflowing and inside the mapped region.");
        }

        return PlatformAuthorityResult.Ok();
    }

    private static PlatformAuthorityResult ValidateExactDomainLease(
        PlatformProviderDomainLease expected,
        PlatformProviderDomainLease actual,
        string role)
    {
        if (actual.LeaseId != expected.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                $"The DSC1 {role} belongs to a different provider domain lease.");
        }

        if (actual.Generation != expected.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                $"The DSC1 {role} uses a stale provider domain generation.");
        }

        if (actual.Subject != expected.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                $"The DSC1 {role} belongs to a different local subject.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDsc1ComputeProvider
{
    /// <summary>
    /// Admits one exact semantic request. When the feature manifest classifies the
    /// provider as ModelOnly, this call describes lifecycle only and has no external
    /// memory effect; the SingNextOS runtime owns the bounded reference copy.
    /// </summary>
    PlatformAuthorityResult<PlatformProviderDsc1Submission> SubmitDsc1Copy(
        PlatformDsc1CopyRequest request);

    PlatformAuthorityResult<PlatformProviderDsc1Completion> ObserveDsc1Completion(
        PlatformProviderDsc1Submission submission);

    PlatformAuthorityResult<PlatformProviderDsc1Completion> CancelDsc1(
        PlatformProviderDsc1Submission submission);
}
