using System.Runtime.CompilerServices;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public readonly record struct PlatformMoveTargetMappingRequest(
    PlatformDomainBinding Binding,
    CapabilityId CapabilityId,
    PlatformMemoryAccess Access);

public readonly record struct PlatformBoundedCopyFallbackPolicy(long MaxBytes);

public readonly record struct PlatformBoundedCopyEvidence(
    RegionHandle Region,
    long ByteLength,
    long MaxBytes)
{
    public bool IsExactAndBounded =>
        ByteLength > 0 && MaxBytes >= ByteLength;
}

public enum PlatformMoveTargetExposureState
{
    NotRequested = 0,
    ExactMappedAndPublished,
    LocalOwnershipFallback,
    BoundedCopyFallback,
}

public readonly record struct PlatformOwnedBufferMoveResult<T>(
    OwnedBuffer<T> Buffer,
    PlatformMoveTargetExposureState TargetExposure,
    PlatformOwnedRegionSliceMapping? TargetMapping,
    PlatformRegionVisibilityEvidence? TargetPublication,
    KernelError TargetExposureError)
    where T : unmanaged
{
    public PlatformBoundedCopyEvidence? BoundedCopy { get; init; }
}

public sealed partial class RuntimeKernel
{
    private sealed class BoundedCopyStagingRegion<T> : IDisposable
        where T : unmanaged
    {
        private T[]? _data;

        internal BoundedCopyStagingRegion(int elementCount, long byteLength, long maxBytes)
        {
            if (elementCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(elementCount));
            if (byteLength <= 0 || maxBytes < byteLength)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            _data = new T[elementCount];
            ByteLength = byteLength;
            MaxBytes = maxBytes;
        }

        internal long ByteLength { get; }
        internal long MaxBytes { get; }

        internal Span<T> Span =>
            _data is { } data
                ? data.AsSpan()
                : throw new ObjectDisposedException(nameof(BoundedCopyStagingRegion<T>));

        public void Dispose()
        {
            if (_data is not { } data) return;
            Array.Clear(data);
            _data = null;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<PlatformRegionMappingId, byte> _movePublishedMappings = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PlatformRegionMappingId, byte> _moveProgressingMappings = new();

    public KernelResult<PlatformOwnedBufferMoveResult<T>> MovePlatformOwnedBuffer<T>(
        ProcessHandle source,
        ProcessHandle target,
        OwnedBuffer<T> buffer,
        PlatformOwnedRegionSliceMapping sourceMapping,
        PlatformMoveTargetMappingRequest? targetMapping = null,
        PlatformBoundedCopyFallbackPolicy? copyFallback = null)
        where T : unmanaged
    {
        PlatformRegionMappingId mappingId = sourceMapping.Mapping.MappingId;
        if (!_moveProgressingMappings.TryAdd(mappingId, 0))
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(KernelError.PlatformBindingDraining,
                "This mapping MOVE is already progressing.");
        try { return MovePlatformOwnedBufferCore(source, target, buffer, sourceMapping, targetMapping, copyFallback); }
        finally { _moveProgressingMappings.TryRemove(mappingId, out _); }
    }

    private KernelResult<PlatformOwnedBufferMoveResult<T>> MovePlatformOwnedBufferCore<T>(
        ProcessHandle source, ProcessHandle target, OwnedBuffer<T> buffer,
        PlatformOwnedRegionSliceMapping sourceMapping,
        PlatformMoveTargetMappingRequest? targetMapping,
        PlatformBoundedCopyFallbackPolicy? copyFallback) where T : unmanaged
    {
        KernelResult<SingProcess> sourceProcessResult = Processes.Resolve(source);
        if (!sourceProcessResult.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                sourceProcessResult.Error,
                sourceProcessResult.Message!);
        }

        KernelResult<SingProcess> targetProcessResult = Processes.Resolve(target);
        if (!targetProcessResult.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                targetProcessResult.Error,
                targetProcessResult.Message!);
        }

        SingProcess sourceProcess = sourceProcessResult.Value!;
        SingProcess targetProcess = targetProcessResult.Value!;
        KernelResult sourceEffect = EnsureProcessAcceptsNewEffects(sourceProcess);
        if (!sourceEffect.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                sourceEffect.Error,
                sourceEffect.Message!);
        }

        KernelResult targetEffect = EnsureProcessAcceptsNewEffects(targetProcess);
        if (!targetEffect.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                targetEffect.Error,
                targetEffect.Message!);
        }

        PlatformDomainIdentity sourceIdentity = PlatformIdentity(sourceProcess);
        PlatformDomainIdentity targetIdentity = PlatformIdentity(targetProcess);
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

        RegionOwner sourceOwner = new RegionOwner(sourceProcess.DomainId, source.Generation);
        KernelResult<RegionDescriptor> sourceRegion = Regions.Validate(buffer.Handle, sourceOwner);
        if (!sourceRegion.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                sourceRegion.Error,
                sourceRegion.Message!);
        }

        long authoritativeByteLength = sourceRegion.Value!.ByteLength;
        long bufferByteLength;
        try
        {
            bufferByteLength = checked((long)buffer.Length * Unsafe.SizeOf<T>());
        }
        catch (OverflowException)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.CapacityExhausted,
                "The moved buffer byte length overflows the bounded-copy accounting range.");
        }

        if (bufferByteLength != authoritativeByteLength)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.PlatformFaulted,
                "The owned buffer storage length does not match RegionAuthority byte length.");
        }

        KernelResult copyPolicyValidation = ValidateBoundedCopyPolicy(
            targetMapping,
            copyFallback,
            authoritativeByteLength);
        if (!copyPolicyValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                copyPolicyValidation.Error,
                copyPolicyValidation.Message!);
        }

        KernelResult mappingValidation = PlatformAuthority.ValidateExactMapping(
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
            KernelResult targetPrerequisites = ValidateMoveTargetMappingPrerequisites(
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

        KernelResult<PlatformRegionMappingLifecycle> lifecycleResult = PlatformAuthority.QueryRegionMappingLifecycle(
            sourceMapping.Mapping,
            sourceIdentity);
        if (!lifecycleResult.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                lifecycleResult.Error,
                lifecycleResult.Message!);
        }

        PlatformRegionMappingLifecycle lifecycle = lifecycleResult.Value!;
        if (lifecycle.PlatformClosure == PlatformExternalClosureState.Active)
        {
            KernelResult<PlatformRegionVisibilityEvidence> publication = PreparePlatformRegionMappingForConsumer(
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

            _movePublishedMappings.TryAdd(sourceMapping.Mapping.MappingId, 0);
            KernelResult<PlatformRegionMappingLifecycle> begin = PlatformAuthority.BeginRegionMappingRevocation(
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
            if (!_movePublishedMappings.ContainsKey(sourceMapping.Mapping.MappingId))
            {
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                    KernelError.PlatformDenied,
                    "The draining mapping was not started by this MOVE handoff and cannot be adopted as transfer proof.");
            }

            KernelResult<PlatformRegionMappingLifecycle> observed = PlatformAuthority.ObserveRegionMappingRevocation(
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

        if (!_movePublishedMappings.ContainsKey(sourceMapping.Mapping.MappingId))
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                KernelError.PlatformDenied,
                "MOVE closure is missing source publication evidence for this exact mapping.");
        }

        if ((sourceMapping.Access & PlatformMemoryAccess.Write) != 0 && !lifecycle.LocalReservationReleased)
        {
            KernelResult<PlatformRegionAcquireEvidence> acquire = PlatformAuthority.AcquireClosedRegionMappingFromConsumer(
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

        KernelResult finalize = FinalizePlatformRegionMappingClosure(
            sourceMapping.Mapping,
            sourceIdentity,
            lifecycle);
        if (!finalize.IsSuccess)
        {
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                finalize.Error,
                finalize.Message!);
        }

        KernelResult<OwnedBuffer<T>> moved = TransferRegion(source, target, buffer);
        if (!moved.IsSuccess)
        {
            if (!buffer.IsValid)
                _movePublishedMappings.TryRemove(sourceMapping.Mapping.MappingId, out _);
            return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(
                moved.Error,
                moved.Message!);
        }
        _movePublishedMappings.TryRemove(sourceMapping.Mapping.MappingId, out _);

        if (targetMapping is not { } targetRequest)
        {
            return PublishResult(
                new PlatformOwnedBufferMoveResult<T>(
                    moved.Value!,
                    PlatformMoveTargetExposureState.NotRequested,
                    null,
                    null,
                    KernelError.None));
        }

        KernelResult<PlatformOwnedRegionSliceMapping> mappedTarget = MapPlatformOwnedRegionSlice(
            target,
            targetRequest.Binding,
            targetRequest.CapabilityId,
            moved.Value!.Handle,
            sourceMapping.Offset,
            sourceMapping.Length,
            targetRequest.Access);
        if (!mappedTarget.IsSuccess)
        {
            if (copyFallback is { } fallbackPolicy)
            {
                KernelResult<(OwnedBuffer<T> Buffer, PlatformBoundedCopyEvidence Evidence)> rematerialized = RematerializeMovedBufferThroughBoundedCopy(
                    target,
                    targetProcess,
                    moved.Value,
                    authoritativeByteLength,
                    fallbackPolicy);
                if (rematerialized.IsSuccess)
                {
                    (OwnedBuffer<T> Buffer, PlatformBoundedCopyEvidence Evidence) copy = rematerialized.Value!;
                    return PublishResult(
                        new PlatformOwnedBufferMoveResult<T>(
                            copy.Buffer,
                            PlatformMoveTargetExposureState.BoundedCopyFallback,
                            null,
                            null,
                            mappedTarget.Error)
                        {
                            BoundedCopy = copy.Evidence,
                        });
                }

                return PublishResult(
                    new PlatformOwnedBufferMoveResult<T>(
                        moved.Value,
                        PlatformMoveTargetExposureState.LocalOwnershipFallback,
                        null,
                        null,
                        rematerialized.Error));
            }

            return PublishResult(
                new PlatformOwnedBufferMoveResult<T>(
                    moved.Value,
                    PlatformMoveTargetExposureState.LocalOwnershipFallback,
                    null,
                    null,
                    mappedTarget.Error));
        }

        KernelResult<PlatformRegionVisibilityEvidence> targetPublication = PreparePlatformRegionMappingForConsumer(
            target,
            mappedTarget.Value!,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        if (!targetPublication.IsSuccess)
        {
            KernelResult cleanup = RevokePlatformRegionMapping(target, mappedTarget.Value!);
            if (!cleanup.IsSuccess)
                return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(targetPublication.Error,
                    $"{targetPublication.Message} Target mapping {mappedTarget.Value!.Mapping.MappingId} cleanup refused ({cleanup.Error}); exact owner facts remain retained.");
            return PublishResult(
                new PlatformOwnedBufferMoveResult<T>(
                    moved.Value,
                    PlatformMoveTargetExposureState.LocalOwnershipFallback,
                    null,
                    null,
                    targetPublication.Error));
        }

        return PublishResult(
            new PlatformOwnedBufferMoveResult<T>(
                moved.Value,
                PlatformMoveTargetExposureState.ExactMappedAndPublished,
                mappedTarget.Value,
                targetPublication.Value,
                KernelError.None));

        KernelResult<PlatformOwnedBufferMoveResult<T>> PublishResult(PlatformOwnedBufferMoveResult<T> result)
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult ready = ValidateTransferredPublication(target, result.Buffer.Handle);
                if (!ready.IsSuccess)
                    return KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(ready.Error, ready.Message!);
                return result.Buffer.IsValid
                    ? KernelResult<PlatformOwnedBufferMoveResult<T>>.Ok(result)
                    : KernelResult<PlatformOwnedBufferMoveResult<T>>.Fail(KernelError.InvalidRegionState,
                        "MOVE target ownership changed before usable publication.");
            }
        }
    }

    private static KernelResult ValidateBoundedCopyPolicy(
        PlatformMoveTargetMappingRequest? targetMapping,
        PlatformBoundedCopyFallbackPolicy? policy,
        long authoritativeByteLength)
    {
        if (policy is null) return KernelResult.Ok();

        if (targetMapping is null)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "Bounded-copy fallback requires an explicit target mapping request.");
        }

        if (policy.Value.MaxBytes <= 0)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "Bounded-copy fallback requires a positive maximum byte bound.");
        }

        if (authoritativeByteLength > policy.Value.MaxBytes)
        {
            return KernelResult.Fail(
                KernelError.CapacityExhausted,
                "The authoritative moved region exceeds the bounded-copy fallback limit.");
        }

        return KernelResult.Ok();
    }

    internal Action? BeforeBoundedCopyRematerializationQualificationHook { get; set; }

    private KernelResult<(OwnedBuffer<T> Buffer, PlatformBoundedCopyEvidence Evidence)>
        RematerializeMovedBufferThroughBoundedCopy<T>(
            ProcessHandle target,
            SingProcess targetProcess,
            OwnedBuffer<T> moved,
            long authoritativeByteLength,
            PlatformBoundedCopyFallbackPolicy policy)
        where T : unmanaged
    {
        BeforeBoundedCopyRematerializationQualificationHook?.Invoke();
        lock (_platformMemoryUseGate)
        {
            KernelResult ready = ValidateTransferredPublication(target, moved.Handle);
            if (!ready.IsSuccess)
                return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(ready.Error, ready.Message!);
            KernelResult<object> gate = Regions.GetPlatformMappingCompletionGate(moved.Handle,
                new RegionOwner(targetProcess.DomainId, target.Generation));
            if (!gate.IsSuccess)
                return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(gate.Error, gate.Message!);
            lock (gate.Value!)
                return RematerializeMovedBufferThroughBoundedCopyCore(target, targetProcess, moved, authoritativeByteLength, policy);
        }
    }

    private KernelResult<(OwnedBuffer<T> Buffer, PlatformBoundedCopyEvidence Evidence)>
        RematerializeMovedBufferThroughBoundedCopyCore<T>(ProcessHandle target,
            SingProcess targetProcess, OwnedBuffer<T> moved, long authoritativeByteLength,
            PlatformBoundedCopyFallbackPolicy policy) where T : unmanaged
    {
        RegionOwner targetOwner = new RegionOwner(targetProcess.DomainId, target.Generation);
        KernelResult<RegionDescriptor> regionValidation = Regions.Validate(moved.Handle, targetOwner);
        if (!regionValidation.IsSuccess)
        {
            return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(
                regionValidation.Error,
                regionValidation.Message!);
        }

        if (regionValidation.Value!.ByteLength != authoritativeByteLength)
        {
            return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(
                KernelError.StaleGeneration,
                "The target region byte length changed before bounded-copy rematerialization.");
        }

        if (PlatformAuthority.HasActiveMapping(moved.Handle))
        {
            return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(
                KernelError.PlatformBindingActive,
                "Bounded-copy rematerialization is forbidden while a target platform mapping remains active.");
        }

        if (authoritativeByteLength <= 0 || authoritativeByteLength > policy.MaxBytes)
        {
            return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(
                KernelError.CapacityExhausted,
                "The target region no longer fits the prevalidated bounded-copy limit.");
        }

        KernelResult<RegionUseDescriptor> pin = Regions.AcquireUse(moved.Handle, targetOwner, RegionUseMode.ExclusiveRead,
            new(0, authoritativeByteLength));
        if (!pin.IsSuccess)
            return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(pin.Error, pin.Message!);
        try
        {
            RuntimeBufferLease<T> lease;
            try { lease = moved.ReserveForRuntime(RuntimeBufferAccess.Read); }
            catch (InvalidOperationException exception)
            { return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(KernelError.RegionUseConflict, exception.Message); }
            using RuntimeBufferLease<T> bufferLease = lease;
            using BoundedCopyStagingRegion<T> staging = new BoundedCopyStagingRegion<T>(
                moved.Length,
                authoritativeByteLength,
                policy.MaxBytes);
            bufferLease.ReadOnlySpan.CopyTo(staging.Span);

            T[] replacementData = new T[moved.Length];
            staging.Span.CopyTo(replacementData);
            OwnedBuffer<T> replacement = new OwnedBuffer<T>(moved.Handle, replacementData);
            bufferLease.Dispose();

            Regions.ReplacePayload(
                moved.Handle,
                moved.Handle,
                (ITransferableOwnedPayload)replacement);
            ((ITransferableOwnedPayload)moved).InvalidateForRuntime();

            PlatformBoundedCopyEvidence evidence = new PlatformBoundedCopyEvidence(
                replacement.Handle,
                staging.ByteLength,
                staging.MaxBytes);
            return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Ok(
                (replacement, evidence));
        }
        catch (OutOfMemoryException)
        {
            return KernelResult<(OwnedBuffer<T>, PlatformBoundedCopyEvidence)>.Fail(
                KernelError.CapacityExhausted,
                "The bounded-copy staging or replacement backing could not be allocated.");
        }
        finally { _ = Regions.ReleaseUse(pin.Value!.Handle, targetOwner); }
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

        PlatformDomainIdentity identity = PlatformIdentity(targetProcess);
        KernelResult binding = PlatformAuthority.ValidateDomain(request.Binding, identity);
        if (!binding.IsSuccess) return binding;

        CapabilityRights requiredRights = CapabilityRights.Map;
        if ((request.Access & PlatformMemoryAccess.Read) != 0)
            requiredRights |= CapabilityRights.Read;
        if ((request.Access & PlatformMemoryAccess.Write) != 0)
            requiredRights |= CapabilityRights.Write;

        KernelResult<CapabilityDescriptorV1> capability = CapabilityAuthority.Validate(
            request.CapabilityId,
            targetProcess.DomainId,
            target.Generation,
            requiredRights);
        if (!capability.IsSuccess)
        {
            return KernelResult.Fail(
                capability.Error,
                capability.Message ?? "The target mapping capability is invalid.");
        }

        CapabilityDescriptorV1 descriptor = capability.Value!;
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
