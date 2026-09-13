using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private enum CrossMechanismMappingUseState
    {
        None = 0,
        Active,
        Draining,
        Faulted,
    }

    /// <summary>
    /// Validates the exact DSC1 mapping inputs before RuntimeKernel reserves or
    /// snapshots managed buffers. This is bridge-private policy admission, not
    /// provider authority and not a coherence result.
    /// </summary>
    internal KernelResult ValidateDsc1MappingUseAdmission(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformDsc1RegionRange source,
        PlatformDsc1RegionRange destination)
    {
        lock (_dsc1Gate)
        {
            return ValidateDsc1MappingUseAdmissionLocked(
                binding,
                expectedSubject,
                source,
                destination);
        }
    }

    private KernelResult ValidateDsc1MappingUseAdmissionLocked(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformDsc1RegionRange source,
        PlatformDsc1RegionRange destination)
    {
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess) return bindingValidation;

        var sourceValidation = ValidateDsc1LocalRange(
            binding,
            expectedSubject,
            source,
            PlatformMemoryAccess.Read,
            "source");
        if (!sourceValidation.IsSuccess) return sourceValidation;

        var destinationValidation = ValidateDsc1LocalRange(
            binding,
            expectedSubject,
            destination,
            PlatformMemoryAccess.Write,
            "destination");
        if (!destinationValidation.IsSuccess) return destinationValidation;

        if (source.Length != destination.Length)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DSC1 Copy source and destination ranges must have equal byte lengths.");
        }

        if (source.Mapping.Region.RegionId == destination.Mapping.Region.RegionId)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DSC1 Copy v1 requires disjoint source and destination owned regions.");
        }

        var sourceConflict = ClassifyDmaMappingUse(source.Mapping);
        var destinationConflict = ClassifyDmaMappingUse(destination.Mapping);
        return CrossMechanismAdmissionResult(
            MostSevere(sourceConflict, destinationConflict),
            "DMA",
            "DSC1 Copy");
    }

    /// <summary>
    /// Called only while the RuntimeKernel platform-memory-use gate and the
    /// bridge DSC1 gate are held. The exact DMA grant has already passed all
    /// identity, authority and prepared-cycle validation.
    /// </summary>
    private KernelResult ValidateDmaMappingUseAdmissionLocked(
        PlatformDmaGrant grant)
    {
        var state = CrossMechanismMappingUseState.None;
        foreach (var record in _dsc1Operations.Values)
        {
            if (record.LocalReservationsReleased ||
                (record.Submission.Source.Mapping != grant.Mapping.Mapping &&
                 record.Submission.Destination.Mapping != grant.Mapping.Mapping))
            {
                continue;
            }

            var candidate = record.State switch
            {
                Dsc1OperationState.Faulted => CrossMechanismMappingUseState.Faulted,
                Dsc1OperationState.ClosedCompleted or
                Dsc1OperationState.ClosedCancelled =>
                    CrossMechanismMappingUseState.Draining,
                _ when record.CancellationRequested =>
                    CrossMechanismMappingUseState.Draining,
                _ => CrossMechanismMappingUseState.Active,
            };
            state = MostSevere(state, candidate);
        }

        return CrossMechanismAdmissionResult(state, "DSC1 Copy", "DMA");
    }

    private CrossMechanismMappingUseState ClassifyDmaMappingUse(
        PlatformRegionMapping mapping)
    {
        lock (_dmaCompletionGate)
        {
            var state = CrossMechanismMappingUseState.None;
            foreach (var grantRecord in _dmaGrants.Values)
            {
                var grant = grantRecord.Grant;
                if (grantRecord.PlatformClosed || grant.Mapping.Mapping != mapping)
                    continue;

                if (_dmaSubmissionFaultPins.Contains(grant.GrantId))
                {
                    state = CrossMechanismMappingUseState.Faulted;
                    continue;
                }

                if (!_activeDmaSubmissions.TryGetValue(
                        grant.GrantId,
                        out var submission))
                {
                    continue;
                }

                var candidate = submission.CompletionProven ||
                                submission.CompletionObservationInFlight
                    ? CrossMechanismMappingUseState.Draining
                    : CrossMechanismMappingUseState.Active;
                state = MostSevere(state, candidate);
            }

            return state;
        }
    }

    private static CrossMechanismMappingUseState MostSevere(
        CrossMechanismMappingUseState left,
        CrossMechanismMappingUseState right) =>
        (CrossMechanismMappingUseState)Math.Max((int)left, (int)right);

    private static KernelResult CrossMechanismAdmissionResult(
        CrossMechanismMappingUseState state,
        string existingMechanism,
        string requestedMechanism) => state switch
        {
            CrossMechanismMappingUseState.None => KernelResult.Ok(),
            CrossMechanismMappingUseState.Active => KernelResult.Fail(
                KernelError.PlatformBindingActive,
                $"The exact platform region mapping has an active {existingMechanism} use and cannot admit {requestedMechanism}."),
            CrossMechanismMappingUseState.Draining => KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                $"The exact platform region mapping is draining {existingMechanism} use and cannot admit {requestedMechanism}."),
            _ => KernelResult.Fail(
                KernelError.PlatformFaulted,
                $"The exact platform region mapping is fault-pinned by {existingMechanism} use and cannot admit {requestedMechanism}."),
        };
}
