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
        MappingRecord record;
        PlatformRegionSlice slice;
        PlatformRegionAcquireRequest request;
        IPlatformRegionAcquireProvider acquireProvider;
        lock (_secureDomainLifecycleGate)
        {
            var identityValidation = ValidateClosedExactMappingIdentity(mapping, expectedSubject);
            if (!identityValidation.IsSuccess)
            {
                return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                    identityValidation.Error,
                    identityValidation.Message!);
            }

            record = _mappings[mapping.Mapping.MappingId];
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

            var visibilityFeature = _featureManifest.Resolve(PlatformFeatureFamily.ExplicitMemoryVisibility);
            if (visibilityFeature.ContractVersion < PlatformRegionAcquireContract.ContractVersion ||
                visibilityFeature.Availability is not
                    (PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable))
            {
                return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                    KernelError.PlatformUnsupported,
                    "The platform provider does not advertise executable mapping acquire v3 semantics.");
            }

            if (_provider is not IPlatformRegionAcquireProvider resolvedAcquireProvider)
            {
                return KernelResult<PlatformRegionAcquireEvidence>.Fail(
                    KernelError.PlatformUnsupported,
                    "The platform provider does not expose post-close mapping acquire semantics.");
            }

            acquireProvider = resolvedAcquireProvider;

            slice = _exactMappingSlices[mapping.Mapping.MappingId];
            request = new PlatformRegionAcquireRequest(
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

            if (record.AcquireInFlight || BackendEpoch != record.BackendEpoch || _backendEpochExhausted)
                return KernelResult<PlatformRegionAcquireEvidence>.Fail(KernelError.PlatformBindingDraining,
                    "Exact mapping acquire admission is unavailable before provider callback.");
            if (record.AcquireUncertain || record.AcquireRequest is { } originalRequest && originalRequest != request)
                return KernelResult<PlatformRegionAcquireEvidence>.Fail(KernelError.PlatformFaulted,
                    "Original acquire is unresolved or belongs to a different request; repeat effect refused.");
            record.AcquireRequest = request;
            record.AcquireInFlight = true;
        }
        try
        {
        PlatformAuthorityResult<PlatformRegionAcquireResult> providerResult;
        try { providerResult = record.AcquireObservation ?? acquireProvider.AcquireRegionMappingFromConsumer(request); }
        catch
        {
            lock (_secureDomainLifecycleGate) record.AcquireUncertain = true;
            throw;
        }
        lock (_secureDomainLifecycleGate) record.AcquireObservation = providerResult;
        if (!providerResult.IsSuccess)
        {
            lock (_secureDomainLifecycleGate) record.AcquireUncertain = true;
            return FromProviderFailure<PlatformRegionAcquireEvidence>(
                providerResult.Status,
                providerResult.Message);
        }

        var result = providerResult.Value!;
        var resultValidation = PlatformRegionAcquireContract.ValidateResult(request, result);
        if (!resultValidation.IsSuccess)
        {
            lock (_secureDomainLifecycleGate) record.AcquireUncertain = true;
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

        // Source observation invokes provider code and must stay outside the owner gate.
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(KernelError.PlatformFaulted,
                "Original acquire reply retained while provider source observation is unavailable.");
        }
        lock (_secureDomainLifecycleGate)
        {
        if (!_mappings.TryGetValue(mapping.Mapping.MappingId, out var current) || !ReferenceEquals(current, record) ||
            !ValidateClosedExactMappingIdentity(mapping, expectedSubject).IsSuccess ||
            !_exactMappingSlices.TryGetValue(mapping.Mapping.MappingId, out var currentSlice) || currentSlice != slice ||
            record.ClosureState != PlatformExternalClosureState.Closed || record.LocalReservationReleased ||
            source.Value == 0 || source != record.ProviderIncarnation || BackendEpoch != record.BackendEpoch)
            return KernelResult<PlatformRegionAcquireEvidence>.Fail(KernelError.PlatformFaulted,
                "Original acquire reply retained without current exact mapping/source/reservation publication.");

        return KernelResult<PlatformRegionAcquireEvidence>.Ok(
            new PlatformRegionAcquireEvidence(
                mapping,
                result.Producer,
                result.Requirement,
                result.Outcome));
        }
        }
        finally { lock (_secureDomainLifecycleGate) record.AcquireInFlight = false; }
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
