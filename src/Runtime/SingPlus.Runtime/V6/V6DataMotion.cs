using System.Runtime.CompilerServices;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal sealed record V6DataMotionExecution<T>(OwnedBuffer<T> Moved, DataMotionReceiptV1 Receipt)
    where T : unmanaged;

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
        var sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var region = Regions.Validate(input.Handle,
            new RegionOwner(sourceProcess.Value!.DomainId, source.Generation));
        if (!region.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(region.Error, region.Message!);

        var sourceEstimate = costProvider.QueryFresh(sourceProviderClass, nowUnixMilliseconds);
        if (!sourceEstimate.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceEstimate.Error, sourceEstimate.Message!);
        var destinations = new List<LocalityCostEstimateV1>(destinationProviderClasses.Count);
        foreach (var providerClass in destinationProviderClasses)
        {
            var estimate = costProvider.QueryFresh(providerClass, nowUnixMilliseconds);
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
            var current = costProvider.CurrentGeneration(providerClass);
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
        if (currentSourceEstimateGeneration() != plan.SourceEstimateGeneration ||
            currentDestinationEstimateGeneration() != plan.DestinationEstimateGeneration)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.StaleGeneration,
                "Locality cost evidence changed before data-motion execution.");
        var sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var destinationProcess = Processes.Resolve(destination);
        if (!destinationProcess.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(destinationProcess.Error, destinationProcess.Message!);
        var owner = new RegionOwner(sourceProcess.Value!.DomainId, source.Generation);
        var region = Regions.Validate(input.Handle, owner);
        if (!region.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(region.Error, region.Message!);
        ulong exactBytes;
        try { exactBytes = checked((ulong)input.Length * (ulong)Unsafe.SizeOf<T>()); }
        catch (OverflowException)
        { return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.InvalidMessage, "Data-motion byte length overflowed."); }
        if (plan.SourceRegionGeneration != input.Handle.Generation.Value ||
            plan.SourceMutationGeneration != region.Value!.MutationEpoch.Value ||
            plan.ByteLength != exactBytes || plan.MaximumTemporaryChargeBytes < exactBytes)
            return KernelResult<V6DataMotionExecution<T>>.Fail(KernelError.StaleGeneration,
                "Data-motion plan no longer matches the exact source Region tuple.");

        var output = AllocateBuffer<T>(destination, input.Length);
        if (!output.IsSuccess) return KernelResult<V6DataMotionExecution<T>>.Fail(output.Error, output.Message!);
        var outputBuffer = output.Value!;
        RegionUseDescriptor? pin = null;
        RuntimeBufferLease<T>? bufferLease = null;
        try
        {
            if (currentSourceEstimateGeneration() != plan.SourceEstimateGeneration ||
                currentDestinationEstimateGeneration() != plan.DestinationEstimateGeneration)
                return FailAndRelease(KernelError.StaleGeneration, "Locality cost evidence changed at the final copy sentry.");
            var acquired = Regions.AcquireUse(input.Handle, owner, RegionUseMode.ExclusiveRead,
                new RegionUseRange(0, checked((long)exactBytes)));
            if (!acquired.IsSuccess) return FailAndRelease(acquired.Error, acquired.Message!);
            pin = acquired.Value!;
            try { bufferLease = input.ReserveForRuntime(RuntimeBufferAccess.Read); }
            catch (InvalidOperationException exception)
            { return FailAndRelease(KernelError.RegionUseConflict, exception.Message); }
            bufferLease.ReadOnlySpan.CopyTo(outputBuffer.Span);
            bufferLease.Dispose();
            bufferLease = null;
            var releasedPin = Regions.ReleaseUse(pin.Handle, owner);
            pin = null;
            if (!releasedPin.IsSuccess) return FailAndRelease(releasedPin.Error, releasedPin.Message!);
            var releasedSource = ReleaseRegion(source, input);
            if (!releasedSource.IsSuccess) return FailAndRelease(releasedSource.Error, releasedSource.Message!);
            var receipt = new DataMotionReceiptV1(1,
                $"move:{input.Handle.RegionId.Value}:{input.Handle.Generation.Value}:{outputBuffer.Handle.RegionId.Value}",
                plan, source.Generation, destination.Generation, outputBuffer.Handle.Generation.Value,
                [DataMotionLifecycleStateV1.Planned, DataMotionLifecycleStateV1.SourcePinned,
                 DataMotionLifecycleStateV1.Copied, DataMotionLifecycleStateV1.SourceReleased,
                 DataMotionLifecycleStateV1.Completed]).Validate();
            return KernelResult<V6DataMotionExecution<T>>.Ok(new(outputBuffer, receipt));
        }
        finally
        {
            bufferLease?.Dispose();
            if (pin is { } remaining) _ = Regions.ReleaseUse(remaining.Handle, owner);
        }

        KernelResult<V6DataMotionExecution<T>> FailAndRelease(KernelError error, string message)
        {
            _ = ReleaseRegion(destination, outputBuffer);
            return KernelResult<V6DataMotionExecution<T>>.Fail(error, message);
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
        Func<ulong> currentDestinationEstimateGeneration) where T : unmanaged
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

        var sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var destinationProcess = Processes.Resolve(destination);
        if (!destinationProcess.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(destinationProcess.Error, destinationProcess.Message!);
        var sourceRegion = Regions.Validate(input.Handle,
            new RegionOwner(sourceProcess.Value!.DomainId, source.Generation));
        if (!sourceRegion.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceRegion.Error, sourceRegion.Message!);
        var destinationRegion = Regions.Validate(output.Handle,
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

        var submitted = SubmitV6PlatformDmaCopy(source, sourceGrant, sourcePrepare,
            destination, destinationGrant, destinationPrepare);
        V6PlatformDmaCopyExecution execution;
        if (submitted.IsSuccess)
            execution = submitted.Value!;
        else if (submitted.Error == KernelError.PlatformBindingActive)
        {
            var active = GetActiveV6PlatformDmaCopy(
                source, sourceGrant, destination, destinationGrant);
            if (!active.IsSuccess)
                return KernelResult<V6DataMotionExecution<T>>.Fail(active.Error, active.Message!);
            execution = active.Value!;
        }
        else
            return KernelResult<V6DataMotionExecution<T>>.Fail(submitted.Error, submitted.Message!);
        var completed = CompleteV6PlatformDmaCopy(source, destination, execution);
        if (!completed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(completed.Error, completed.Message!);

        var sourceGrantClosed = RevokePlatformDma(source, sourceGrant);
        if (!sourceGrantClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceGrantClosed.Error, sourceGrantClosed.Message!);
        var destinationGrantClosed = RevokePlatformDma(destination, destinationGrant);
        if (!destinationGrantClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(destinationGrantClosed.Error, destinationGrantClosed.Message!);
        var sourceMappingClosed = RevokePlatformRegionMapping(source, sourceMapping);
        if (!sourceMappingClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(sourceMappingClosed.Error, sourceMappingClosed.Message!);
        var destinationMappingClosed = RevokePlatformRegionMapping(destination, destinationMapping);
        if (!destinationMappingClosed.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(destinationMappingClosed.Error,
                destinationMappingClosed.Message!);
        var releasedSource = ReleaseRegion(source, input);
        if (!releasedSource.IsSuccess)
            return KernelResult<V6DataMotionExecution<T>>.Fail(releasedSource.Error, releasedSource.Message!);

        var receipt = new DataMotionReceiptV1(1,
            $"dma-move:{input.Handle.RegionId.Value}:{input.Handle.Generation.Value}:{output.Handle.RegionId.Value}",
            plan, source.Generation, destination.Generation, output.Handle.Generation.Value,
            [DataMotionLifecycleStateV1.Planned, DataMotionLifecycleStateV1.SourcePinned,
             DataMotionLifecycleStateV1.Copied, DataMotionLifecycleStateV1.SourceReleased,
             DataMotionLifecycleStateV1.Completed]).Validate();
        return KernelResult<V6DataMotionExecution<T>>.Ok(new(output, receipt));

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
