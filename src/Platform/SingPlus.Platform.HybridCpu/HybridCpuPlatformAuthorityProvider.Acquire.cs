using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformRegionAcquireProvider
{
    public PlatformAuthorityResult<PlatformRegionAcquireResult> AcquireRegionMappingFromConsumer(
        PlatformRegionAcquireRequest request)
    {
        var requestValidation = PlatformRegionAcquireContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                requestValidation.Status,
                requestValidation.Message ?? "The region-acquire request is invalid.");
        }

        var mappingValidation = ValidateProviderMappingForAcquire(
            request.Mapping,
            request.Slice);
        if (!mappingValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                mappingValidation.Status,
                mappingValidation.Message ?? "The provider mapping cannot produce acquire evidence.");
        }

        if (request.Producer != PlatformMemoryConsumerClass.ExternalExecutionDomain)
        {
            return PlatformAuthorityResult<PlatformRegionAcquireResult>.Ok(
                AcquireResult(request, PlatformMemoryAcquireOutcome.Unsupported));
        }

        var record = _providerMappings[request.Mapping.MappingId];
        var external = _runtime.AcquireOwnedRegionVisibility(
            record.HybridCpuLease,
            ToNeutralAcquireRequirement(request.Requirement));

        switch (external.Decision)
        {
            case NeutralOwnedRegionAcquireDecision.Satisfied:
                if (external.Lease != record.HybridCpuLease)
                {
                    return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                        PlatformAuthorityStatus.Faulted,
                        "HybridCPU returned acquire evidence for a different mapping lease.");
                }

                return PlatformAuthorityResult<PlatformRegionAcquireResult>.Ok(
                    AcquireResult(request, FromNeutralAcquireOutcome(external.Outcome)));

            case NeutralOwnedRegionAcquireDecision.Unsupported:
                return PlatformAuthorityResult<PlatformRegionAcquireResult>.Ok(
                    AcquireResult(request, PlatformMemoryAcquireOutcome.Unsupported));

            case NeutralOwnedRegionAcquireDecision.NotClosed:
                return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                    PlatformAuthorityStatus.Denied,
                    external.Reason);

            case NeutralOwnedRegionAcquireDecision.RevokedDomain:
                return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                    PlatformAuthorityStatus.Revoked,
                    external.Reason);

            case NeutralOwnedRegionAcquireDecision.Stale:
            case NeutralOwnedRegionAcquireDecision.NotFound:
            case NeutralOwnedRegionAcquireDecision.Faulted:
            default:
                return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    private PlatformAuthorityResult ValidateProviderMappingForAcquire(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionSlice expectedSlice)
    {
        if (!_providerMappings.TryGetValue(mapping.MappingId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping does not exist.");
        }

        if (record.Mapping.Lease.Generation != mapping.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider mapping generation is stale.");
        }

        if (record.Mapping.Lease.DomainLease.LeaseId != mapping.DomainLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider mapping belongs to a different domain lease.");
        }

        if (record.Mapping.Lease.DomainLease.Generation != mapping.DomainLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider mapping domain generation is stale.");
        }

        if (record.Mapping.Lease.DomainLease.Subject != mapping.DomainLease.Subject ||
            record.Mapping.Lease.Region.Owner != mapping.Region.Owner)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider mapping belongs to a different owner subject.");
        }

        if (record.Mapping.Lease.Region.Handle.RegionId != mapping.Region.Handle.RegionId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping refers to a different region.");
        }

        if (record.Mapping.Lease.Region.Handle.Generation != mapping.Region.Handle.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider mapping region generation is stale.");
        }

        if (record.Mapping.Lease.Region.ByteLength != mapping.Region.ByteLength ||
            record.Mapping.Lease.Access != mapping.Access ||
            record.Mapping.Slice != expectedSlice)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider acquire request does not match the exact materialized mapping.");
        }

        if (!record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping must reach exact closure before acquire evidence is requested.");
        }

        return ValidateDomain(mapping.DomainLease);
    }

    private static PlatformRegionAcquireResult AcquireResult(
        PlatformRegionAcquireRequest request,
        PlatformMemoryAcquireOutcome outcome) =>
        new(
            request.Mapping.MappingId,
            request.Mapping.Generation,
            request.Slice,
            request.Producer,
            request.Requirement,
            outcome);

    private static NeutralMemoryAcquireRequirement ToNeutralAcquireRequirement(
        PlatformMemoryAcquireRequirement requirement) =>
        requirement switch
        {
            PlatformMemoryAcquireRequirement.AcquisitionFence =>
                NeutralMemoryAcquireRequirement.AcquisitionFence,
            _ => throw new ArgumentOutOfRangeException(nameof(requirement)),
        };

    private static PlatformMemoryAcquireOutcome FromNeutralAcquireOutcome(
        NeutralMemoryAcquireOutcome outcome) =>
        outcome switch
        {
            NeutralMemoryAcquireOutcome.AcquisitionFenceSatisfied =>
                PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied,
            NeutralMemoryAcquireOutcome.Unsupported =>
                PlatformMemoryAcquireOutcome.Unsupported,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
