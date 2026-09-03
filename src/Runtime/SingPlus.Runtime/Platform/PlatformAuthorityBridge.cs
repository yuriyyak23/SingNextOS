using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDomainBindingId(ulong Value);
public readonly record struct PlatformDomainBindingGeneration(ulong Value);
public readonly record struct PlatformRegionMappingId(ulong Value);
public readonly record struct PlatformRegionMappingGeneration(ulong Value);

public readonly record struct PlatformDomainBinding(
    PlatformDomainBindingId BindingId,
    PlatformDomainBindingGeneration Generation,
    PlatformDomainIdentity Subject);

public readonly record struct PlatformRegionMapping(
    PlatformRegionMappingId MappingId,
    PlatformRegionMappingGeneration Generation,
    PlatformDomainBinding DomainBinding,
    RegionHandle Region,
    PlatformMemoryAccess Access);

public enum PlatformExternalClosureState
{
    Active = 0,
    Draining,
    Closed,
    Faulted
}

public readonly record struct PlatformRegionMappingLifecycle(
    PlatformRegionMapping Mapping,
    bool LocalAuthorizationRevoked,
    PlatformExternalClosureState PlatformClosure,
    bool LocalReservationReleased)
{
    public bool LocalReclaimAllowed =>
        PlatformClosure == PlatformExternalClosureState.Closed && LocalReservationReleased;
}

public sealed partial class PlatformAuthorityBridge
{
    private enum DomainAuthorityState
    {
        Active = 0,
        Quarantined,
        Closed,
    }

    private sealed class DomainRecord(
        PlatformDomainBinding binding,
        PlatformProviderDomainLease providerLease)
    {
        public PlatformDomainBinding Binding { get; } = binding;
        public PlatformProviderDomainLease ProviderLease { get; } = providerLease;
        public DomainAuthorityState AuthorityState { get; set; }
    }

    private sealed class MappingRecord(
        PlatformRegionMapping mapping,
        PlatformProviderRegionMappingLease providerLease,
        CapabilityId authorityCapabilityId)
    {
        public PlatformRegionMapping Mapping { get; } = mapping;
        public PlatformProviderRegionMappingLease ProviderLease { get; } = providerLease;
        public CapabilityId AuthorityCapabilityId { get; } = authorityCapabilityId;
        public bool LocalAuthorizationRevoked { get; set; }
        public PlatformExternalClosureState ClosureState { get; set; } =
            PlatformExternalClosureState.Active;
        public PlatformOperationIdentity? ClosureOperation { get; set; }
        public bool LocalReservationReleased { get; set; }

        public PlatformRegionMappingLifecycle Lifecycle => new(
            Mapping,
            LocalAuthorizationRevoked,
            ClosureState,
            LocalReservationReleased);
    }

    private readonly IPlatformAuthorityProvider? _provider;
    private readonly PlatformFeatureManifest _featureManifest;
    private readonly Dictionary<PlatformDomainBindingId, DomainRecord> _domains = [];
    private readonly Dictionary<PlatformRegionMappingId, MappingRecord> _mappings = [];
    private readonly Dictionary<PlatformDomainIdentity, PlatformDomainBindingId> _activeSubjects = [];
    private ulong _nextDomainBindingId = 1;
    private ulong _nextMappingId = 1;

    internal PlatformAuthorityBridge(IPlatformAuthorityProvider? provider)
    {
        _provider = provider;
        _featureManifest = provider switch
        {
            null => PlatformFeatureManifest.Empty,
            IPlatformFeatureProvider featureProvider =>
                featureProvider.QueryFeatures() ?? PlatformFeatureManifest.Empty,
            _ => PlatformFeatureManifest.FromLegacy(provider.Descriptor.Features)
        };
    }

    public PlatformProviderDescriptor? ProviderDescriptor => _provider?.Descriptor;

    public PlatformFeatureManifest FeatureManifest => _featureManifest;

    public bool IsAvailable => _provider is not null;

    internal KernelResult<PlatformDomainBinding> BindDomain(PlatformDomainIdentity subject)
    {
        if (_provider is null)
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformUnavailable,
                "No platform authority provider is configured.");

        var subjectValidation = PlatformDomainContract.ValidateSubject(subject);
        if (!subjectValidation.IsSuccess)
            return FromProviderFailure<PlatformDomainBinding>(
                subjectValidation.Status,
                subjectValidation.Message);

        var neutralDomainFeature = _featureManifest.Resolve(
            PlatformFeatureFamily.NeutralDomains);
        if (!Supports(PlatformAuthorityFeatures.NeutralDomainBinding) ||
            neutralDomainFeature.ContractVersion < PlatformDomainContract.ContractVersion ||
            neutralDomainFeature.Availability is not
                (PlatformFeatureAvailability.RuntimeAdmission or
                 PlatformFeatureAvailability.Executable))
        {
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformUnsupported,
                $"The platform provider does not admit neutral domain contract v{PlatformDomainContract.ContractVersion} authority.");
        }

        if (_activeSubjects.ContainsKey(subject))
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformDenied,
                "The local subject already has an active platform binding.");

        var providerResult = _provider.BindDomain(subject);
        if (!providerResult.IsSuccess)
            return FromProviderFailure<PlatformDomainBinding>(providerResult.Status, providerResult.Message);

        var providerLease = providerResult.Value!;
        var leaseValidation = PlatformDomainContract.ValidateLease(subject, providerLease);
        if (!leaseValidation.IsSuccess)
        {
            var leaseMessage = leaseValidation.Message ??
                "The platform provider returned malformed domain authority.";
            var cleanup = _provider.RevokeDomain(providerLease);
            if (!cleanup.IsSuccess)
            {
                _ = AddDomainRecord(
                    subject,
                    providerLease,
                    DomainAuthorityState.Quarantined);
            }

            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformFaulted,
                cleanup.IsSuccess
                    ? leaseMessage
                    : $"{leaseMessage} Cleanup returned {cleanup.Status}; the provider lease remains quarantined for teardown.");
        }

        var binding = AddDomainRecord(
            subject,
            providerLease,
            DomainAuthorityState.Active);
        return KernelResult<PlatformDomainBinding>.Ok(binding);
    }

    internal bool TryGetQuarantinedDomainBinding(
        PlatformDomainIdentity subject,
        out PlatformDomainBinding binding)
    {
        if (_activeSubjects.TryGetValue(subject, out var bindingId) &&
            _domains.TryGetValue(bindingId, out var record) &&
            record.AuthorityState == DomainAuthorityState.Quarantined)
        {
            binding = record.Binding;
            return true;
        }

        binding = default;
        return false;
    }

    internal KernelResult RevokeDomain(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDomainIdentity(binding, expectedSubject);
        if (!validation.IsSuccess) return validation;

        if (_mappings.Values.Any(m =>
                !m.LocalReservationReleased &&
                m.Mapping.DomainBinding.BindingId == binding.BindingId))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingActive,
                "Platform region mappings must reach verified closure and release their local reservation before the domain binding.");
        }

        var record = _domains[binding.BindingId];
        if (record.AuthorityState == DomainAuthorityState.Closed)
        {
            ReleaseActiveSubject(record);
            return KernelResult.Ok();
        }

        var providerResult = _provider!.RevokeDomain(record.ProviderLease);
        if (!providerResult.IsSuccess)
        {
            if (RequiresDomainQuarantine(providerResult.Status))
                QuarantineDomain(record);

            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        CloseDomain(record);
        return KernelResult.Ok();
    }

    internal KernelResult ValidateDomain(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        var identityValidation = ValidateDomainIdentity(binding, expectedSubject);
        if (!identityValidation.IsSuccess) return identityValidation;

        var record = _domains[binding.BindingId];
        if (record.AuthorityState != DomainAuthorityState.Active)
        {
            return record.AuthorityState == DomainAuthorityState.Closed
                ? KernelResult.Fail(
                    KernelError.PlatformBindingRevoked,
                    "The platform domain binding has reached externally confirmed closure.")
                : KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "The platform domain binding is quarantined without external closure proof.");
        }

        return KernelResult.Ok();
    }

    private KernelResult ValidateDomainIdentity(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_domains.TryGetValue(binding.BindingId, out var record))
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform domain binding does not exist.");

        if (record.Binding.Generation != binding.Generation)
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform domain binding generation is stale.");

        if (record.Binding.Subject != binding.Subject || record.Binding.Subject != expectedSubject)
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The platform domain binding does not belong to the expected local subject.");

        return KernelResult.Ok();
    }

    internal KernelResult TransitionDomainExecution(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformDomainExecutionTransition transition)
    {
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess) return bindingValidation;

        var transitionValidation = PlatformDomainExecutionContract.ValidateTransition(transition);
        if (!transitionValidation.IsSuccess)
            return FromProviderFailure(transitionValidation.Status, transitionValidation.Message);

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable))
        {
            return KernelResult.Fail(
                KernelError.PlatformUnsupported,
                "The bound platform provider does not classify neutral execution transitions as executable.");
        }

        if (_provider is not IPlatformDomainExecutionProvider executionProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformUnsupported,
                "The bound platform provider does not expose neutral execution lifecycle transitions.");
        }

        var record = _domains[binding.BindingId];
        var providerResult = executionProvider.TransitionDomainExecution(
            record.ProviderLease,
            transition);
        if (!providerResult.IsSuccess)
        {
            if (RequiresDomainQuarantine(providerResult.Status))
                QuarantineDomain(record);

            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        var resultValidation = PlatformDomainExecutionContract.ValidateResult(
            record.ProviderLease,
            transition,
            providerResult.Value!);
        if (!resultValidation.IsSuccess)
        {
            QuarantineDomain(record);
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                resultValidation.Message ?? "The platform provider returned malformed execution transition evidence.");
        }

        return KernelResult.Ok();
    }

    internal KernelResult<PlatformRegionMapping> MapOwnedRegion(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access)
    {
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);

        if (_provider is null)
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformUnavailable,
                "No platform authority provider is configured.");

        if (!Supports(PlatformAuthorityFeatures.DirectOwnedRegionMapping))
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not support direct owned-region mapping.");

        if (!IsValidAccess(access))
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformDenied,
                "The requested platform memory access is invalid.");

        var domainRecord = _domains[binding.BindingId];
        var providerResult = _provider.MapOwnedRegion(domainRecord.ProviderLease, region, access);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status is PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.Stale)
                QuarantineDomain(domainRecord);

            return FromProviderFailure<PlatformRegionMapping>(providerResult.Status, providerResult.Message);
        }

        var providerLease = providerResult.Value!;
        if (providerLease.DomainLease != domainRecord.ProviderLease ||
            providerLease.Region != region ||
            providerLease.Access != access)
        {
            _ = _provider.RevokeRegionMapping(
                providerLease,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke);
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformFaulted,
                "The platform provider returned a mapping identity that does not match the request.");
        }

        var mapping = new PlatformRegionMapping(
            new PlatformRegionMappingId(_nextMappingId++),
            new PlatformRegionMappingGeneration(1),
            binding,
            region.Handle,
            access);

        _mappings.Add(
            mapping.MappingId,
            new MappingRecord(mapping, providerLease, authorityCapabilityId));
        return KernelResult<PlatformRegionMapping>.Ok(mapping);
    }

    internal KernelResult<PlatformRegionMappingLifecycle> BeginRegionMappingRevocation(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformRegionRevocationPolicy policy)
    {
        var validation = ValidateMappingIdentity(mapping, expectedSubject);
        if (!validation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                validation.Error,
                validation.Message!);

        var record = _mappings[mapping.MappingId];
        if (record.LocalReservationReleased ||
            record.ClosureState == PlatformExternalClosureState.Closed)
        {
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform region mapping has already reached verified closure.");
        }

        if (record.ClosureState == PlatformExternalClosureState.Faulted)
        {
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformFaulted,
                "The platform region mapping closure is faulted and remains non-reclaimable.");
        }

        if (record.ClosureState == PlatformExternalClosureState.Draining &&
            record.ClosureOperation is not null)
        {
            return ObserveRegionMappingRevocation(mapping, expectedSubject);
        }

        record.ClosureState = PlatformExternalClosureState.Draining;

        if (_provider is not IPlatformRegionRevocationProvider revocationProvider)
        {
            var legacyResult = _provider!.RevokeRegionMapping(record.ProviderLease, policy);
            if (!legacyResult.IsSuccess)
            {
                if (legacyResult.Status == PlatformAuthorityStatus.Faulted)
                    record.ClosureState = PlatformExternalClosureState.Faulted;

                return FromProviderFailure<PlatformRegionMappingLifecycle>(
                    legacyResult.Status,
                    legacyResult.Message);
            }

            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformUnsupported,
                "The provider does not expose a completion-backed region-revocation contract; local reclaim remains pinned despite synchronous provider revocation.");
        }

        var beginResult = revocationProvider.BeginRegionMappingRevocation(
            record.ProviderLease,
            policy);
        if (!beginResult.IsSuccess)
        {
            if (beginResult.Status == PlatformAuthorityStatus.Faulted)
                record.ClosureState = PlatformExternalClosureState.Faulted;

            return FromProviderFailure<PlatformRegionMappingLifecycle>(
                beginResult.Status,
                beginResult.Message);
        }

        var ticket = beginResult.Value!;
        var ticketValidation = PlatformRegionRevocationContract.ValidateTicket(
            record.ProviderLease,
            ticket);
        if (!ticketValidation.IsSuccess)
        {
            record.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformFaulted,
                ticketValidation.Message ?? "The provider returned a malformed region-revocation ticket.");
        }

        record.ClosureOperation = ticket.Operation;
        return ObserveRegionMappingRevocation(mapping, expectedSubject);
    }

    internal KernelResult<PlatformRegionMappingLifecycle> ObserveRegionMappingRevocation(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateMappingIdentity(mapping, expectedSubject);
        if (!validation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                validation.Error,
                validation.Message!);

        var record = _mappings[mapping.MappingId];
        if (record.ClosureState == PlatformExternalClosureState.Active)
        {
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformDenied,
                "Platform region mapping revocation has not started.");
        }

        if (record.ClosureState == PlatformExternalClosureState.Faulted)
        {
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformFaulted,
                "The platform region mapping closure is faulted and remains non-reclaimable.");
        }

        if (record.ClosureState == PlatformExternalClosureState.Closed)
            return KernelResult<PlatformRegionMappingLifecycle>.Ok(record.Lifecycle);

        if (record.ClosureOperation is not { } operation)
        {
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformBindingDraining,
                "The platform region mapping is draining without a completion-capable operation; local reclaim remains pinned.");
        }

        if (_provider is not IPlatformCompletionProvider completionProvider)
        {
            record.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformFaulted,
                "The provider returned a revocation operation but cannot observe its completion.");
        }

        var observed = completionProvider.ObserveCompletion(operation);
        if (!observed.IsSuccess)
        {
            if (observed.Status == PlatformAuthorityStatus.Faulted)
                record.ClosureState = PlatformExternalClosureState.Faulted;

            return FromProviderFailure<PlatformRegionMappingLifecycle>(
                observed.Status,
                observed.Message);
        }

        var receipt = observed.Value!;
        var receiptValidation = PlatformCompletionContract.ValidateReceiptIdentity(operation, receipt);
        if (!receiptValidation.IsSuccess)
        {
            if (receiptValidation.Status == PlatformAuthorityStatus.Faulted)
                record.ClosureState = PlatformExternalClosureState.Faulted;

            return FromProviderFailure<PlatformRegionMappingLifecycle>(
                receiptValidation.Status,
                receiptValidation.Message);
        }

        switch (receipt.State)
        {
            case PlatformCompletionState.Closed:
                record.ClosureState = PlatformExternalClosureState.Closed;
                return KernelResult<PlatformRegionMappingLifecycle>.Ok(record.Lifecycle);
            case PlatformCompletionState.Faulted:
                record.ClosureState = PlatformExternalClosureState.Faulted;
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    KernelError.PlatformFaulted,
                    "The provider reported a faulted region-revocation operation; local reclaim remains pinned.");
            default:
                record.ClosureState = PlatformExternalClosureState.Draining;
                return KernelResult<PlatformRegionMappingLifecycle>.Ok(record.Lifecycle);
        }
    }

    internal KernelResult<PlatformRegionMappingLifecycle> QueryRegionMappingLifecycle(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateMappingIdentity(mapping, expectedSubject);
        if (!validation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                validation.Error,
                validation.Message!);

        return KernelResult<PlatformRegionMappingLifecycle>.Ok(
            _mappings[mapping.MappingId].Lifecycle);
    }

    internal KernelResult MarkRegionReservationReleased(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateMappingIdentity(mapping, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _mappings[mapping.MappingId];
        if (record.ClosureState != PlatformExternalClosureState.Closed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "Local region reservation cannot be released before verified platform closure.");
        }

        record.LocalReservationReleased = true;
        return KernelResult.Ok();
    }

    internal KernelResult RevokeRegionMapping(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformRegionRevocationPolicy policy)
    {
        var lifecycle = BeginRegionMappingRevocation(mapping, expectedSubject, policy);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        return lifecycle.Value!.PlatformClosure switch
        {
            PlatformExternalClosureState.Closed => KernelResult.Ok(),
            PlatformExternalClosureState.Faulted => KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform region mapping closure faulted."),
            _ => KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "The platform region mapping is still draining.")
        };
    }

    internal KernelResult ValidateMapping(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateMappingIdentity(mapping, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _mappings[mapping.MappingId];
        if (record.LocalAuthorizationRevoked)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The local authorization backing this platform mapping has been revoked.");
        }

        return record.ClosureState switch
        {
            PlatformExternalClosureState.Active => KernelResult.Ok(),
            PlatformExternalClosureState.Draining => KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "The platform region mapping is draining and cannot authorize new effects."),
            PlatformExternalClosureState.Faulted => KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform region mapping closure faulted and cannot authorize new effects."),
            _ => KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform region mapping has reached verified closure.")
        };
    }

    internal IReadOnlyList<PlatformRegionMapping> BeginCapabilityRevocation(CapabilityId capabilityId)
    {
        var affected = _mappings.Values
            .Where(m =>
                m.AuthorityCapabilityId == capabilityId &&
                !m.LocalReservationReleased)
            .OrderBy(static m => m.Mapping.MappingId.Value)
            .ToArray();

        foreach (var record in affected)
            record.LocalAuthorizationRevoked = true;

        return affected.Select(static m => m.Mapping).ToArray();
    }

    internal bool HasActiveAuthority(PlatformDomainIdentity subject) =>
        (_activeSubjects.TryGetValue(subject, out var id) &&
         _domains.TryGetValue(id, out var domain) &&
         domain.AuthorityState != DomainAuthorityState.Closed) ||
        _mappings.Values.Any(
            m =>
                !m.LocalReservationReleased &&
                m.Mapping.DomainBinding.Subject == subject);

    internal bool HasActiveMapping(RegionHandle region) =>
        _mappings.Values.Any(
            m => !m.LocalReservationReleased && m.Mapping.Region == region);

    private KernelResult ValidateMappingIdentity(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_mappings.TryGetValue(mapping.MappingId, out var record))
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform region mapping does not exist.");

        if (record.Mapping.Generation != mapping.Generation)
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform region mapping generation is stale.");

        if (record.Mapping.DomainBinding.BindingId != mapping.DomainBinding.BindingId)
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The platform region mapping refers to a different local platform binding.");

        if (record.Mapping.DomainBinding.Generation != mapping.DomainBinding.Generation)
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform region mapping domain-binding generation is stale.");

        if (record.Mapping.DomainBinding.Subject != mapping.DomainBinding.Subject)
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The platform region mapping domain subject does not match the active mapping.");

        if (record.Mapping.Region.RegionId != mapping.Region.RegionId)
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The platform region mapping refers to a different local region.");

        if (record.Mapping.Region.Generation != mapping.Region.Generation)
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform region mapping region generation is stale.");

        if (record.Mapping.Access != mapping.Access)
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "The platform region mapping access does not match the active mapping.");

        return ValidateDomain(record.Mapping.DomainBinding, expectedSubject);
    }

    private PlatformDomainBinding AddDomainRecord(
        PlatformDomainIdentity subject,
        PlatformProviderDomainLease providerLease,
        DomainAuthorityState authorityState)
    {
        var binding = new PlatformDomainBinding(
            new PlatformDomainBindingId(_nextDomainBindingId++),
            new PlatformDomainBindingGeneration(1),
            subject);
        var record = new DomainRecord(binding, providerLease)
        {
            AuthorityState = authorityState,
        };
        _domains.Add(binding.BindingId, record);
        _activeSubjects.Add(subject, binding.BindingId);
        return binding;
    }

    private static void QuarantineDomain(DomainRecord record)
    {
        if (record.AuthorityState != DomainAuthorityState.Closed)
            record.AuthorityState = DomainAuthorityState.Quarantined;
    }

    private static bool RequiresDomainQuarantine(PlatformAuthorityStatus status) =>
        status is PlatformAuthorityStatus.Stale or
            PlatformAuthorityStatus.Revoked or
            PlatformAuthorityStatus.WrongDomain or
            PlatformAuthorityStatus.Faulted;

    private void CloseDomain(DomainRecord record)
    {
        record.AuthorityState = DomainAuthorityState.Closed;
        ReleaseActiveSubject(record);
    }

    private void ReleaseActiveSubject(DomainRecord record)
    {
        if (_activeSubjects.TryGetValue(record.Binding.Subject, out var activeId) &&
            activeId == record.Binding.BindingId)
        {
            _activeSubjects.Remove(record.Binding.Subject);
        }
    }

    private bool Supports(PlatformAuthorityFeatures feature) =>
        _provider is not null &&
        (_provider.Descriptor.Features & feature) == feature;

    private static bool IsValidAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;

    private static KernelResult FromProviderFailure(
        PlatformAuthorityStatus status,
        string? message) =>
        KernelResult.Fail(ToKernelError(status), message ?? $"Platform provider returned {status}.");

    private static KernelResult<T> FromProviderFailure<T>(
        PlatformAuthorityStatus status,
        string? message) =>
        KernelResult<T>.Fail(ToKernelError(status), message ?? $"Platform provider returned {status}.");

    private static KernelError ToKernelError(PlatformAuthorityStatus status) =>
        status switch
        {
            PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
            PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
            PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
            PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
            PlatformAuthorityStatus.Revoked => KernelError.PlatformBindingRevoked,
            PlatformAuthorityStatus.WrongDomain => KernelError.WrongPlatformDomain,
            PlatformAuthorityStatus.Faulted => KernelError.PlatformFaulted,
            _ => KernelError.PlatformFaulted
        };
}
