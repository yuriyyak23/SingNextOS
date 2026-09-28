using SingPlus.Contracts;
using SingPlus.Platform;

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
    internal KernelResult<V6PlatformDmaExecution> SubmitV6PlatformDma(
        ProcessHandle subject,
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence,
        ISemanticTraceSinkV1? traceSink = null)
    {
        KernelResult<V6PlatformDmaExecution> result;
        PlatformAuthorityBridge.DmaTraceObserver? trace = null;
        lock (_platformMemoryUseGate)
        {
            result = SubmitV6PlatformDmaLocked(subject, grant, prepareEvidence, traceSink,
                out trace);
            if (result.IsSuccess && traceSink is not null)
                trace = PlatformAuthority.CaptureDmaTrace(result.Value!.Submission);
        }
        PlatformAuthority.FlushDmaTrace(trace);
        return result;
    }

    private KernelResult<V6PlatformDmaExecution> SubmitV6PlatformDmaLocked(
        ProcessHandle subject, PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence,
        ISemanticTraceSinkV1? traceSink,
        out PlatformAuthorityBridge.DmaTraceObserver? ambiguityTrace)
    {
        ambiguityTrace = null;
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<V6PlatformDmaExecution>.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
            return KernelResult<V6PlatformDmaExecution>.Fail(effect.Error, effect.Message!);
        var region = Regions.Validate(grant.Mapping.Region,
            new RegionOwner(process.DomainId, subject.Generation));
        if (!region.IsSuccess)
            return KernelResult<V6PlatformDmaExecution>.Fail(region.Error, region.Message!);
        var submitted = PlatformAuthority.SubmitDmaGrantBound(grant, prepareEvidence,
            PlatformIdentity(process), region.Value!.MutationEpoch.Value, traceSink,
            out ambiguityTrace);
        return submitted.IsSuccess
            ? KernelResult<V6PlatformDmaExecution>.Ok(new(
                submitted.Value.Submission, submitted.Value.Binding))
            : KernelResult<V6PlatformDmaExecution>.Fail(submitted.Error, submitted.Message!);
    }

    internal KernelResult<V6PlatformDmaCopyExecution> SubmitV6PlatformDmaCopy(
        ProcessHandle sourceSubject,
        PlatformDmaGrant sourceGrant,
        PlatformDmaPrepareEvidence sourcePrepare,
        ProcessHandle destinationSubject,
        PlatformDmaGrant destinationGrant,
        PlatformDmaPrepareEvidence destinationPrepare)
    {
        lock (_platformMemoryUseGate)
        {
            var source = ResolveDmaCopyLeg(sourceSubject, sourceGrant);
            if (!source.IsSuccess)
                return KernelResult<V6PlatformDmaCopyExecution>.Fail(source.Error, source.Message!);
            var destination = ResolveDmaCopyLeg(destinationSubject, destinationGrant);
            if (!destination.IsSuccess)
                return KernelResult<V6PlatformDmaCopyExecution>.Fail(destination.Error, destination.Message!);
            var submitted = PlatformAuthority.SubmitDmaCopyPairBound(
                sourceGrant, sourcePrepare, source.Value!.Subject, source.Value.MutationGeneration,
                destinationGrant, destinationPrepare, destination.Value!.Subject,
                destination.Value.MutationGeneration);
            return submitted.IsSuccess
                ? KernelResult<V6PlatformDmaCopyExecution>.Ok(new(
                    new(submitted.Value.Source, submitted.Value.SourceBinding),
                    new(submitted.Value.Destination, submitted.Value.DestinationBinding)))
                : KernelResult<V6PlatformDmaCopyExecution>.Fail(submitted.Error, submitted.Message!);
        }
    }

    internal KernelResult<V6PlatformDmaCopyExecution> GetActiveV6PlatformDmaCopy(
        ProcessHandle sourceSubject,
        PlatformDmaGrant sourceGrant,
        ProcessHandle destinationSubject,
        PlatformDmaGrant destinationGrant)
    {
        var source = Processes.Resolve(sourceSubject);
        if (!source.IsSuccess)
            return KernelResult<V6PlatformDmaCopyExecution>.Fail(source.Error, source.Message!);
        var destination = Processes.Resolve(destinationSubject);
        if (!destination.IsSuccess)
            return KernelResult<V6PlatformDmaCopyExecution>.Fail(
                destination.Error, destination.Message!);
        var active = PlatformAuthority.GetActiveDmaCopyPairBound(
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
        var sourceCompletion = GetOrObservePlatformDmaCompletion(
            sourceSubject, execution.Source.Submission);
        if (!sourceCompletion.IsSuccess)
            return KernelResult<V6PlatformDmaCopyCompletion>.Fail(
                sourceCompletion.Error, sourceCompletion.Message!);
        var destinationCompletion = GetOrObservePlatformDmaCompletion(
            destinationSubject, execution.Destination.Submission);
        if (!destinationCompletion.IsSuccess)
            return KernelResult<V6PlatformDmaCopyCompletion>.Fail(
                destinationCompletion.Error, destinationCompletion.Message!);

        // Do not close either pending lifetime until completion of both copy legs is proven.
        var sourceVisibility = FinalizePlatformDmaPostCompletionVisibility(sourceSubject,
            execution.Source.Submission, sourceCompletion.Value!);
        if (!sourceVisibility.IsSuccess)
            return KernelResult<V6PlatformDmaCopyCompletion>.Fail(
                sourceVisibility.Error, sourceVisibility.Message!);
        var destinationVisibility = FinalizePlatformDmaPostCompletionVisibility(destinationSubject,
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
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<(PlatformDomainIdentity, ulong)>.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
            return KernelResult<(PlatformDomainIdentity, ulong)>.Fail(effect.Error, effect.Message!);
        var region = Regions.Validate(grant.Mapping.Region,
            new RegionOwner(process.DomainId, subject.Generation));
        return region.IsSuccess
            ? KernelResult<(PlatformDomainIdentity, ulong)>.Ok((PlatformIdentity(process),
                region.Value!.MutationEpoch.Value))
            : KernelResult<(PlatformDomainIdentity, ulong)>.Fail(region.Error, region.Message!);
    }
}
