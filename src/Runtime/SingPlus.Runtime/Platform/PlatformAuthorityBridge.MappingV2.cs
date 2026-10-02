using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformOwnedRegionSliceMapping(
    PlatformRegionMapping Mapping,
    long Offset,
    long Length)
{
    public RegionHandle Region => Mapping.Region;
    public PlatformMemoryAccess Access => Mapping.Access;
}

public readonly record struct PlatformRegionVisibilityEvidence(
    PlatformOwnedRegionSliceMapping Mapping,
    PlatformMemoryConsumerClass Consumer,
    PlatformMemoryVisibilityRequirement Requirement,
    PlatformMemoryVisibilityOutcome Outcome)
{
    public bool IsSatisfied =>
        PlatformMemoryVisibilityContract.IsSatisfied(Requirement, Outcome);
}

public sealed partial class PlatformAuthorityBridge
{
    private readonly Dictionary<PlatformRegionMappingId, PlatformRegionSlice>
        _exactMappingSlices = [];

    internal KernelResult<PlatformOwnedRegionSliceMapping> MapOwnedRegionSlice(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformRegionSlice slice,
        bool retainFaultedHandleOnMalformed = false) =>
        MapOwnedRegionSlice(binding, expectedSubject, authorityCapabilityId, slice,
            retainFaultedHandleOnMalformed, out _);

    internal KernelResult<PlatformOwnedRegionSliceMapping> MapOwnedRegionSlice(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformRegionSlice slice,
        bool retainFaultedHandleOnMalformed,
        out bool retainReservation)
    {
        retainReservation = false;
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        var sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
        if (!sliceValidation.IsSuccess)
        {
            return FromProviderFailure<PlatformOwnedRegionSliceMapping>(
                sliceValidation.Status,
                sliceValidation.Message);
        }

        if (_provider is null)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.PlatformUnavailable,
                "No platform authority provider is configured.");
        }

        var mappingFeature = _featureManifest.Resolve(PlatformFeatureFamily.OwnedRegionMapping);
        if (mappingFeature.ContractVersion < PlatformOwnedRegionMappingContract.ContractVersion ||
            mappingFeature.Availability is not
                (PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable))
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise executable exact owned-region mapping v2.");
        }

        if (_provider is not IPlatformOwnedRegionMappingProvider mappingProvider)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose exact owned-region slice mapping.");
        }

        var domainRecord = _domains[binding.BindingId];
        var providerIncarnation = CurrentProviderIncarnation();
        var backendEpoch = BackendEpoch;
        if (providerIncarnation.Value == 0)
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(KernelError.PlatformFaulted,
                "The exact mapping provider returned an invalid zero runtime incarnation.");
        lock (_secureDomainLifecycleGate)
        {
            var admission = ValidateDomain(binding, expectedSubject);
            if (!admission.IsSuccess)
                return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(admission.Error, admission.Message!);
            domainRecord.PendingMappings++;
        }
        try
        {
        PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> providerResult;
        try { providerResult = mappingProvider.MapOwnedRegionSlice(domainRecord.ProviderLease, slice); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                // Keep an owner record for an effect whose receipt was lost. No provider
                // lease or closure evidence is inferred from this local correlation.
                var unresolved = new PlatformRegionMapping(
                    new PlatformRegionMappingId(_nextMappingId++),
                    new PlatformRegionMappingGeneration(1), binding, slice.Region.Handle, slice.Access);
                _mappings.Add(unresolved.MappingId,
                    new MappingRecord(unresolved, default, authorityCapabilityId,
                        providerIncarnation, backendEpoch)
                    {
                        ClosureState = PlatformExternalClosureState.Faulted,
                    });
                _exactMappingSlices.Add(unresolved.MappingId, slice);
                domainRecord.MappingMayHaveEffect = true;
                QuarantineDomain(domainRecord);
            }
            retainReservation = true;
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(KernelError.PlatformFaulted,
                $"Exact mapping may have taken effect without a provider receipt: {exception.Message}");
        }
        if (!providerResult.IsSuccess)
        {
            var admissionLostContinuity = BackendEpoch != backendEpoch ||
                !ReferenceEquals(_domains[binding.BindingId], domainRecord) ||
                domainRecord.AuthorityState != DomainAuthorityState.Active;
            if (RequiresDomainQuarantine(providerResult.Status) || admissionLostContinuity)
            {
                lock (_secureDomainLifecycleGate)
                {
                var quarantinedMapping = new PlatformRegionMapping(
                    new PlatformRegionMappingId(_nextMappingId++),
                    new PlatformRegionMappingGeneration(1), binding,
                    slice.Region.Handle, slice.Access);
                _mappings.Add(quarantinedMapping.MappingId,
                    new MappingRecord(quarantinedMapping, default,
                        authorityCapabilityId, providerIncarnation, backendEpoch)
                    {
                        ClosureState = PlatformExternalClosureState.Faulted,
                    });
                _exactMappingSlices.Add(quarantinedMapping.MappingId, slice);
                QuarantineDomain(domainRecord);
                retainReservation = true;
                }
            }

            if (admissionLostContinuity)
                return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(KernelError.PlatformFaulted,
                    "Exact mapping admission crossed a backend generation or parent-domain transition; local reservation remains pinned.");

            return FromProviderFailure<PlatformOwnedRegionSliceMapping>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerMapping = providerResult.Value!;
        var resultValidation = PlatformOwnedRegionMappingContract.ValidateResult(
            domainRecord.ProviderLease,
            slice,
            providerMapping);
        if (!resultValidation.IsSuccess && !retainFaultedHandleOnMalformed)
        {
            var cleanupProven = TryCloseMalformedMappingLease(providerMapping.Lease,
                providerIncarnation, backendEpoch);
            if (!cleanupProven)
            {
                lock (_secureDomainLifecycleGate)
                {
                var quarantinedMapping = new PlatformRegionMapping(
                    new PlatformRegionMappingId(_nextMappingId++),
                    new PlatformRegionMappingGeneration(1), binding,
                    slice.Region.Handle, slice.Access);
                _mappings.Add(quarantinedMapping.MappingId,
                    new MappingRecord(quarantinedMapping, providerMapping.Lease,
                        authorityCapabilityId, providerIncarnation, backendEpoch)
                    {
                        ClosureState = PlatformExternalClosureState.Faulted,
                    });
                _exactMappingSlices.Add(quarantinedMapping.MappingId, slice);
                QuarantineDomain(domainRecord);
                retainReservation = true;
                }
            }
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.PlatformFaulted,
                resultValidation.Message ?? "The provider returned malformed exact mapping evidence.");
        }

        PlatformRegionMapping mapping;
        lock (_secureDomainLifecycleGate)
        {
            mapping = new PlatformRegionMapping(
                new PlatformRegionMappingId(_nextMappingId++),
                new PlatformRegionMappingGeneration(1), binding,
                slice.Region.Handle, slice.Access);
            var mappingRecord = new MappingRecord(mapping, providerMapping.Lease,
                authorityCapabilityId, providerIncarnation, backendEpoch);
            if (!resultValidation.IsSuccess ||
                !ReferenceEquals(_domains[binding.BindingId], domainRecord) ||
                CurrentProviderIncarnation() != providerIncarnation || BackendEpoch != backendEpoch ||
                domainRecord.AuthorityState != DomainAuthorityState.Active)
                mappingRecord.ClosureState = PlatformExternalClosureState.Faulted;
            _mappings.Add(mapping.MappingId, mappingRecord);
            _exactMappingSlices.Add(mapping.MappingId, slice);
        }

        return KernelResult<PlatformOwnedRegionSliceMapping>.Ok(
            new PlatformOwnedRegionSliceMapping(
                mapping,
                slice.Offset,
                slice.Length));
        }
        finally
        {
            lock (_secureDomainLifecycleGate) domainRecord.PendingMappings--;
        }
    }

    internal KernelResult<PlatformRegionVisibilityEvidence> PrepareRegionMappingForConsumer(
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformMemoryConsumerClass consumer,
        PlatformMemoryVisibilityRequirement requirement)
    {
        var validation = ValidateExactMapping(mapping, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                validation.Error,
                validation.Message!);
        }

        if (_provider is not IPlatformRegionVisibilityProvider visibilityProvider)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose mapping-bound memory visibility semantics.");
        }

        var record = _mappings[mapping.Mapping.MappingId];
        var slice = _exactMappingSlices[mapping.Mapping.MappingId];
        var request = new PlatformRegionVisibilityRequest(
            record.ProviderLease,
            slice,
            consumer,
            requirement);

        var requestValidation = PlatformRegionVisibilityContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                requestValidation.Message ?? "The mapping visibility request is malformed.");
        }

        var providerResult = visibilityProvider.PrepareRegionMappingForConsumer(request);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status == PlatformAuthorityStatus.Revoked)
                record.LocalAuthorizationRevoked = true;

            return FromProviderFailure<PlatformRegionVisibilityEvidence>(
                providerResult.Status,
                providerResult.Message);
        }

        var result = providerResult.Value!;
        var resultValidation = PlatformRegionVisibilityContract.ValidateResult(request, result);
        if (!resultValidation.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                resultValidation.Message ?? "The provider returned malformed mapping visibility evidence.");
        }

        if (!result.IsSatisfied)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider cannot satisfy the exact mapping visibility requirement.");
        }

        return KernelResult<PlatformRegionVisibilityEvidence>.Ok(
            new PlatformRegionVisibilityEvidence(
                mapping,
                result.Consumer,
                result.Requirement,
                result.Outcome));
    }

    internal KernelResult ValidateExactMapping(
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        var baseValidation = ValidateMapping(mapping.Mapping, expectedSubject);
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
                "The exact mapping range or access does not match the active mapping.");
        }

        return KernelResult.Ok();
    }

    internal void ForgetExactMappingMetadata(PlatformRegionMapping mapping) =>
        _exactMappingSlices.Remove(mapping.MappingId);
}
