using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider
{
    public PlatformAuthorityResult<PlatformExecutionTerminalObservation> ResolveExecutionTerminal(
        PlatformProviderChildDomainLease childLease)
    {
        if (_childRuntime is not INeutralExecutionTerminalProvider producer)
            return PlatformAuthorityResult<PlatformExecutionTerminalObservation>.Fail(PlatformAuthorityStatus.Unsupported, "Typed terminal producer is unavailable.");
        var parent = ValidateDomain(childLease.ParentDomainLease);
        if (!parent.IsSuccess) return PlatformAuthorityResult<PlatformExecutionTerminalObservation>.Fail(parent.Status, parent.Message!);
        ExecutionAttempt attempt;
        lock (_childCreateCommitGate)
        {
            var candidates = _originalProviderArtifacts.Values.Where(artifact => artifact.Receipt.ChildLeaseId == childLease.LeaseId &&
                artifact.Receipt.ChildGeneration == childLease.Generation && artifact.ExecutionPublished && !artifact.Released).ToArray();
            if (candidates.Length != 1 || candidates[0].OriginalExecution is not { } fact ||
                !TerminalSources(fact, childLease))
                return PlatformAuthorityResult<PlatformExecutionTerminalObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original published Start is unavailable.");
            attempt = fact;
        }
        var raw = producer.ResolveExecutionTerminal(attempt.Request);
        lock (_childCreateCommitGate) attempt.TerminalObservation ??= raw; // Preserve first original raw before parent/source callbacks.
        var currentParent = ValidateDomain(childLease.ParentDomainLease);
        lock (_childCreateCommitGate)
        {
            if (!currentParent.IsSuccess || !TerminalSources(attempt, childLease) || !raw.IsSuccess ||
                raw.Value.Request != attempt.Request || !Enum.IsDefined(raw.Value.Kind) ||
                attempt.TerminalObservation is { IsSuccess: true } originalTerminal && raw.Value != originalTerminal.Value ||
                attempt.Observation is not { IsSuccess: true } admitted || raw.Value.Receipt != admitted.Value ||
                !NeutralChildExecutionContract.ValidateExecutionReceipt(attempt.Request, raw.Value.Receipt).IsSuccess)
                return PlatformAuthorityResult<PlatformExecutionTerminalObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original typed terminal observation is unknown, foreign or stale.");
            var value = raw.Value.Receipt;
            var artifact = attempt.Source.Receipt;
            var receipt = new PlatformChildExecutionReceipt(artifact.ArtifactId, artifact.ArtifactGeneration,
                artifact.ChildLeaseId, artifact.ChildGeneration, artifact.MappingLeaseId, artifact.MappingGeneration,
                artifact.ParentLeaseId, artifact.ParentGeneration, artifact.ContentDigest,
                new(attempt.Request.OperationId.Value), new(attempt.Request.OperationGeneration.Value),
                new(value.ExecutionGeneration.Value), value.RetiredWorkUnits, value.RetiredSequence,
                value.LastRetiredCodeOffset, value.IsTerminal);
            return PlatformAuthorityResult<PlatformExecutionTerminalObservation>.Ok(new(receipt, (PlatformExecutionTerminalKind)(byte)raw.Value.Kind)
            { ProcessExitCode = raw.Value.ProcessExitCode });
        }
    }

    private bool TerminalSources(ExecutionAttempt attempt, PlatformProviderChildDomainLease lease) =>
        ReferenceEquals(attempt.OwnerSelf, attempt) && OriginalExecutionHistoryIntact(attempt.Source) &&
        ReferenceEquals(attempt.Source.OriginalExecution, attempt) && attempt.Source.ExecutionPublished &&
        !attempt.Source.ExecutionInFlight && !attempt.Source.ReleaseInvoked && !attempt.Source.Released && attempt.Source.ReleaseRequest is null &&
        _providerArtifacts.TryGetValue(attempt.Source.Receipt.ArtifactId, out var artifact) && ReferenceEquals(artifact, attempt.Source) &&
        _originalProviderArtifacts.TryGetValue(attempt.Source.Receipt.ArtifactId, out var original) && ReferenceEquals(original, artifact) &&
        _providerChildren.TryGetValue(lease.LeaseId, out var child) && ReferenceEquals(child, attempt.Child) && IsOriginalChildOwner(child) && child.Lease == lease &&
        (child.NestedParent is null || ProviderNestedIdentity(child)) && !child.CloseInFlight && child.FailedClose is null && child.ClosureReceipt is null &&
        !child.TransitionInFlight && child.FailedTransition is null && !HasArtifactBindInterlock(lease) &&
        ReferenceEquals(attempt.Source.BindMapping, attempt.Mapping) && HasCurrentGuestMapSources(attempt.Mapping) &&
        _providerGuestMappings.TryGetValue(attempt.Mapping.Lease.LeaseId, out var mapping) && ReferenceEquals(mapping, attempt.Mapping);
}
