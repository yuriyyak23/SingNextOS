using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformOwnedRegionMappingProvider,
    IPlatformRegionVisibilityProvider,
    IPlatformRegionRevocationProvider
{
    private sealed class ProviderMappingRecord(
        PlatformProviderOwnedRegionMapping mapping,
        NeutralOwnedRegionMappingLease hybridCpuLease)
    {
        public PlatformProviderOwnedRegionMapping Mapping { get; } = mapping;
        public NeutralOwnedRegionMappingLease HybridCpuLease { get; } = hybridCpuLease;
        public bool Revoked { get; set; }
    }

    private sealed class ProviderOperationRecord(
        PlatformOperationIdentity identity,
        PlatformCompletionReceipt receipt)
    {
        public PlatformOperationIdentity Identity { get; } = identity;
        public PlatformCompletionReceipt Receipt { get; } = receipt;
    }

    private readonly Dictionary<PlatformProviderRegionMappingId, ProviderMappingRecord>
        _providerMappings = [];
    private readonly Dictionary<PlatformOperationId, ProviderOperationRecord>
        _providerOperations = [];
    private ulong _nextProviderMappingId = 1;
    private ulong _nextProviderOperationId = 1;

    public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
        PlatformProviderDomainLease domainLease,
        PlatformRegionSlice slice)
    {
        var domainValidation = ValidateDomain(domainLease);
        if (!domainValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                domainValidation.Status,
                domainValidation.Message ?? "The provider domain lease is not live.");
        }

        var sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
        if (!sliceValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                sliceValidation.Status,
                sliceValidation.Message ?? "The exact region slice is invalid.");
        }

        var expectedOwner = new SingPlus.Contracts.RegionOwner(
            domainLease.Subject.DomainId,
            domainLease.Subject.ProcessGeneration);
        if (slice.Region.Owner != expectedOwner)
        {
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The exact region slice is owned by a different Sing subject.");
        }

        var domainRecord = _domains[domainLease.LeaseId];
        var neutralSlice = new NeutralOwnedRegionSlice(
            slice.Offset,
            slice.Length,
            ToNeutralAccess(slice.Access));
        var external = _runtime.MapOwnedRegion(
            domainRecord.HybridCpuLease,
            neutralSlice);
        if (!external.IsMapped)
        {
            var status = external.Decision switch
            {
                NeutralOwnedRegionMapDecision.InvalidRange => PlatformAuthorityStatus.Denied,
                NeutralOwnedRegionMapDecision.InvalidAccess => PlatformAuthorityStatus.Denied,
                NeutralOwnedRegionMapDecision.Revoked => PlatformAuthorityStatus.Revoked,
                NeutralOwnedRegionMapDecision.Stale => PlatformAuthorityStatus.Faulted,
                NeutralOwnedRegionMapDecision.NotFound => PlatformAuthorityStatus.Faulted,
                _ => PlatformAuthorityStatus.Faulted,
            };
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                status,
                external.Reason);
        }

        if (external.Lease.DomainLease != domainRecord.HybridCpuLease ||
            external.Lease.Slice != neutralSlice ||
            external.Lease.Coherence != NeutralMemoryCoherenceModel.NonCoherent)
        {
            _ = _runtime.CloseOwnedRegionMapping(external.Lease);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned mapping evidence that does not match the exact provider request.");
        }

        var lease = new PlatformProviderRegionMappingLease(
            new PlatformProviderRegionMappingId(NextNonZero(ref _nextProviderMappingId)),
            new PlatformProviderLeaseGeneration(1),
            domainRecord.Lease,
            slice.Region,
            slice.Access);
        var mapping = new PlatformProviderOwnedRegionMapping(lease, slice);
        _providerMappings.Add(
            lease.MappingId,
            new ProviderMappingRecord(mapping, external.Lease));
        return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapping);
    }

    public PlatformAuthorityResult<PlatformRegionVisibilityResult> PrepareRegionMappingForConsumer(
        PlatformRegionVisibilityRequest request)
    {
        var requestValidation = PlatformRegionVisibilityContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                requestValidation.Status,
                requestValidation.Message ?? "The region-visibility request is invalid.");
        }

        var mappingValidation = ValidateProviderMapping(request.Mapping, request.Slice);
        if (!mappingValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                mappingValidation.Status,
                mappingValidation.Message ?? "The provider mapping is not live.");
        }

        var record = _providerMappings[request.Mapping.MappingId];
        if (request.Consumer != PlatformMemoryConsumerClass.ExternalExecutionDomain)
        {
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                VisibilityResult(request, PlatformMemoryVisibilityOutcome.Unsupported));
        }

        var external = _runtime.PrepareOwnedRegionVisibility(
            record.HybridCpuLease,
            ToNeutralVisibilityRequirement(request.Requirement));
        switch (external.Decision)
        {
            case NeutralOwnedRegionVisibilityDecision.Satisfied:
                if (external.Lease != record.HybridCpuLease)
                {
                    return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                        PlatformAuthorityStatus.Faulted,
                        "HybridCPU returned visibility evidence for a different mapping lease.");
                }

                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                    VisibilityResult(request, FromNeutralVisibilityOutcome(external.Outcome)));

            case NeutralOwnedRegionVisibilityDecision.Unsupported:
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                    VisibilityResult(request, PlatformMemoryVisibilityOutcome.Unsupported));

            case NeutralOwnedRegionVisibilityDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                    PlatformAuthorityStatus.Revoked,
                    external.Reason);

            case NeutralOwnedRegionVisibilityDecision.Stale:
            case NeutralOwnedRegionVisibilityDecision.NotFound:
            case NeutralOwnedRegionVisibilityDecision.Faulted:
            default:
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy)
    {
        if (!Enum.IsDefined(policy) || policy != PlatformRegionRevocationPolicy.DrainBeforeRevoke)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Only drain-before-revoke is supported for neutral owned-region mappings.");
        }

        var validation = ValidateProviderMapping(mapping, expectedSlice: null);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                validation.Status,
                validation.Message ?? "The provider mapping is not live.");
        }

        var close = CloseProviderRegionMapping(mapping);
        if (!close.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                close.Status,
                close.Message ?? "The provider mapping could not be closed.");
        }

        var operation = new PlatformOperationIdentity(
            new PlatformOperationId(NextNonZero(ref _nextProviderOperationId)),
            new PlatformOperationGeneration(1),
            mapping.DomainLease);
        var receipt = new PlatformCompletionReceipt(
            operation.OperationId,
            operation.Generation,
            operation.DomainLease,
            PlatformCompletionState.Closed);
        _providerOperations.Add(
            operation.OperationId,
            new ProviderOperationRecord(operation, receipt));

        return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
            new PlatformRegionRevocationTicket(
                mapping.MappingId,
                mapping.Generation,
                operation));
    }

    public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
        PlatformOperationIdentity operation)
    {
        if (!_providerOperations.TryGetValue(operation.OperationId, out var record))
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider operation does not exist.");
        }

        if (record.Identity.Generation != operation.Generation)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider operation generation is stale.");
        }

        if (record.Identity.DomainLease.LeaseId != operation.DomainLease.LeaseId)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider operation belongs to a different domain lease.");
        }

        if (record.Identity.DomainLease.Generation != operation.DomainLease.Generation)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider operation domain generation is stale.");
        }

        if (record.Identity.DomainLease.Subject != operation.DomainLease.Subject)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider operation belongs to a different Sing subject.");
        }

        return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(record.Receipt);
    }

    private bool HasActiveProviderMappings(PlatformProviderDomainLease domainLease) =>
        _providerMappings.Values.Any(record =>
            !record.Revoked && record.Mapping.Lease.DomainLease == domainLease);

    private PlatformAuthorityResult CloseProviderRegionMapping(
        PlatformProviderRegionMappingLease mapping)
    {
        var validation = ValidateProviderMapping(mapping, expectedSlice: null);
        if (!validation.IsSuccess) return validation;

        if (HasActiveProviderDmaGrants(mapping))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU DMA grants must close before the provider region mapping.");
        }

        var record = _providerMappings[mapping.MappingId];
        var external = _runtime.CloseOwnedRegionMapping(record.HybridCpuLease);
        switch (external.Decision)
        {
            case NeutralOwnedRegionCloseDecision.Closed:
            case NeutralOwnedRegionCloseDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();
            case NeutralOwnedRegionCloseDecision.ActiveDependents:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    external.Reason);
            case NeutralOwnedRegionCloseDecision.Stale:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU rejected the provider-owned mapping lease as stale; closure is not proven.");
            case NeutralOwnedRegionCloseDecision.NotFound:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU no longer recognizes the provider-owned mapping lease; closure is not proven.");
            default:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    private PlatformAuthorityResult ValidateProviderMapping(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionSlice? expectedSlice)
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
            record.Mapping.Lease.Access != mapping.Access)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping range authority or access does not match the materialized mapping.");
        }

        if (expectedSlice is { } slice && record.Mapping.Slice != slice)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping does not match the exact region slice.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider mapping has already been revoked.");
        }

        return PlatformAuthorityResult.Ok();
    }

    private static PlatformRegionVisibilityResult VisibilityResult(
        PlatformRegionVisibilityRequest request,
        PlatformMemoryVisibilityOutcome outcome) =>
        new(
            request.Mapping.MappingId,
            request.Mapping.Generation,
            request.Slice,
            request.Consumer,
            request.Requirement,
            outcome);

    private static NeutralMemoryAccess ToNeutralAccess(PlatformMemoryAccess access) =>
        access switch
        {
            PlatformMemoryAccess.Read => NeutralMemoryAccess.Read,
            PlatformMemoryAccess.Write => NeutralMemoryAccess.Write,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write =>
                NeutralMemoryAccess.Read | NeutralMemoryAccess.Write,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };

    private static NeutralMemoryVisibilityRequirement ToNeutralVisibilityRequirement(
        PlatformMemoryVisibilityRequirement requirement) =>
        requirement switch
        {
            PlatformMemoryVisibilityRequirement.CoherentAccess =>
                NeutralMemoryVisibilityRequirement.CoherentAccess,
            PlatformMemoryVisibilityRequirement.PublicationFence =>
                NeutralMemoryVisibilityRequirement.PublicationFence,
            PlatformMemoryVisibilityRequirement.CacheMaintenance =>
                NeutralMemoryVisibilityRequirement.CacheMaintenance,
            _ => throw new ArgumentOutOfRangeException(nameof(requirement)),
        };

    private static PlatformMemoryVisibilityOutcome FromNeutralVisibilityOutcome(
        NeutralMemoryVisibilityOutcome outcome) =>
        outcome switch
        {
            NeutralMemoryVisibilityOutcome.Coherent => PlatformMemoryVisibilityOutcome.Coherent,
            NeutralMemoryVisibilityOutcome.PublicationFenceSatisfied =>
                PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            NeutralMemoryVisibilityOutcome.CacheMaintenanceSatisfied =>
                PlatformMemoryVisibilityOutcome.CacheMaintenanceSatisfied,
            NeutralMemoryVisibilityOutcome.Unsupported => PlatformMemoryVisibilityOutcome.Unsupported,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
