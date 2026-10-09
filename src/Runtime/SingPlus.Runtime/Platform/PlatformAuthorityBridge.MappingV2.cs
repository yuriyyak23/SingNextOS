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

        var capturedDomain = CaptureDomainAdmissionRecord(binding, expectedSubject);
        if (!capturedDomain.IsSuccess)
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(capturedDomain.Error, capturedDomain.Message!);
        var domainRecord = capturedDomain.Value!;
        PlatformProviderIncarnation providerIncarnation;
        try { providerIncarnation = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(KernelError.PlatformFaulted,
                "The exact mapping admission source is unavailable; no provider Map was invoked.");
        }
        var backendEpoch = BackendEpoch;
        if (providerIncarnation.Value == 0)
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(KernelError.PlatformFaulted,
                "The exact mapping provider returned an invalid zero runtime incarnation.");
        PlatformRegionMappingId mappingIdentity;
        lock (_secureDomainLifecycleGate)
        {
            var originalSource = ValidateRootCleanupSourceLocked(domainRecord, binding, expectedSubject,
                domainRecord.BackendEpoch, providerIncarnation);
            if (!originalSource.IsSuccess)
                return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(originalSource.Error, originalSource.Message!);
            var admission = ValidateDomain(binding, expectedSubject);
            if (!admission.IsSuccess)
                return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(admission.Error, admission.Message!);
            if (domainRecord.PendingMappings == int.MaxValue || domainRecord.MappingRecords.Count >= int.MaxValue - domainRecord.PendingMappings || _nextMappingId is 0 or ulong.MaxValue ||
                _mappings.ContainsKey(new PlatformRegionMappingId(_nextMappingId)))
                return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(KernelError.CapacityExhausted,
                    "The root mapping admission identity or interlock is exhausted.");
            mappingIdentity = new PlatformRegionMappingId(_nextMappingId++);
            domainRecord.MappingRecords.EnsureCapacity(domainRecord.MappingRecords.Count + domainRecord.PendingMappings + 1);
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
                    mappingIdentity,
                    new PlatformRegionMappingGeneration(1), binding, slice.Region.Handle, slice.Access);
                AddOriginalMappingRecordLocked(domainRecord, unresolved.MappingId,
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
            var refusalSource = providerIncarnation;
            var refusalSourceAvailable = true;
            if (providerResult.Status == PlatformAuthorityStatus.NotAccepted)
            {
                try { refusalSource = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { refusalSourceAvailable = false; }
            }
            lock (_secureDomainLifecycleGate)
            {
            var admissionLostContinuity = !refusalSourceAvailable || refusalSource != providerIncarnation ||
                BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !_domains.TryGetValue(binding.BindingId, out var routed) || !ReferenceEquals(routed, domainRecord) ||
                !ValidateDomain(binding, expectedSubject).IsSuccess;
            if (providerResult.Status != PlatformAuthorityStatus.NotAccepted || admissionLostContinuity)
            {
                var quarantinedMapping = new PlatformRegionMapping(
                    mappingIdentity,
                    new PlatformRegionMappingGeneration(1), binding,
                    slice.Region.Handle, slice.Access);
                AddOriginalMappingRecordLocked(domainRecord, quarantinedMapping.MappingId,
                    new MappingRecord(quarantinedMapping, default,
                        authorityCapabilityId, providerIncarnation, backendEpoch)
                    {
                        ClosureState = PlatformExternalClosureState.Faulted,
                    });
                _exactMappingSlices.Add(quarantinedMapping.MappingId, slice);
                QuarantineDomain(domainRecord);
                retainReservation = true;
            }

            if (admissionLostContinuity)
                return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(KernelError.PlatformFaulted,
                    "Exact mapping admission crossed a backend generation or parent-domain transition; local reservation remains pinned.");

            return FromProviderFailure<PlatformOwnedRegionSliceMapping>(
                providerResult.Status,
                providerResult.Message);
            }
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
                    mappingIdentity,
                    new PlatformRegionMappingGeneration(1), binding,
                    slice.Region.Handle, slice.Access);
                AddOriginalMappingRecordLocked(domainRecord, quarantinedMapping.MappingId,
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
        MappingRecord mappingRecord;
        lock (_secureDomainLifecycleGate)
        {
            mapping = new PlatformRegionMapping(
                mappingIdentity,
                new PlatformRegionMappingGeneration(1), binding,
                slice.Region.Handle, slice.Access);
            mappingRecord = new MappingRecord(mapping, providerMapping.Lease,
                authorityCapabilityId, providerIncarnation, backendEpoch)
            {
                LocalAuthorizationRevoked = true, // Cleanup fact until final local admission.
            };
            if (!resultValidation.IsSuccess ||
                !_domains.TryGetValue(binding.BindingId, out var routed) || !ReferenceEquals(routed, domainRecord) ||
                BackendEpoch != backendEpoch ||
                !ValidateDomain(binding, expectedSubject).IsSuccess)
                mappingRecord.ClosureState = PlatformExternalClosureState.Faulted;
            AddOriginalMappingRecordLocked(domainRecord, mapping.MappingId, mappingRecord);
            _exactMappingSlices.Add(mapping.MappingId, slice);
        }

        // Preserve the exact returned lease before a callback that may fail. An
        // unavailable query refuses publication, not the known cleanup tuple.
        PlatformProviderIncarnation publicationIncarnation = default;
        var sourceAvailable = false;
        try { publicationIncarnation = CurrentProviderIncarnation(); sourceAvailable = true; }
        catch (Exception exception) when (exception is not StackOverflowException) { }
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !_domains.TryGetValue(binding.BindingId, out var routed) || !ReferenceEquals(routed, domainRecord) ||
                !ValidateDomain(binding, expectedSubject).IsSuccess ||
                (sourceAvailable && publicationIncarnation != providerIncarnation))
                mappingRecord.ClosureState = PlatformExternalClosureState.Faulted;
            if (sourceAvailable && mappingRecord.ClosureState == PlatformExternalClosureState.Active)
                mappingRecord.LocalAuthorizationRevoked = false;
            else
            {
                // Faulted lifecycle itself forbids use; preserve V1 fault errors.
                if (mappingRecord.ClosureState == PlatformExternalClosureState.Faulted)
                    mappingRecord.LocalAuthorizationRevoked = false;
                retainReservation = true;
            }
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
        MappingRecord record;
        PlatformRegionSlice slice;
        lock (_secureDomainLifecycleGate)
        {
        var validation = ValidateExactMapping(mapping, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                validation.Error,
                validation.Message!);
        }

        record = _mappings[mapping.Mapping.MappingId];
        slice = _exactMappingSlices[mapping.Mapping.MappingId];
        }
        if (_provider is not IPlatformRegionVisibilityProvider visibilityProvider)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose mapping-bound memory visibility semantics.");
        }

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

        PlatformAuthorityResult<PlatformRegionVisibilityResult> providerResult;
        PlatformProviderIncarnation incarnation;
        try { incarnation = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                "Visibility admission source unavailable; mapping cleanup remains retained.");
        }
        lock (_secureDomainLifecycleGate)
        {
            var source = ValidateVisibilitySourceLocked(mapping, expectedSubject, record, incarnation);
            if (!source.IsSuccess) return KernelResult<PlatformRegionVisibilityEvidence>.Fail(source.Error, source.Message!);
        }
        try { providerResult = visibilityProvider.PrepareRegionMappingForConsumer(request); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                "Visibility invocation outcome unavailable; mapping cleanup remains retained.");
        }
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status == PlatformAuthorityStatus.Revoked)
                _ = RevokeExactMappingLocalAuthorization(mapping.Mapping, expectedSubject);

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

        try { incarnation = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                "Visibility reply source unavailable; evidence refused and mapping cleanup retained.");
        }
        lock (_secureDomainLifecycleGate)
        {
        var publication = ValidateVisibilitySourceLocked(mapping, expectedSubject, record, incarnation);
        if (!publication.IsSuccess)
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(publication.Error, publication.Message!);
        return KernelResult<PlatformRegionVisibilityEvidence>.Ok(
            new PlatformRegionVisibilityEvidence(
                mapping,
                result.Consumer,
                result.Requirement,
                result.Outcome));
        }
    }

    private KernelResult ValidateVisibilitySourceLocked(PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity subject, MappingRecord record, PlatformProviderIncarnation incarnation)
    {
        if (_backendEpochExhausted || BackendEpoch != record.BackendEpoch ||
            incarnation.Value == 0 || incarnation != record.ProviderIncarnation ||
            !_mappings.TryGetValue(mapping.Mapping.MappingId, out var current) || !ReferenceEquals(current, record))
        {
            record.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Visibility lost its exact mapping/provider/backend source; backing remains pinned.");
        }
        return ValidateExactMapping(mapping, subject);
    }

    // Caller holds the existing Region gate before the bridge gate. No provider callback.
    internal KernelResult ValidateVisibilityPublication(
        PlatformOwnedRegionSliceMapping mapping, PlatformDomainIdentity subject, RegionAuthority regions)
    {
        lock (_secureDomainLifecycleGate)
        {
            var usable = regions.ValidatePlatformMappingRegionUsabilityLocked(mapping.Region,
                new RegionOwner(subject.DomainId, subject.ProcessGeneration));
            return usable.IsSuccess ? ValidateExactMapping(mapping, subject) : usable;
        }
    }

    internal KernelResult ValidateExactMapping(
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_secureDomainLifecycleGate)
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
    }

    internal void ForgetExactMappingMetadata(PlatformRegionMapping mapping)
    {
        lock (_secureDomainLifecycleGate) _exactMappingSlices.Remove(mapping.MappingId);
    }
}
