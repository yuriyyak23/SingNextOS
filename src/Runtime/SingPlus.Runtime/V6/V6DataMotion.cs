using System.Runtime.CompilerServices;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal sealed record V6DataMotionExecution<T>(OwnedBuffer<T> Moved, DataMotionReceiptV1 Receipt)
    where T : unmanaged
{
    internal ProviderDmaDataMotionResult<T>? PreparedProviderResult { get; init; }
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<V6DataMotionExecution<T>> ExecuteV6ProviderObservedHostDataMotion<T>(
        ProcessHandle source,
        ProcessHandle destination,
        OwnedBuffer<T> input,
        string sourceProviderClass,
        IReadOnlyList<string> destinationProviderClasses,
        long nowUnixMilliseconds,
        ulong maximumTemporaryChargeBytes,
        IV6LocalityCostEvidenceProvider costProvider) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(destinationProviderClasses);
        ArgumentNullException.ThrowIfNull(costProvider);
        KernelResult<SingProcess> sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        KernelResult<RegionDescriptor> region = Regions.Validate(input.Handle,
            new RegionOwner(sourceProcess.Value!.DomainId, source.Generation));
        if (!region.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(region.Error, region.Message!);

        KernelResult<LocalityCostEstimateV1> sourceEstimate = costProvider.QueryFresh(sourceProviderClass, nowUnixMilliseconds);
        if (!sourceEstimate.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceEstimate.Error, sourceEstimate.Message!);
        List<LocalityCostEstimateV1> destinations = new List<LocalityCostEstimateV1>(destinationProviderClasses.Count);
        foreach (string providerClass in destinationProviderClasses)
        {
            KernelResult<LocalityCostEstimateV1> estimate = costProvider.QueryFresh(providerClass, nowUnixMilliseconds);
            if (!estimate.IsSuccess)
                return KernelResult<V6DataMotionExecution<T>>.Fail(estimate.Error, estimate.Message!);
            destinations.Add(estimate.Value);
        }

        DataMotionPlanV1 plan;
        try
        {
            plan = LocalityPlanningPolicyV1.PlanHostMove(sourceEstimate.Value!, destinations,
                nowUnixMilliseconds, input.Handle.Generation.Value,
                region.Value!.MutationEpoch.Value, maximumTemporaryChargeBytes);
        }
        catch (Exception exception) when (exception is ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.PlatformUnavailable,
                exception.Message);
        }

        return ExecuteV6HostDataMotion(source, destination, input, plan,
            () => CurrentOrInvalid(sourceProviderClass),
            () => CurrentOrInvalid(plan.DestinationProviderClass));

        ulong CurrentOrInvalid(string providerClass)
        {
            KernelResult<ulong> current = costProvider.CurrentGeneration(providerClass);
            return current.IsSuccess ? current.Value : 0;
        }
    }

    internal KernelResult<V6DataMotionExecution<T>> ExecuteV6HostDataMotion<T>(
        ProcessHandle source,
        ProcessHandle destination,
        OwnedBuffer<T> input,
        DataMotionPlanV1 plan,
        Func<ulong> currentSourceEstimateGeneration,
        Func<ulong> currentDestinationEstimateGeneration) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(currentSourceEstimateGeneration);
        ArgumentNullException.ThrowIfNull(currentDestinationEstimateGeneration);
        try { plan.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (plan.Mode != DataMotionModeV1.HostCopyThenReleaseSource)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.PlatformUnsupported,
                "Only the explicitly bounded host-copy data-motion contour is implemented.");
        KernelResult<bool> estimates = ValidateEstimates();
        if (!estimates.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(estimates.Error, estimates.Message!);
        if (!estimates.Value)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.StaleGeneration,
                "Locality cost evidence changed before data-motion execution.");
        KernelResult<SingProcess> sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        KernelResult<SingProcess> destinationProcess = Processes.Resolve(destination);
        if (!destinationProcess.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(destinationProcess.Error, destinationProcess.Message!);
        RegionOwner owner = new RegionOwner(sourceProcess.Value!.DomainId, source.Generation);
        KernelResult<RegionDescriptor> region = Regions.Validate(input.Handle, owner);
        if (!region.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(region.Error, region.Message!);
        ulong exactBytes;
        int length;
        try { length=input.Length;exactBytes = checked((ulong)length * (ulong)Unsafe.SizeOf<T>()); }
        catch (InvalidOperationException exception)
        { return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.InvalidRegionState,exception.Message); }
        catch (OverflowException)
        { return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.InvalidMessage, "Data-motion byte length overflowed."); }
        if (plan.SourceRegionGeneration != input.Handle.Generation.Value ||
            plan.SourceMutationGeneration != region.Value!.MutationEpoch.Value ||
            plan.ByteLength != exactBytes || plan.MaximumTemporaryChargeBytes < exactBytes)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.StaleGeneration,
                "Data-motion plan no longer matches the exact source Region tuple.");

        KernelResult<OwnedBuffer<T>> output = AllocateBuffer<T>(destination, length);
        if (!output.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(output.Error, output.Message!);
        OwnedBuffer<T> outputBuffer = output.Value!;
        RegionUseDescriptor? pin = null;
        RuntimeBufferLease<T>? bufferLease = null;
        try
        {
            estimates = ValidateEstimates();
            if (!estimates.IsSuccess) return FailAndRelease(estimates.Error, estimates.Message!);
            if (!estimates.Value)
                return FailAndRelease(KernelError.StaleGeneration, "Locality cost evidence changed at the final copy sentry.");
            KernelResult<RegionUseDescriptor> acquired;
            lock (_platformMemoryUseGate)
            {
                KernelResult accepting = ValidateEffectAdmissionProcess(source);
                if (accepting.IsSuccess) accepting = ValidateDestination();
                acquired = accepting.IsSuccess
                    ? Regions.AcquireUse(input.Handle, owner, RegionUseMode.ExclusiveRead,
                        new RegionUseRange(0, checked((long)exactBytes)))
                    : KernelResult<RegionUseDescriptor>.Fail(accepting.Error, accepting.Message!);
            }
            if (!acquired.IsSuccess) return FailAndRelease(acquired.Error, acquired.Message!);
            pin = acquired.Value!;
            if (pin.MutationEpoch.Value != plan.SourceMutationGeneration)
                return FailAndRelease(KernelError.StaleGeneration,
                    "Source mutation changed before the host-copy read pin was acquired.");
            try { bufferLease = input.ReserveForRuntime(RuntimeBufferAccess.Read); }
            catch (InvalidOperationException exception)
            { return FailAndRelease(KernelError.RegionUseConflict, exception.Message); }
            KernelResult copied;
            lock (_platformMemoryUseGate)
            {
                copied = ValidateEffectAdmissionProcess(source);
                if (copied.IsSuccess) copied = ValidateDestination();
                if (copied.IsSuccess)
                    bufferLease.ReadOnlySpan.CopyTo(outputBuffer.Span);
            }
            if (!copied.IsSuccess) return FailAndRelease(copied.Error, copied.Message!);
            bufferLease.Dispose();
            bufferLease = null;
            KernelResult releasedPin = Regions.ReleaseUse(pin.Handle, owner);
            pin = null;
            if (!releasedPin.IsSuccess) return FailAndRelease(releasedPin.Error, releasedPin.Message!);
            KernelResult releasedSource = ReleaseRegion(source, input);
            if (!releasedSource.IsSuccess) return FailAndRelease(releasedSource.Error, releasedSource.Message!);
            DataMotionReceiptV1 receipt = new DataMotionReceiptV1(1,
                $"move:{input.Handle.RegionId.Value}:{input.Handle.Generation.Value}:{outputBuffer.Handle.RegionId.Value}",
                plan, source.Generation, destination.Generation, outputBuffer.Handle.Generation.Value,
                [DataMotionLifecycleStateV1.Planned, DataMotionLifecycleStateV1.SourcePinned,
                 DataMotionLifecycleStateV1.Copied, DataMotionLifecycleStateV1.SourceReleased,
                 DataMotionLifecycleStateV1.Completed]).Validate();
            KernelResult publication;
            lock (_platformMemoryUseGate)
            {
                publication = ValidateDestination();
                if (publication.IsSuccess)
                    return KernelResult<V6DataMotionExecution<T>>.Ok(new(outputBuffer, receipt));
            }
            return FailAndRelease(publication.Error, publication.Message!);
        }
        finally
        {
            bufferLease?.Dispose();
            if (pin is { } remaining) _ = Regions.ReleaseUse(remaining.Handle, owner);
        }

        KernelResult<V6DataMotionExecution<T>> FailAndRelease(KernelError error, string message)
        {
            KernelResult released = ReleaseRegion(destination, outputBuffer);
            if (!released.IsSuccess)
                message += $" Temporary Region {outputBuffer.Handle} cleanup refused ({released.Error}); original owner facts remain authoritative.";
            return KernelResult<V6DataMotionExecution<T>>.Fail(error, message);
        }

        KernelResult ValidateDestination()
        {
            KernelResult accepting = ValidateEffectAdmissionProcess(destination);
            if (!accepting.IsSuccess) return accepting;
            if (!outputBuffer.IsValid)
                return KernelResult.Fail(KernelError.InvalidRegionState,
                    "Data-motion destination ownership changed before commit.");
            KernelResult<RegionDescriptor> live = Regions.Validate(outputBuffer.Handle,
                new RegionOwner(destinationProcess.Value!.DomainId, destination.Generation));
            return live.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(live.Error, live.Message!);
        }

        KernelResult<bool> ValidateEstimates()
        {
            try
            {
                return KernelResult<bool>.Ok(
                    currentSourceEstimateGeneration() == plan.SourceEstimateGeneration &&
                    currentDestinationEstimateGeneration() == plan.DestinationEstimateGeneration);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult<bool>.Fail(KernelError.PlatformUnavailable,
                    $"Locality cost generation observation failed before copy: {exception.GetType().Name}.");
            }
        }
    }

    internal KernelResult<V6DataMotionExecution<T>> ExecuteV6ProviderDmaDataMotion<T>(
        ProcessHandle source,
        ProcessHandle destination,
        OwnedBuffer<T> input,
        OwnedBuffer<T> output,
        DataMotionPlanV1 plan,
        PlatformOwnedRegionSliceMapping sourceMapping,
        PlatformDmaGrant sourceGrant,
        PlatformDmaPrepareEvidence sourcePrepare,
        PlatformOwnedRegionSliceMapping destinationMapping,
        PlatformDmaGrant destinationGrant,
        PlatformDmaPrepareEvidence destinationPrepare,
        Func<ulong> currentSourceEstimateGeneration,
        Func<ulong> currentDestinationEstimateGeneration,
        ISemanticTraceSinkV1? sourceTraceSink = null, ISemanticTraceSinkV1? destinationTraceSink = null,
        ProviderDmaDataMotionRequest<T>? originalRequest = null) where T : unmanaged
    {
        lock (_platformMemoryUseGate)
        {
            if (_dmaCopyPayloadCalls.Contains(sourceGrant.GrantId) || _dmaCopyPayloadCalls.Contains(destinationGrant.GrantId))
                return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.PlatformBindingDraining,"Original DMA payload caller is already in flight.");
            _dmaCopyPayloadCalls.EnsureCapacity(_dmaCopyPayloadCalls.Count + 2);
            _dmaCopyPayloadCalls.Add(sourceGrant.GrantId); _dmaCopyPayloadCalls.Add(destinationGrant.GrantId);
        }
        try { return ExecuteV6ProviderDmaDataMotionCore(source,destination,input,output,plan,sourceMapping,sourceGrant,sourcePrepare,
            destinationMapping,destinationGrant,destinationPrepare,currentSourceEstimateGeneration,currentDestinationEstimateGeneration,sourceTraceSink,destinationTraceSink,originalRequest); }
        finally
        {
            lock (_platformMemoryUseGate)
            {
                if (_dmaCopyPayloads.TryGetValue(sourceGrant.GrantId,out DmaCopyPayload? payload) && IsOriginalDmaCopyPayload(payload) &&
                    (!payload.EffectPossible || payload.Consumed && !input.IsValid))
                { payload.Dispose(); _dmaCopyPayloads.Remove(sourceGrant.GrantId); }
                _dmaCopyPayloadCalls.Remove(sourceGrant.GrantId); _dmaCopyPayloadCalls.Remove(destinationGrant.GrantId);
            }
        }
    }

    private KernelResult<V6DataMotionExecution<T>> ExecuteV6ProviderDmaDataMotionCore<T>(
        ProcessHandle source, ProcessHandle destination, OwnedBuffer<T> input, OwnedBuffer<T> output, DataMotionPlanV1 plan,
        PlatformOwnedRegionSliceMapping sourceMapping, PlatformDmaGrant sourceGrant, PlatformDmaPrepareEvidence sourcePrepare,
        PlatformOwnedRegionSliceMapping destinationMapping, PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare,
        Func<ulong> currentSourceEstimateGeneration, Func<ulong> currentDestinationEstimateGeneration,
        ISemanticTraceSinkV1? sourceTraceSink, ISemanticTraceSinkV1? destinationTraceSink,
        ProviderDmaDataMotionRequest<T>? originalRequest) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(currentSourceEstimateGeneration);
        ArgumentNullException.ThrowIfNull(currentDestinationEstimateGeneration);
        try { plan.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (plan.Mode != DataMotionModeV1.ProviderDmaThenReleaseSource)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.PlatformUnsupported,
                "The provider-DMA contour requires an explicit provider-DMA data-motion plan.");
        if (currentSourceEstimateGeneration() != plan.SourceEstimateGeneration ||
            currentDestinationEstimateGeneration() != plan.DestinationEstimateGeneration)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.StaleGeneration,
                "Locality cost evidence changed before provider-DMA execution.");

        KernelResult<SingProcess> sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        KernelResult<SingProcess> destinationProcess = Processes.Resolve(destination);
        if (!destinationProcess.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(destinationProcess.Error, destinationProcess.Message!);
        KernelResult<RegionDescriptor> sourceRegion = Regions.Validate(input.Handle,
            new RegionOwner(sourceProcess.Value!.DomainId, source.Generation));
        if (!sourceRegion.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceRegion.Error, sourceRegion.Message!);
        KernelResult<RegionDescriptor> destinationRegion = Regions.Validate(output.Handle,
            new RegionOwner(destinationProcess.Value!.DomainId, destination.Generation));
        if (!destinationRegion.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(destinationRegion.Error, destinationRegion.Message!);

        ulong exactBytes;
        ulong outputBytes;
        try
        {
            exactBytes = checked((ulong)input.Length * (ulong)Unsafe.SizeOf<T>());
            outputBytes = checked((ulong)output.Length * (ulong)Unsafe.SizeOf<T>());
        }
        catch (OverflowException)
        { return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.InvalidMessage,
            "Provider-DMA data-motion byte length overflowed."); }
        catch (InvalidOperationException exception)
        { return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.InvalidRegionState,exception.Message); }
        if (plan.SourceRegionGeneration != input.Handle.Generation.Value ||
            plan.SourceMutationGeneration != sourceRegion.Value!.MutationEpoch.Value ||
            plan.ByteLength != exactBytes || outputBytes != exactBytes ||
            plan.MaximumTemporaryChargeBytes < exactBytes)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.StaleGeneration,
                "Provider-DMA plan no longer matches the exact source and destination Region tuple.");
        if (!ExactLeg(sourceMapping, sourceGrant, input.Handle, exactBytes,
                PlatformMemoryAccess.Read, PlatformDmaDirection.DeviceReadsMemory) ||
            !ExactLeg(destinationMapping, destinationGrant, output.Handle, exactBytes,
                PlatformMemoryAccess.Write, PlatformDmaDirection.DeviceWritesMemory))
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.PlatformDenied,
                "Provider-DMA movement requires exact whole-buffer source-read and destination-write authority.");
        if (currentSourceEstimateGeneration() != plan.SourceEstimateGeneration ||
            currentDestinationEstimateGeneration() != plan.DestinationEstimateGeneration)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.StaleGeneration,
                "Locality cost evidence changed at the final provider-DMA sentry.");

        KernelResult<bool> cleanup = PlatformAuthority.HasCompletedDmaCopyPairForCleanup(sourceGrant, sourcePrepare, PlatformIdentity(sourceProcess.Value!),
            destinationGrant, destinationPrepare, PlatformIdentity(destinationProcess.Value!), plan.SourceMutationGeneration);
        if (!cleanup.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(cleanup.Error, cleanup.Message!);
        KernelResult<DmaCopyPayload> reserved = ReserveDmaCopyPayload(source,destination,input,output,sourceGrant,sourcePrepare,destinationGrant,destinationPrepare,cleanup.Value);
        if (!reserved.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(reserved.Error,reserved.Message!);
        DmaCopyPayload payload = reserved.Value!;
        if(originalRequest is not null)
        {
            lock(originalRequest)
            {
                if(!ReferenceEquals(originalRequest.Original,originalRequest)||!ReferenceEquals(originalRequest.Owner,this)||
                    !originalRequest.InFlight||originalRequest.CleanupPayload is { } prior && !ReferenceEquals(prior,payload))
                    return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.PlatformFaulted,"Original DMA caller custody changed before effect.");
                originalRequest.CleanupPayload=payload;
            }
        }
        if (!cleanup.Value)
        {
            if(originalRequest?.Cancellation.IsCancellationRequested==true)
                return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.CancellationPending,"Original DMA cancelled before Copy; granted resources still require exact cleanup.");
            KernelResult<V6PlatformDmaCopyExecution> submitted = SubmitV6PlatformDmaCopy(source, sourceGrant, sourcePrepare,
                destination, destinationGrant, destinationPrepare, sourceTraceSink, destinationTraceSink,payload.Backing,
                () => payload.EffectPossible = true, () => payload.EffectPossible = false);
            V6PlatformDmaCopyExecution execution;
            if (submitted.IsSuccess)
                execution = submitted.Value!;
            else if (submitted.Error == KernelError.PlatformBindingActive)
            {
                KernelResult<V6PlatformDmaCopyExecution> active = GetActiveV6PlatformDmaCopy(
                    source, sourceGrant, destination, destinationGrant);
                if (!active.IsSuccess)
                    return KernelResult<V6DataMotionExecution<T>>.Fail(active.Error, active.Message!);
                execution = active.Value!;
            }
            else
                return KernelResult<V6DataMotionExecution<T>>.Fail(submitted.Error, submitted.Message!);
            KernelResult<V6PlatformDmaCopyCompletion> completed = CompleteV6PlatformDmaCopy(source, destination, execution);
            if (!completed.IsSuccess)
                return KernelResult<V6DataMotionExecution<T>>.Fail(completed.Error, completed.Message!);
            if(originalRequest?.Cancellation.IsCancellationRequested==true)
                return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.CancellationPending,"Original DMA cancelled after possible Copy; completion protocol observed, payload retained for exact cleanup.");

        }

        KernelResult sourceGrantClosed = CloseGrant(source, sourceGrant, true);
        if (!sourceGrantClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceGrantClosed.Error, sourceGrantClosed.Message!);
        KernelResult destinationGrantClosed = CloseGrant(destination, destinationGrant, false);
        if (!destinationGrantClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(destinationGrantClosed.Error, destinationGrantClosed.Message!);
        KernelResult sourceMappingClosed = CloseMapping(source, sourceMapping);
        if (!sourceMappingClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceMappingClosed.Error, sourceMappingClosed.Message!);
        KernelResult destinationMappingClosed = CloseMapping(destination, destinationMapping);
        if (!destinationMappingClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(destinationMappingClosed.Error,
                destinationMappingClosed.Message!);
        // Exact completion/visibility and both original mapping closures end the
        // provider's byte lifetime. Local publication is a separate fresh commit.
        // Source reclaim ends only source custody. Keep destination access
        // reserved through callbacks until the final fresh publication commit.
        // Allocate and validate the unpublished result before irreversible
        // source consumption. Allocation failure leaves the original retryable.
        DataMotionReceiptV1 receipt = new DataMotionReceiptV1(1,
            $"dma-move:{input.Handle.RegionId.Value}:{input.Handle.Generation.Value}:{output.Handle.RegionId.Value}",
            plan, source.Generation, destination.Generation, output.Handle.Generation.Value,
            [DataMotionLifecycleStateV1.Planned, DataMotionLifecycleStateV1.SourcePinned,
             DataMotionLifecycleStateV1.Copied, DataMotionLifecycleStateV1.SourceReleased,
             DataMotionLifecycleStateV1.Completed]).Validate();
        V6DataMotionExecution<T> preparedResult = new V6DataMotionExecution<T>(output, receipt)
        { PreparedProviderResult = new ProviderDmaDataMotionResult<T>(output, receipt) };
        if(originalRequest?.Cancellation.IsCancellationRequested==true)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.CancellationPending,"Original DMA cancelled before source consumption; cleanup remains separate.");
        lock (_platformMemoryUseGate) payload.ReleaseSourceAfterClosure();
        KernelResult releasedSource = ReleaseRegion(source, input);
        if (!releasedSource.IsSuccess)
        {
            if (!input.IsValid)
            {
                // Reentry may have consumed the source while its release
                // callback ran. Native grants/mappings are already closed;
                // end original destination custody without publishing a result.
                lock (_platformMemoryUseGate)
                    return RefuseClosedPublication(releasedSource.Error,releasedSource.Message!);
            }
            return KernelResult<V6DataMotionExecution<T>>.Fail(releasedSource.Error, releasedSource.Message!);
        }
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> current = Processes.Resolve(destination);
            if(originalRequest?.Cancellation.IsCancellationRequested==true)
                return RefuseClosedPublication(KernelError.CancellationPending,"Original DMA cancelled before final publication after source callbacks.");
            if (!current.IsSuccess) return RefuseClosedPublication(current.Error,current.Message!);
            if (!ReferenceEquals(current.Value,destinationProcess.Value))
                return RefuseClosedPublication(KernelError.StaleGeneration,"Original DMA destination incarnation changed before publication.");
            KernelResult accepts = EnsureProcessAcceptsNewEffects(current.Value!);
            if (!accepts.IsSuccess) return RefuseClosedPublication(accepts.Error,accepts.Message!);
            KernelResult<RegionDescriptor> exact = Regions.Validate(output.Handle,new RegionOwner(current.Value!.DomainId,destination.Generation));
            if (!exact.IsSuccess || !output.IsValid || !IsOriginalDmaCopyPayload(payload) ||
                !_dmaCopyPayloads.TryGetValue(sourceGrant.GrantId,out DmaCopyPayload? retained) || !ReferenceEquals(retained,payload))
                return RefuseClosedPublication(KernelError.InvalidRegionState,"Original DMA output/backing changed before publication.");
            payload.Dispose(); _dmaCopyPayloads.Remove(sourceGrant.GrantId);
            return KernelResult<V6DataMotionExecution<T>>.Ok(preparedResult);
        }

        KernelResult<V6DataMotionExecution<T>> RefuseClosedPublication(KernelError error,string message)
        {
            // Both original grants/mappings already closed above. Refusing a
            // result must not strand consumed destination custody or its owner.
            if(IsOriginalDmaCopyPayload(payload) && _dmaCopyPayloads.TryGetValue(sourceGrant.GrantId,out DmaCopyPayload? original) && ReferenceEquals(original,payload))
            {payload.Dispose();_dmaCopyPayloads.Remove(sourceGrant.GrantId);}
            return KernelResult<V6DataMotionExecution<T>>.Fail(error,message);
        }

        KernelResult CloseGrant(ProcessHandle process, PlatformDmaGrant grant, bool sourceLeg)
        {
            KernelResult closed = RevokePlatformDma(process, grant);
            if (closed.Error != KernelError.PlatformBindingRevoked) return closed;
            KernelResult<PlatformDmaCopyClosureObservation> fact = PlatformAuthority.ObserveDmaCopyClosure(sourceGrant, PlatformIdentity(sourceProcess.Value!),
                destinationGrant, PlatformIdentity(destinationProcess.Value!));
            return fact.IsSuccess && (sourceLeg ? fact.Value.SourceGrantClosed : fact.Value.DestinationGrantClosed)
                ? KernelResult.Ok() : closed;
        }
        KernelResult CloseMapping(ProcessHandle process, PlatformOwnedRegionSliceMapping mapping)
        {
            KernelResult closed = RevokePlatformRegionMapping(process, mapping);
            return closed.Error == KernelError.PlatformBindingRevoked
                ? ObservePlatformRegionMappingRevocation(process, mapping.Mapping) : closed;
        }
        static bool ExactLeg(
            PlatformOwnedRegionSliceMapping mapping,
            PlatformDmaGrant grant,
            RegionHandle region,
            ulong byteLength,
            PlatformMemoryAccess requiredAccess,
            PlatformDmaDirection requiredDirection) =>
            mapping.Region == region && mapping.Offset == 0 &&
            mapping.Length >= 0 && (ulong)mapping.Length == byteLength &&
            (mapping.Access & requiredAccess) == requiredAccess &&
            grant.Mapping == mapping && grant.Direction == requiredDirection &&
            grant.Range.Offset == 0 && grant.Range.Length >= 0 &&
            (ulong)grant.Range.Length == byteLength;
    }
}
