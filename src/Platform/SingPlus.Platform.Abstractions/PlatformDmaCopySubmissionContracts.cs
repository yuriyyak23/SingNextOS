using SingPlus.Contracts;

namespace SingPlus.Platform;

public readonly record struct PlatformProviderDmaCopySubmissionId(ulong Value);
public readonly record struct PlatformProviderDmaCopySubmissionGeneration(ulong Value);

public readonly record struct PlatformProviderDmaCopySubmitRequest(
    PlatformProviderDmaSubmitRequest Source,
    PlatformProviderDmaSubmitRequest Destination,
    DmaExecutionBindingV1 SourceBinding,
    DmaExecutionBindingV1 DestinationBinding);

/// <summary>
/// Atomic provider acceptance for one exact read grant and one exact write grant.
/// The receipt is lifecycle evidence only and grants no memory or DMA authority.
/// </summary>
public readonly record struct PlatformProviderDmaCopySubmission(
    PlatformProviderDmaCopySubmissionId CopySubmissionId,
    PlatformProviderDmaCopySubmissionGeneration Generation,
    PlatformProviderDmaSubmission SourceSubmission,
    PlatformProviderDmaSubmission DestinationSubmission)
{
    public bool AuthorizesDma => false;
    public bool AuthorizesRegionAccess => false;
    public bool ProvesCompletion => false;
}

public static class PlatformDmaCopySubmissionContract
{
    public const uint ContractVersion = 5;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformProviderDmaCopySubmitRequest request)
    {
        var source = PlatformDmaSubmissionContract.ValidateRequest(request.Source);
        if (!source.IsSuccess) return source;
        var destination = PlatformDmaSubmissionContract.ValidateRequest(request.Destination);
        if (!destination.IsSuccess) return destination;
        if (request.Source.Grant.GrantId == request.Destination.Grant.GrantId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Atomic DMA copy requires distinct source and destination grants.");
        if (request.Source.Grant.Direction != PlatformDmaDirection.DeviceReadsMemory ||
            request.Destination.Grant.Direction != PlatformDmaDirection.DeviceWritesMemory ||
            request.Source.Grant.Range.Length != request.Destination.Grant.Range.Length)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Atomic DMA copy requires equal bounded read-source and write-destination ranges.");
        var sourceBinding = ValidateBinding(request.Source, request.SourceBinding);
        if (!sourceBinding.IsSuccess) return sourceBinding;
        return ValidateBinding(request.Destination, request.DestinationBinding);
    }

    public static PlatformAuthorityResult ValidateSubmission(
        PlatformProviderDmaCopySubmitRequest request,
        PlatformProviderDmaCopySubmission submission)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;
        if (submission.CopySubmissionId.Value == 0 || submission.Generation.Value == 0)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Atomic DMA copy submission identity must be materialized.");
        var source = PlatformDmaSubmissionContract.ValidateSubmission(
            request.Source, submission.SourceSubmission);
        if (!source.IsSuccess) return source;
        var destination = PlatformDmaSubmissionContract.ValidateSubmission(
            request.Destination, submission.DestinationSubmission);
        if (!destination.IsSuccess) return destination;
        if (submission.SourceSubmission.SubmissionId == submission.DestinationSubmission.SubmissionId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Atomic DMA copy legs require distinct provider submission identities.");
        return PlatformAuthorityResult.Ok();
    }

    private static PlatformAuthorityResult ValidateBinding(
        PlatformProviderDmaSubmitRequest request,
        DmaExecutionBindingV1 binding)
    {
        try { binding.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, exception.Message);
        }
        if (binding.EffectState != DmaEffectStateV1.Admitted || binding.AuthorizesDma ||
            binding.RegionGeneration != request.Grant.MappingLease.Region.Handle.Generation.Value ||
            binding.ProcessIncarnation != request.Grant.MappingLease.DomainLease.Subject.ProcessGeneration ||
            binding.AddressSpaceGeneration != request.Grant.MappingLease.DomainLease.Generation.Value ||
            binding.TranslationGeneration != request.Grant.MappingLease.Generation.Value ||
            binding.DeviceLeaseGeneration != request.Grant.DeviceLease.Generation.Value ||
            binding.SessionGeneration != request.Grant.Generation.Value)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "Atomic DMA copy binding does not match the exact provider grant generations.");
        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDmaCopySubmissionProvider
{
    /// <summary>
    /// Success means both legs were accepted atomically. NotAccepted means neither leg was
    /// accepted. Any other failure is ambiguous and requires both grants to remain pinned.
    /// </summary>
    PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(
        PlatformProviderDmaCopySubmitRequest request);
}
