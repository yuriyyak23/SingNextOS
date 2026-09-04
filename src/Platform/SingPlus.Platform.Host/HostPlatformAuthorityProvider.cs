using SingPlus.Platform;

namespace SingPlus.Platform.Host;

public sealed partial class HostPlatformAuthorityProvider :
    IPlatformAuthorityProvider,
    IPlatformFeatureProvider,
    IPlatformCompletionProvider,
    IPlatformMemoryVisibilityProvider,
    IPlatformRegionRevocationProvider,
    IPlatformExecutionPolicyProvider,
    IPlatformDsc1ComputeProvider
{
    private sealed class DomainRecord(PlatformProviderDomainLease lease)
    {
        public PlatformProviderDomainLease Lease { get; } = lease;
        public PlatformExecutionPolicy? ExecutionPolicy { get; set; }
        public bool Revoked { get; set; }
    }

    private sealed class MappingRecord(PlatformProviderRegionMappingLease lease)
    {
        public PlatformProviderRegionMappingLease Lease { get; } = lease;
        public bool Revoked { get; set; }
        public PlatformOperationIdentity? RevocationOperation { get; set; }
    }

    private sealed class OperationRecord(PlatformOperationIdentity operation)
    {
        public PlatformOperationIdentity Operation { get; } = operation;
        public PlatformCompletionState State { get; set; } = PlatformCompletionState.Staged;

        public PlatformCompletionReceipt Receipt => new(
            Operation.OperationId,
            Operation.Generation,
            Operation.DomainLease,
            State);
    }

    private readonly Dictionary<PlatformProviderDomainLeaseId, DomainRecord> _domains = [];
    private readonly Dictionary<PlatformProviderRegionMappingId, MappingRecord> _mappings = [];
    private readonly Dictionary<PlatformOperationId, OperationRecord> _operations = [];
    private readonly Dictionary<PlatformDomainIdentity, PlatformProviderDomainLeaseId> _activeSubjects = [];
    private readonly Dictionary<PlatformRegionIdentity, PlatformProviderRegionMappingId> _activeRegions = [];
    private readonly PlatformAuthorityStatus? _regionRevocationFailure;
    private readonly bool _deferRegionRevocationCompletion;
    private readonly PlatformFeatureManifest _featureManifest;
    private ulong _nextDomainId = 1;
    private ulong _nextMappingId = 1;
    private ulong _nextOperationId = 1;

    public HostPlatformAuthorityProvider(
        PlatformAuthorityFeatures features =
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping,
        PlatformAuthorityStatus? regionRevocationFailure = null,
        IEnumerable<PlatformFeatureDescriptor>? additionalFeatures = null,
        bool deferRegionRevocationCompletion = false,
        bool deferDsc1Completion = false)
    {
        if (regionRevocationFailure == PlatformAuthorityStatus.Success)
            throw new ArgumentOutOfRangeException(nameof(regionRevocationFailure));

        _regionRevocationFailure = regionRevocationFailure;
        _deferRegionRevocationCompletion = deferRegionRevocationCompletion;
        _deferDsc1Completion = deferDsc1Completion;
        Descriptor = new PlatformProviderDescriptor(new PlatformProviderId("host-test"), 2, features);

        var featureDescriptors = PlatformFeatureManifest.FromLegacy(features).Features
            .Where(static feature => feature.Family != PlatformFeatureFamily.NeutralDomains)
            .ToList();
        if ((features & PlatformAuthorityFeatures.NeutralDomainBinding) != 0)
        {
            featureDescriptors.Add(new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission));
            featureDescriptors.Add(new PlatformFeatureDescriptor(
                PlatformFeatureFamily.ExecutionPolicy,
                PlatformExecutionPolicyContract.ContractVersion,
                PlatformFeatureAvailability.ModelOnly));
        }

        featureDescriptors.Add(new PlatformFeatureDescriptor(
            PlatformFeatureFamily.ExplicitMemoryVisibility,
            1,
            PlatformFeatureAvailability.ModelOnly));

        if ((features & (PlatformAuthorityFeatures.NeutralDomainBinding |
                         PlatformAuthorityFeatures.DirectOwnedRegionMapping)) ==
            (PlatformAuthorityFeatures.NeutralDomainBinding |
             PlatformAuthorityFeatures.DirectOwnedRegionMapping))
        {
            featureDescriptors.Add(new PlatformFeatureDescriptor(
                PlatformFeatureFamily.Dsc1BulkCompute,
                PlatformDsc1ComputeContract.ContractVersion,
                PlatformFeatureAvailability.ModelOnly));
        }

        _featureManifest = additionalFeatures is null
            ? new PlatformFeatureManifest(featureDescriptors)
            : new PlatformFeatureManifest(featureDescriptors.Concat(additionalFeatures));
    }

    public PlatformProviderDescriptor Descriptor { get; }

    public int BindDomainCallCount { get; private set; }
    public int RevokeDomainCallCount { get; private set; }
    public int MapOwnedRegionCallCount { get; private set; }
    public int RevokeRegionMappingCallCount { get; private set; }
    public PlatformRegionRevocationPolicy? LastRegionRevocationPolicy { get; private set; }
    public PlatformOperationIdentity? LastRegionRevocationOperation { get; private set; }

    public PlatformFeatureManifest QueryFeatures() => _featureManifest;

    public PlatformAuthorityResult<PlatformMemoryVisibilityResult> EnsureMemoryVisibility(
        PlatformMemoryVisibilityRequest request)
    {
        var requestValidation = PlatformMemoryVisibilityContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformMemoryVisibilityResult>.Fail(
                requestValidation.Status,
                requestValidation.Message!);
        }

        var operationValidation = ValidateOperation(request.Operation);
        if (!operationValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformMemoryVisibilityResult>.Fail(
                operationValidation.Status,
                operationValidation.Message!);
        }

        var operationState = _operations[request.Operation.OperationId].State;
        if (PlatformCompletionContract.IsTerminal(operationState))
        {
            return PlatformAuthorityResult<PlatformMemoryVisibilityResult>.Fail(
                PlatformAuthorityStatus.Denied,
                "Terminal platform operations cannot produce new memory-visibility evidence.");
        }

        var outcome = ModelMemoryVisibility(request.Consumer, request.Requirement);
        return PlatformAuthorityResult<PlatformMemoryVisibilityResult>.Ok(
            new PlatformMemoryVisibilityResult(
                request.Consumer,
                request.Requirement,
                outcome));
    }

    public PlatformAuthorityResult<PlatformOperationIdentity> StageOperation(
        PlatformProviderDomainLease domainLease)
    {
        var domainValidation = ValidateDomain(domainLease);
        if (!domainValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformOperationIdentity>.Fail(
                domainValidation.Status,
                domainValidation.Message!);

        var operation = new PlatformOperationIdentity(
            new PlatformOperationId(_nextOperationId++),
            new PlatformOperationGeneration(1),
            domainLease);

        _operations.Add(operation.OperationId, new OperationRecord(operation));
        return PlatformAuthorityResult<PlatformOperationIdentity>.Ok(operation);
    }

    public PlatformAuthorityResult<PlatformCompletionReceipt> AdvanceOperation(
        PlatformOperationIdentity operation,
        PlatformCompletionState nextState)
    {
        var operationValidation = ValidateOperation(operation);
        if (!operationValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                operationValidation.Status,
                operationValidation.Message!);

        if (!Enum.IsDefined(nextState))
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Denied,
                "The requested completion state is undefined.");
        }

        var record = _operations[operation.OperationId];
        if (!CanTransition(record.State, nextState))
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Denied,
                $"Completion transition {record.State} -> {nextState} is not allowed.");
        }

        record.State = nextState;
        return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(record.Receipt);
    }

    public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
        PlatformOperationIdentity operation)
    {
        var validation = ValidateOperation(operation);
        if (!validation.IsSuccess)
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                validation.Status,
                validation.Message!);

        return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
            _operations[operation.OperationId].Receipt);
    }

    public PlatformAuthorityResult ValidateCompletionReceipt(
        PlatformOperationIdentity expectedOperation,
        PlatformCompletionReceipt receipt)
    {
        var operationValidation = ValidateOperation(expectedOperation);
        if (!operationValidation.IsSuccess) return operationValidation;

        var identityValidation = PlatformCompletionContract.ValidateReceiptIdentity(
            expectedOperation,
            receipt);
        if (!identityValidation.IsSuccess) return identityValidation;

        var record = _operations[expectedOperation.OperationId];
        if (record.State != receipt.State)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The completion receipt no longer matches the current operation state.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
    {
        BindDomainCallCount++;

        if (!Supports(PlatformAuthorityFeatures.NeutralDomainBinding))
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Neutral domain binding is not supported by this provider.");

        var subjectValidation = PlatformDomainContract.ValidateSubject(subject);
        if (!subjectValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                subjectValidation.Status,
                subjectValidation.Message!);

        if (_activeSubjects.ContainsKey(subject))
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform subject already has an active binding.");

        var lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(_nextDomainId++),
            new PlatformProviderLeaseGeneration(1),
            subject);

        _domains.Add(lease.LeaseId, new DomainRecord(lease));
        _activeSubjects.Add(subject, lease.LeaseId);
        return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
    }

    public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
    {
        RevokeDomainCallCount++;

        var validation = ValidateDomain(lease);
        if (!validation.IsSuccess) return validation;

        if (_mappings.Values.Any(m => !m.Revoked && m.Lease.DomainLease.LeaseId == lease.LeaseId))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Active region mappings must be revoked before the domain binding.");

        if (HasActiveDsc1SubmissionForDomain(lease))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Active DSC1 model submissions must close before the domain binding.");

        var record = _domains[lease.LeaseId];
        record.Revoked = true;
        _activeSubjects.Remove(record.Lease.Subject);
        return PlatformAuthorityResult.Ok();
    }

    public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
        PlatformProviderDomainLease domainLease,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access)
    {
        MapOwnedRegionCallCount++;

        if (!Supports(PlatformAuthorityFeatures.DirectOwnedRegionMapping))
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Direct owned-region mapping is not supported by this provider.");

        var domainValidation = ValidateDomain(domainLease);
        if (!domainValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                domainValidation.Status,
                domainValidation.Message!);

        if (!IsValidAccess(access))
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The requested platform memory access is invalid.");

        if (region.ByteLength <= 0)
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The mapped region must have a positive byte length.");

        var expectedOwner = region.Owner;
        if (expectedOwner.DomainId != domainLease.Subject.DomainId ||
            expectedOwner.ProcessGeneration != domainLease.Subject.ProcessGeneration)
        {
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The region owner does not match the bound platform subject.");
        }

        if (_activeRegions.ContainsKey(region))
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The exact owned region already has an active platform mapping.");

        var lease = new PlatformProviderRegionMappingLease(
            new PlatformProviderRegionMappingId(_nextMappingId++),
            new PlatformProviderLeaseGeneration(1),
            domainLease,
            region,
            access);

        _mappings.Add(lease.MappingId, new MappingRecord(lease));
        _activeRegions.Add(region, lease.MappingId);
        return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(lease);
    }

    public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy)
    {
        RevokeRegionMappingCallCount++;
        LastRegionRevocationPolicy = policy;

        var validation = ValidateRegionMappingForRevocation(mapping, policy);
        if (!validation.IsSuccess)
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                validation.Status,
                validation.Message!);

        if (HasActiveDsc1SubmissionForMapping(mapping))
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                PlatformAuthorityStatus.Denied,
                "Active DSC1 model submissions must close before their owned-region mappings.");
        }

        var record = _mappings[mapping.MappingId];
        if (record.RevocationOperation is { } existingOperation)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    existingOperation));
        }

        if (_regionRevocationFailure is { } failure)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                failure,
                "The host provider was configured to fail region drain/revocation.");
        }

        var staged = StageOperation(mapping.DomainLease);
        if (!staged.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                staged.Status,
                staged.Message!);
        }

        var operation = staged.Value!;
        var pending = AdvanceOperation(operation, PlatformCompletionState.Pending);
        if (!pending.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                pending.Status,
                pending.Message!);
        }

        var draining = AdvanceOperation(operation, PlatformCompletionState.Draining);
        if (!draining.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                draining.Status,
                draining.Message!);
        }

        record.RevocationOperation = operation;
        LastRegionRevocationOperation = operation;

        if (!_deferRegionRevocationCompletion)
        {
            var closed = CompleteRegionMappingRevocation(operation);
            if (!closed.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    closed.Status,
                    closed.Message!);
            }
        }

        return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
            new PlatformRegionRevocationTicket(
                mapping.MappingId,
                mapping.Generation,
                operation));
    }

    public PlatformAuthorityResult<PlatformCompletionReceipt> CompleteRegionMappingRevocation(
        PlatformOperationIdentity operation)
    {
        var operationValidation = ValidateOperation(operation);
        if (!operationValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                operationValidation.Status,
                operationValidation.Message!);
        }

        MappingRecord? mappingRecord = null;
        foreach (var candidate in _mappings.Values)
        {
            if (candidate.RevocationOperation is { } candidateOperation &&
                candidateOperation == operation)
            {
                mappingRecord = candidate;
                break;
            }
        }

        if (mappingRecord is null)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform operation is not a region-mapping revocation operation.");
        }

        if (mappingRecord.Revoked)
            return ObserveCompletion(operation);

        mappingRecord.Revoked = true;
        _activeRegions.Remove(mappingRecord.Lease.Region);
        return AdvanceOperation(operation, PlatformCompletionState.Closed);
    }

    public PlatformAuthorityResult RevokeRegionMapping(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy)
    {
        RevokeRegionMappingCallCount++;
        LastRegionRevocationPolicy = policy;

        var validation = ValidateRegionMappingForRevocation(mapping, policy);
        if (!validation.IsSuccess) return validation;

        if (HasActiveDsc1SubmissionForMapping(mapping))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Active DSC1 model submissions must close before their owned-region mappings.");

        if (_regionRevocationFailure is { } failure)
            return PlatformAuthorityResult.Fail(
                failure,
                "The host provider was configured to fail region drain/revocation.");

        var record = _mappings[mapping.MappingId];
        record.Revoked = true;
        _activeRegions.Remove(record.Lease.Region);
        return PlatformAuthorityResult.Ok();
    }

    private PlatformAuthorityResult ValidateRegionMappingForRevocation(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy)
    {
        if (policy != PlatformRegionRevocationPolicy.DrainBeforeRevoke)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "The host provider only supports drain-before-revoke semantics.");

        if (!_mappings.TryGetValue(mapping.MappingId, out var record))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform mapping does not exist.");

        if (record.Lease.Generation != mapping.Generation)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The platform mapping generation is stale.");

        if (record.Lease != mapping)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform mapping identity does not match the active mapping.");

        if (record.Revoked)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The platform mapping has already been revoked.");

        return PlatformAuthorityResult.Ok();
    }

    private PlatformAuthorityResult ValidateOperation(PlatformOperationIdentity operation)
    {
        if (operation.OperationId.Value == 0 || operation.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Platform operation identities must use non-zero IDs and generations.");
        }

        if (!_operations.TryGetValue(operation.OperationId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform operation does not exist.");
        }

        if (record.Operation.Generation != operation.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The platform operation generation is stale.");
        }

        if (record.Operation.DomainLease.LeaseId != operation.DomainLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform operation belongs to a different domain lease.");
        }

        if (record.Operation.DomainLease.Generation != operation.DomainLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The platform operation domain-lease generation is stale.");
        }

        if (record.Operation.DomainLease.Subject != operation.DomainLease.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform operation domain subject does not match the active operation.");
        }

        return ValidateDomain(record.Operation.DomainLease);
    }

    private PlatformAuthorityResult ValidateDomain(PlatformProviderDomainLease lease)
    {
        if (!_domains.TryGetValue(lease.LeaseId, out var record))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform domain binding does not exist.");

        if (record.Lease.Generation != lease.Generation)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The platform domain binding generation is stale.");

        if (record.Lease.Subject != lease.Subject)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform domain subject does not match the active binding.");

        if (record.Revoked)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The platform domain binding has been revoked.");

        return PlatformAuthorityResult.Ok();
    }

    private static bool CanTransition(
        PlatformCompletionState current,
        PlatformCompletionState next) =>
        current switch
        {
            PlatformCompletionState.Staged =>
                next is PlatformCompletionState.Pending or
                    PlatformCompletionState.Cancelled or
                    PlatformCompletionState.Faulted,
            PlatformCompletionState.Pending =>
                next is PlatformCompletionState.Draining or
                    PlatformCompletionState.Completed or
                    PlatformCompletionState.Cancelled or
                    PlatformCompletionState.Faulted,
            PlatformCompletionState.Draining =>
                next is PlatformCompletionState.Completed or
                    PlatformCompletionState.Cancelled or
                    PlatformCompletionState.Closed or
                    PlatformCompletionState.Faulted,
            PlatformCompletionState.Completed =>
                next is PlatformCompletionState.Closed or PlatformCompletionState.Faulted,
            PlatformCompletionState.Cancelled =>
                next is PlatformCompletionState.Draining or
                    PlatformCompletionState.Closed or
                    PlatformCompletionState.Faulted,
            _ => false
        };

    private static PlatformMemoryVisibilityOutcome ModelMemoryVisibility(
        PlatformMemoryConsumerClass consumer,
        PlatformMemoryVisibilityRequirement requirement) =>
        (consumer, requirement) switch
        {
            (PlatformMemoryConsumerClass.CpuExecution,
                PlatformMemoryVisibilityRequirement.CoherentAccess) =>
                PlatformMemoryVisibilityOutcome.Coherent,
            (PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence) =>
                PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            (PlatformMemoryConsumerClass.IoDevice,
                PlatformMemoryVisibilityRequirement.CacheMaintenance) =>
                PlatformMemoryVisibilityOutcome.CacheMaintenanceSatisfied,
            _ => PlatformMemoryVisibilityOutcome.Unsupported
        };

    private bool Supports(PlatformAuthorityFeatures feature) =>
        (Descriptor.Features & feature) == feature;

    private static bool IsValidAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;
}
