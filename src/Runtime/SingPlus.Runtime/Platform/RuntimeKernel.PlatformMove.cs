using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public readonly record struct PlatformMoveTargetMappingRequest(
    PlatformDomainBinding Binding,
    CapabilityId CapabilityId,
    PlatformMemoryAccess Access);

public enum PlatformMoveTargetExposureState
{
    NotRequested = 0,
    ExactMappedAndPublished,
    LocalOwnershipFallback,
}

public readonly record struct PlatformOwnedBufferMoveResult<T>(
    OwnedBuffer<T> Buffer,
    PlatformMoveTargetExposureState TargetExposure,
    PlatformOwnedRegionSliceMapping? TargetMapping,
    PlatformRegionVisibilityEvidence? TargetPublication,
    KernelError TargetExposureError)
    where T : unmanaged;

public sealed partial class RuntimeKernel
{
    private readonly HashSet<PlatformRegionMappingId> _movePublishedMappings = [];

    public KernelResult<PlatformOwnedBufferMoveResult<T>> MovePlatformOwnedBuffer<T>(
        ProcessHandle source,
        ProcessHandle target,
        OwnedBuffer<T> buffer,
        PlatformOwnedRegionSliceMapping sourceMapping,
        PlatformMoveTargetMappingRequest? targetMapping = null)
        where T : unmanaged
    {
        var sourceProcessResult = Processes.Resolve(source);
        if (!sourceProcessResult.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                sourceProcessResult.Error,
                sourceProcessResult.Message!);
        }

        var targetProcessResult = Processes.Resolve(target);
        if (!targetProcessResult.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                targetProcessResult.Error,
                targetProcessResult.Message!);
        }

        var sourceProcess = sourceProcessResult.Value!;
        var targetProcess = targetProcessResult.Value!;
        var sourceEffect = EnsureProcessAcceptsNewEffects(sourceProcess);
        if (!sourceEffect.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                sourceEffect.Error,
                sourceEffect.Message!);
        }

        var targetEffect = EnsureProcessAcceptsNewEffects(targetProcess);
        if (!targetEffect.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                targetEffect.Error,
                targetEffect.Message!);
        }

        var sourceIdentity = new PlatformDomainIdentity(
            sourceProcess.DomainId,
            source.Generation);
        var targetIdentity = new PlatformDomainIdentity(
            targetProcess.DomainId,
            target.Generation);
        if (sourceIdentity == targetIdentity)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.PlatformDenied,
                "Mapped MOVE handoff requires two distinct local platform subjects.");
        }

        if (buffer.Handle != sourceMapping.Region)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.StaleGeneration,
                "The owned buffer generation does not match the exact source mapping.");
        }

        var mappingValidation = PlatformAuthority.ValidateExactMapping(
            sourceMapping,
            sourceIdentity);
        if (!mappingValidation.IsSuccess &&
            mappingValidation.Error != KernelError.PlatformBindingDraining &&
            mappingValidation.Error != KernelError.PlatformBindingRevoked)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                mappingValidation.Error,
                mappingValidation.Message!);
        }

        if (targetMapping is { } requestedTarget)
        {
            var targetPrerequisites = ValidateMoveTargetMappingPrerequisites(
                target,
                targetProcess,
                requestedTarget,
                sourceMapping.Region.RegionId);
            if (!targetPrerequisites.IsSuccess)
            {
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                    targetPrerequisites.Error,
                    targetPrerequisites.Message!);
            }
        }

        var lifecycleResult = PlatformAuthority.QueryRegionMappingLifecycle(
            sourceMapping.Mapping,
            sourceIdentity);
        if (!lifecycleResult.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                lifecycleResult.Error,
                lifecycleResult.Message!);
        }

        var lifecycle = lifecycleResult.Value!;
        if (lifecycle.PlatformClosure == PlatformExternalClosureState.Active)
        {
            var publication = PreparePlatformRegionMappingForConsumer(
                source,
                sourceMapping,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence);
            if (!publication.IsSuccess)
            {
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                    publication.Error,
                    publication.Message!);
            }

            _movePublishedMappings.Add(sourceMapping.Mapping.MappingId);
            var begin = PlatformAuthority.BeginRegionMappingRevocation(
                sourceMapping.Mapping,
                sourceIdentity,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke);
            if (!begin.IsSuccess)
            {
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                    begin.Error,
                    begin.Message!);
            }

            lifecycle = begin.Value!;
        }
        else if (lifecycle.PlatformClosure == PlatformExternalClosureState.Draining)
        {
            if (!_movePublishedMappings.Contains(sourceMapping.Mapping.MappingId))
            {
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                    KernelError.PlatformDenied,
                    "The draining mapping was not started by this MOVE handoff and cannot be adopted as transfer proof.");
            }

            var observed = PlatformAuthority.ObserveRegionMappingRevocation(
                sourceMapping.Mapping,
                sourceIdentity);
            if (!observed.IsSuccess)
            {
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                    observed.Error,
                    observed.Message!);
            }

            lifecycle = observed.Value!;
        }

        if (lifecycle.PlatformClosure == PlatformExternalClosureState.Faulted)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.PlatformFaulted,
                "The source exact mapping faulted during MOVE drain; ownership remains unchanged.");
        }

        if (lifecycle.PlatformClosure != PlatformExternalClosureState.Closed)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.PlatformBindingDraining,
                "The source exact mapping is still draining; ownership remains with the source.");
        }

        if (!_movePublishedMappings.Contains(sourceMapping.Mapping.MappingId))
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.PlatformDenied,
                "MOVE closure is missing source publication evidence for this exact mapping.");
        }

        if ((sourceMapping.Access & PlatformMemoryAccess.Write) != 0)
        {
            var acquire = PlatformAuthority.AcquireClosedRegionMappingFromConsumer(
                sourceMapping,
                sourceIdentity,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryAcquireRequirement.AcquisitionFence);
            if (!acquire.IsSuccess)
            {
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                    acquire.Error,
                    acquire.Message!);
            }
        }

        var finalize = FinalizePlatformRegionMappingClosure(
            sourceMapping.Mapping,
            sourceIdentity,
            lifecycle);
        if (!finalize.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                finalize.Error,
                finalize.Message!);
        }

        _movePublishedMappings.Remove(sourceMapping.Mapping.MappingId);

        var moved = TransferRegion(source, target, buffer);
        if (!moved.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                moved.Error,
                moved.Message!);
        }

        if (targetMapping is not { } targetRequest)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Ok(
                new PlatformOwnedBufferMoveResult<T>(
                    moved.Value!,
                    PlatformMoveTargetExposureState.NotRequested,
                    null,
                    null,
                    KernelError.None));
        }

        var mappedTarget = MapPlatformOwnedRegionSlice(
            target,
            targetRequest.Binding,
            targetRequest.CapabilityId,
            moved.Value!.Handle,
            sourceMapping.Offset,
            sourceMapping.Length,
            targetRequest.Access);
        if (!mappedTarget.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Ok(
                new PlatformOwnedBufferMoveResult<T>(
                    moved.Value,
                    PlatformMoveTargetExposureState.LocalOwnershipFallback,
                    null,
                    null,
                    mappedTarget.Error));
        }

        var targetPublication = PreparePlatformRegionMappingForConsumer(
            target,
            mappedTarget.Value!,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        if (!targetPublication.IsSuccess)
        {
            _ = RevokePlatformRegionMapping(target, mappedTarget.Value!);
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Ok(
                new PlatformOwnedBufferMoveResult<T>(
                    moved.Value,
                    PlatformMoveTargetExposureState.LocalOwnershipFallback,
                    null,
                    null,
                    targetPublication.Error));
        }

        return KernelResult<PlatformOwnedBufferMoveResult<T>>.Ok(
            new PlatformOwnedBufferMoveResult<T>(
                moved.Value,
                PlatformMoveTargetExposureState.ExactMappedAndPublished,
                mappedTarget.Value,
                targetPublication.Value,
                KernelError.None));
    }

    private KernelResult ValidateMoveTargetMappingPrerequisites(
        ProcessHandle target,
        SingPlus.Sip.SingProcess targetProcess,
        PlatformMoveTargetMappingRequest request,
        RegionId regionId)
    {
        if (!IsValidPlatformAccess(request.Access))
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "Target platform memory access must be Read, Write, or Read|Write.");
        }

        var identity = new PlatformDomainIdentity(
            targetProcess.DomainId,
            target.Generation);
        var binding = PlatformAuthority.ValidateDomain(request.Binding, identity);
        if (!binding.IsSuccess) return binding;

        var requiredRights = CapabilityRights.Map;
        if ((request.Access & PlatformMemoryAccess.Read) != 0)
            requiredRights |= CapabilityRights.Read;
        if ((request.Access & PlatformMemoryAccess.Write) != 0)
            requiredRights |= CapabilityRights.Write;

        var capability = CapabilityAuthority.Validate(
            request.CapabilityId,
            targetProcess.DomainId,
            target.Generation,
            requiredRights);
        if (!capability.IsSuccess) return capability;

        var descriptor = capability.Value!;
        if (descriptor.ResourceKind != ResourceKind.MemoryRegion ||
            !string.Equals(
                descriptor.ResourceId,
                CapabilityResourceIds.MemoryRegion(regionId),
                StringComparison.Ordinal))
        {
            return KernelResult.Fail(
                KernelError.WrongCapabilityResource,
                "The target mapping capability does not authorize the moved region.");
        }

        return KernelResult.Ok();
    }
}
