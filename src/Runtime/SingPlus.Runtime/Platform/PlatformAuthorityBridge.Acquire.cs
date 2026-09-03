using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformRegionAcquireEvidence(
    PlatformOwnedRegionSliceMapping Mapping,
    PlatformMemoryConsumerClass Producer,
    PlatformMemoryAcquireRequirement Requirement,
    PlatformMemoryAcquireOutcome Outcome)
{
    public bool IsSatisfied =>
        Requirement == PlatformMemoryAcquireRequirement.AcquisitionFence &&
        Outcome == PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied;
}

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformRegionAcquireEvidence> AcquireClosedRegionMappingFromConsumer(
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformMemoryConsumerClass producer,
        PlatformMemoryAcquireRequirement requirement)
    {
        var identityValidation = ValidateClosedExactMappingIdentity(mapping, expectedSubject);
        if (!identityValidation.IsSuccess)
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                identityValidation.Error,
                identityValidation.Message!);
        }

        var record = _mappings[mapping.Mapping.MappingId];
        if (record.ClosureState != PlatformExternalClosureState.Closed)
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                record.ClosureState == PlatformExternalClosureState.Faulted
                    ? KernelError.PlatformFaulted
                    : KernelError.PlatformBindingDraining,
                "The exact mapping must reach verified Closed before acquire evidence is requested.");
        }

        if (record.LocalReservationReleased)
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                KernelError.PlatformBindingRevoked,
                "The local reservation has already been released for this mapping.");
        }

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.ExplicitMemoryVisibility,
                PlatformRegionAcquireContract.ContractVersion,
                PlatformFeatureAvailability.Executable))
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise executable mapping acquire v3 semantics.");
        }

        if (_provider is not IPlatformRegionAcquireProvider acquireProvider)
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose post-close mapping acquire semantics.");
        }

        var slice = _exactMappingSlices[mapping.Mapping.MappingId];
        var request = new PlatformRegionAcquireRequest(
            record.ProviderLease,
            slice,
            producer,
            requirement);
        var requestValidation = PlatformRegionAcquireContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                KernelError.PlatformFaulted,
                requestValidation.Message ?? "The post-close mapping acquire request is malformed.");
        }

        var providerResult = acquireProvider.AcquireRegionMappingFromConsumer(request);
        if (!providerResult.IsSuccess)
        {
            return FromProviderFailure<PlatformRegionAcquireEvidence>(
                providerResult.Status,
                providerResult.Message);
        }

        var result = providerResult.Value!;
        var resultValidation = PlatformRegionAcquireContract.ValidateResult(request, result);
        if (!resultValidation.IsSuccess)
        {
            return FromProviderFailure<PlatformRegionAcquireEvidence>(
                resultValidation.Status,
                resultValidation.Message);
        }

        if (!result.IsSatisfied)
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider cannot satisfy the exact post-close acquire requirement.");
        }

        return KernelResult<PlatformRegionAcquireEvidence>.Ok(
            new PlatformRegionAcquireEvidence(
                mapping,
                result.Producer,
                result.Requirement,
                result.Outcome));
    }

    private KernelResult ValidateClosedExactMappingIdentity(
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        var baseValidation = ValidateMappingIdentity(mapping.Mapping, expectedSubject);
        if (!baseValidation.IsSuccess) return baseValidation;

        if (!_exactMappingSlices.TryGetValue(
                mapping.Mapping.MappingId,
                out var expectedSlice))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The mapping does not carry an exact owned-region slice contract.");
        }

        if (expectedSlice.Region.Handle.RegionId != mapping.Region.RegionId)
        {
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The exact mapping refers to a different local region.");
        }

        if (expectedSlice.Region.Handle.Generation != mapping.Region.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The exact mapping region generation is stale.");
        }

        if (expectedSlice.Access != mapping.Access ||
            expectedSlice.Offset != mapping.Offset ||
            expectedSlice.Length != mapping.Length)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "The exact mapping range or access does not match the closure being acquired.");
        }

        return KernelResult.Ok();
    }
}
