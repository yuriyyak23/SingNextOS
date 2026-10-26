using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal readonly record struct V6PlatformDmaExecution(
    PlatformDmaSubmission Submission,
    DmaExecutionBindingV1 Binding);

internal readonly record struct V6PlatformDmaCopyExecution(
    V6PlatformDmaExecution Source,
    V6PlatformDmaExecution Destination);

internal readonly record struct V6PlatformDmaCopyCompletion(
    PlatformDmaCompletionEvidence SourceCompletion,
    PlatformDmaCompletionEvidence DestinationCompletion,
    PlatformDmaPostCompletionVisibilityEvidence SourceVisibility,
    PlatformDmaPostCompletionVisibilityEvidence DestinationVisibility);

public sealed partial class RuntimeKernel
{
    internal KernelResult<DmaGrantClosureObservationV1> QueryV6PlatformDmaGrantClosure(
        ProcessHandle subject, PlatformDmaGrant grant)
    {
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> resolved = Processes.Resolve(subject);
            return resolved.IsSuccess
                ? PlatformAuthority.QueryDmaGrantClosureObservation(grant, PlatformIdentity(resolved.Value!))
                : KernelResult<DmaGrantClosureObservationV1>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult<V6PlatformDmaExecution> SubmitV6PlatformDma(
        ProcessHandle subject, PlatformDmaGrant grant, PlatformDmaPrepareEvidence prepareEvidence,
        ISemanticTraceSinkV1? traceSink = null)
    {
        PlatformAuthorityBridge.DmaTraceObserver? trace = null;
        try
        {
            SingPlus.Sip.SingProcess process;
            PlatformDomainIdentity identity;
            ulong mutation;
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> resolved = Processes.Resolve(subject);
                if (!resolved.IsSuccess) return KernelResult<V6PlatformDmaExecution>.Fail(resolved.Error, resolved.Message!);
                process = resolved.Value!;
                KernelResult ready = EnsureProcessAcceptsNewEffects(process);
                if (!ready.IsSuccess) return KernelResult<V6PlatformDmaExecution>.Fail(ready.Error, ready.Message!);
                identity = PlatformIdentity(process);
                KernelResult usable = ValidateDmaGrantRegionUsability(grant, identity);
                if (!usable.IsSuccess) return KernelResult<V6PlatformDmaExecution>.Fail(usable.Error, usable.Message!);
                KernelResult<RegionDescriptor> region = Regions.Validate(grant.Mapping.Region, new RegionOwner(identity.DomainId, subject.Generation));
                if (!region.IsSuccess) return KernelResult<V6PlatformDmaExecution>.Fail(region.Error, region.Message!);
                mutation = region.Value!.MutationEpoch.Value;
            }
            KernelResult<(PlatformDmaSubmission Submission, DmaExecutionBindingV1 Binding)> submitted = PlatformAuthority.SubmitDmaGrantBound(grant, prepareEvidence, identity, mutation, traceSink, commit =>
            {
                lock (_platformMemoryUseGate)
                {
                    KernelResult<SingProcess> current = Processes.Resolve(subject);
                    if (!current.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(current.Error, current.Message!);
                    if (!ReferenceEquals(current.Value, process))
                        return KernelResult<PlatformDmaSubmission>.Fail(KernelError.StaleGeneration, "Bound DMA process incarnation changed.");
                    KernelResult ready = EnsureProcessAcceptsNewEffects(process);
                    if (!ready.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(ready.Error, ready.Message!);
                    RegionOwner owner = new RegionOwner(identity.DomainId, subject.Generation);
                    KernelResult<object> gate = Regions.GetPlatformMappingCompletionGate(grant.Mapping.Region, owner);
                    if (!gate.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(gate.Error, gate.Message!);
                    lock (gate.Value!)
                    {
                        KernelResult usable = Regions.ValidatePlatformMappingRegionUsability(grant.Mapping.Region, owner, mutation);
                        return usable.IsSuccess ? commit() : KernelResult<PlatformDmaSubmission>.Fail(usable.Error, usable.Message!);
                    }
                }
            }, out trace);
            return submitted.IsSuccess
                ? KernelResult<V6PlatformDmaExecution>.Ok(new(submitted.Value.Submission, submitted.Value.Binding))
                : KernelResult<V6PlatformDmaExecution>.Fail(submitted.Error, submitted.Message!);
        }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
    }
    internal KernelResult<V6PlatformDmaCopyExecution> SubmitV6PlatformDmaCopy(
        ProcessHandle sourceSubject, PlatformDmaGrant sourceGrant, PlatformDmaPrepareEvidence sourcePrepare,
        ProcessHandle destinationSubject, PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare,
        ISemanticTraceSinkV1? sourceSink = null, ISemanticTraceSinkV1? destinationSink = null,
        PlatformDmaCopyBacking? backing = null, Action? effectBoundary = null, Action? noEffectBoundary = null)
    {
        PlatformAuthorityBridge.DmaTraceObserver? sourceTrace = null, destinationTrace = null;
        try
        {
            (PlatformDomainIdentity Subject, ulong MutationGeneration) source, destination;
            SingPlus.Sip.SingProcess sourceProcess, destinationProcess;
            lock (_platformMemoryUseGate)
            {
                KernelResult<(PlatformDomainIdentity Subject, ulong MutationGeneration)> resolvedSource = ResolveDmaCopyLeg(sourceSubject, sourceGrant);
                if (!resolvedSource.IsSuccess) return KernelResult<V6PlatformDmaCopyExecution>.Fail(resolvedSource.Error, resolvedSource.Message!);
                KernelResult<(PlatformDomainIdentity Subject, ulong MutationGeneration)> resolvedDestination = ResolveDmaCopyLeg(destinationSubject, destinationGrant);
                if (!resolvedDestination.IsSuccess) return KernelResult<V6PlatformDmaCopyExecution>.Fail(resolvedDestination.Error, resolvedDestination.Message!);
                source = resolvedSource.Value;
                destination = resolvedDestination.Value;
                sourceProcess = Processes.Resolve(sourceSubject).Value!;
                destinationProcess = Processes.Resolve(destinationSubject).Value!;
            }
            KernelResult<PlatformDmaCopyPairSubmission> submitted = PlatformAuthority.SubmitDmaCopyPairBound(
                sourceGrant, sourcePrepare, source.Subject, source.MutationGeneration,
                destinationGrant, destinationPrepare, destination.Subject, destination.MutationGeneration, commit =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        KernelResult<SingProcess> currentSource = Processes.Resolve(sourceSubject);
                        if (!currentSource.IsSuccess) return Fail(currentSource.Error, currentSource.Message!);
                        KernelResult<SingProcess> currentDestination = Processes.Resolve(destinationSubject);
                        if (!currentDestination.IsSuccess) return Fail(currentDestination.Error, currentDestination.Message!);
                        if (!ReferenceEquals(currentSource.Value, sourceProcess) || !ReferenceEquals(currentDestination.Value, destinationProcess))
                            return Fail(KernelError.StaleGeneration, "DMA copy process incarnation changed.");
                        KernelResult ready = EnsureProcessAcceptsNewEffects(sourceProcess);
                        if (!ready.IsSuccess) return Fail(ready.Error, ready.Message!);
                        ready = EnsureProcessAcceptsNewEffects(destinationProcess);
                        if (!ready.IsSuccess) return Fail(ready.Error, ready.Message!);
                        RegionOwner sourceOwner = new RegionOwner(source.Subject.DomainId, sourceSubject.Generation);
                        RegionOwner destinationOwner = new RegionOwner(destination.Subject.DomainId, destinationSubject.Generation);
                        KernelResult<object> sourceGate = Regions.GetPlatformMappingCompletionGate(sourceGrant.Mapping.Region, sourceOwner);
                        if (!sourceGate.IsSuccess) return Fail(sourceGate.Error, sourceGate.Message!);
                        KernelResult<object> destinationGate = Regions.GetPlatformMappingCompletionGate(destinationGrant.Mapping.Region, destinationOwner);
                        if (!destinationGate.IsSuccess) return Fail(destinationGate.Error, destinationGate.Message!);
                        // Existing Region owner gates in ascending RegionId order, including a shared Region.
                        bool sourceFirst = sourceGrant.Mapping.Region.RegionId.Value <= destinationGrant.Mapping.Region.RegionId.Value;
                        lock (sourceFirst ? sourceGate.Value! : destinationGate.Value!)
                        lock (sourceFirst ? destinationGate.Value! : sourceGate.Value!)
                        {
                                KernelResult usable = Regions.ValidatePlatformMappingRegionUsability(sourceGrant.Mapping.Region, sourceOwner, source.MutationGeneration);
                            if (!usable.IsSuccess) return Fail(usable.Error, usable.Message!);
                            usable = Regions.ValidatePlatformMappingRegionUsability(destinationGrant.Mapping.Region, destinationOwner, destination.MutationGeneration);
                            return usable.IsSuccess ? commit() : Fail(usable.Error, usable.Message!);
                        }
                    }
                }, sourceSink, destinationSink, out sourceTrace, out destinationTrace,backing,effectBoundary,noEffectBoundary);
            return submitted.IsSuccess
                ? KernelResult<V6PlatformDmaCopyExecution>.Ok(new(new(submitted.Value.Source, submitted.Value.SourceBinding),
                    new(submitted.Value.Destination, submitted.Value.DestinationBinding)))
                : KernelResult<V6PlatformDmaCopyExecution>.Fail(submitted.Error, submitted.Message!);
            static KernelResult<PlatformDmaCopyPairSubmission> Fail(KernelError error, string message) =>
                KernelResult<PlatformDmaCopyPairSubmission>.Fail(error, message);
        }
        finally
        {
            PlatformAuthority.FlushDmaTrace(sourceTrace);
            PlatformAuthority.FlushDmaTrace(destinationTrace);
        }
    }
    internal KernelResult<V6PlatformDmaCopyExecution> GetActiveV6PlatformDmaCopy(
        ProcessHandle sourceSubject,
        PlatformDmaGrant sourceGrant,
        ProcessHandle destinationSubject,
        PlatformDmaGrant destinationGrant)
    {
        KernelResult<SingProcess> source = Processes.Resolve(sourceSubject);
        if (!source.IsSuccess)
            return KernelResult<V6PlatformDmaCopyExecution>.Fail(source.Error, source.Message!);
        KernelResult<SingProcess> destination = Processes.Resolve(destinationSubject);
        if (!destination.IsSuccess)
            return KernelResult<V6PlatformDmaCopyExecution>.Fail(
                destination.Error, destination.Message!);
        KernelResult<PlatformDmaCopyPairSubmission> active = PlatformAuthority.GetActiveDmaCopyPairBound(
            sourceGrant, PlatformIdentity(source.Value!),
            destinationGrant, PlatformIdentity(destination.Value!));
        return active.IsSuccess
            ? KernelResult<V6PlatformDmaCopyExecution>.Ok(new(
                new(active.Value!.Source, active.Value.SourceBinding),
                new(active.Value.Destination, active.Value.DestinationBinding)))
            : KernelResult<V6PlatformDmaCopyExecution>.Fail(active.Error, active.Message!);
    }

    internal KernelResult<V6PlatformDmaCopyCompletion> CompleteV6PlatformDmaCopy(
        ProcessHandle sourceSubject,
        ProcessHandle destinationSubject,
        V6PlatformDmaCopyExecution execution)
    {
        KernelResult<PlatformDmaCompletionEvidence> sourceCompletion = GetOrObservePlatformDmaCompletion(
            sourceSubject, execution.Source.Submission);
        if (!sourceCompletion.IsSuccess)
            return KernelResult<V6PlatformDmaCopyCompletion>.Fail(
                sourceCompletion.Error, sourceCompletion.Message!);
        KernelResult<PlatformDmaCompletionEvidence> destinationCompletion = GetOrObservePlatformDmaCompletion(
            destinationSubject, execution.Destination.Submission);
        if (!destinationCompletion.IsSuccess)
            return KernelResult<V6PlatformDmaCopyCompletion>.Fail(
                destinationCompletion.Error, destinationCompletion.Message!);

        // Do not close either pending lifetime until completion of both copy legs is proven.
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> sourceVisibility = GetOrFinalizePlatformDmaCopyVisibility(sourceSubject,
            execution.Source.Submission, sourceCompletion.Value!);
        if (!sourceVisibility.IsSuccess)
            return KernelResult<V6PlatformDmaCopyCompletion>.Fail(
                sourceVisibility.Error, sourceVisibility.Message!);
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> destinationVisibility = GetOrFinalizePlatformDmaCopyVisibility(destinationSubject,
            execution.Destination.Submission, destinationCompletion.Value!);
        if (!destinationVisibility.IsSuccess)
            return KernelResult<V6PlatformDmaCopyCompletion>.Fail(
                destinationVisibility.Error, destinationVisibility.Message!);
        return KernelResult<V6PlatformDmaCopyCompletion>.Ok(new(sourceCompletion.Value,
            destinationCompletion.Value, sourceVisibility.Value!, destinationVisibility.Value!));
    }

    private KernelResult<(PlatformDomainIdentity Subject, ulong MutationGeneration)> ResolveDmaCopyLeg(
        ProcessHandle subject, PlatformDmaGrant grant)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<(PlatformDomainIdentity, ulong)>.Fail(resolved.Error, resolved.Message!);
        SingProcess process = resolved.Value!;
        KernelResult effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
            return KernelResult<(PlatformDomainIdentity, ulong)>.Fail(effect.Error, effect.Message!);
        KernelResult usable = ValidateDmaGrantRegionUsability(grant, PlatformIdentity(process));
        if (!usable.IsSuccess)
            return KernelResult<(PlatformDomainIdentity, ulong)>.Fail(usable.Error, usable.Message!);
        KernelResult<RegionDescriptor> region = Regions.Validate(grant.Mapping.Region,
            new RegionOwner(process.DomainId, subject.Generation));
        return region.IsSuccess
            ? KernelResult<(PlatformDomainIdentity, ulong)>.Ok((PlatformIdentity(process),
                region.Value!.MutationEpoch.Value))
            : KernelResult<(PlatformDomainIdentity, ulong)>.Fail(region.Error, region.Message!);
    }
}
