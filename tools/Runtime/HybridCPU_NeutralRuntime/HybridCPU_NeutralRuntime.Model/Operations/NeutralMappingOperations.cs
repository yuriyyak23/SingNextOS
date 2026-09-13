namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade
{
    public int ActiveOwnedRegionMappingCount => _dependencies.ActiveMappingCount;

    public NeutralOwnedRegionMapResult MapOwnedRegion(NeutralDomainBindingLease domain, NeutralOwnedRegionSlice slice)
    {
        var parent = _leases.Validate(domain);
        if (!parent.IsValid) return new() { Decision = ToMappingMap(parent.Status) };
        if (slice.Offset < 0 || slice.Length <= 0 || slice.Offset > long.MaxValue - slice.Length) return new() { Decision = NeutralOwnedRegionMapDecision.InvalidRange };
        if (slice.Access == NeutralMemoryAccess.None || !NeutralRuntimeValidation.IsDefinedFlags(slice.Access, NeutralMemoryAccess.Read | NeutralMemoryAccess.Write)) return new() { Decision = NeutralOwnedRegionMapDecision.InvalidAccess };
        if (!Enum.IsDefined(slice.Coherence)) return new() { Decision = NeutralOwnedRegionMapDecision.InvalidRange, Reason = "Mapping coherence model is invalid." };
        if (!TryAllocate(ref _nextResource, out var handle)) return new() { Decision = NeutralOwnedRegionMapDecision.Faulted, Reason = "Neutral resource handle space is exhausted." };
        var lease = new NeutralOwnedRegionMappingLease(domain, slice, slice.Coherence, new(handle), new(1));
        _dependencies.RegisterMapping(lease);
        return new() { IsMapped = true, Lease = lease, Decision = NeutralOwnedRegionMapDecision.Mapped };
    }

    public NeutralOwnedRegionCloseResult CloseOwnedRegionMapping(NeutralOwnedRegionMappingLease lease)
    {
        var validation = _leases.Validate(lease);
        if (!validation.IsValid) return new() { Decision = ToMappingClose(validation.Status) };
        if (_dependencies.HasActiveDependents(lease)) return new() { Decision = NeutralOwnedRegionCloseDecision.ActiveDependents };
        validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
        return new() { Decision = NeutralOwnedRegionCloseDecision.Closed };
    }

    public NeutralOwnedRegionVisibilityResult PrepareOwnedRegionVisibility(NeutralOwnedRegionMappingLease lease, NeutralMemoryVisibilityRequirement requirement)
    {
        var validation = _leases.Validate(lease);
        if (!validation.IsValid) return new() { Lease = lease, Requirement = requirement, Decision = ToVisibility(validation.Status), Outcome = NeutralMemoryVisibilityOutcome.Unsupported };
        return (lease.Coherence, requirement) switch
        {
            (NeutralMemoryCoherenceModel.Coherent, NeutralMemoryVisibilityRequirement.CoherentAccess) => Visibility(lease, requirement, NeutralOwnedRegionVisibilityDecision.Satisfied, NeutralMemoryVisibilityOutcome.Coherent),
            (_, NeutralMemoryVisibilityRequirement.PublicationFence) => Visibility(lease, requirement, NeutralOwnedRegionVisibilityDecision.Satisfied, NeutralMemoryVisibilityOutcome.PublicationFenceSatisfied),
            _ => Visibility(lease, requirement, NeutralOwnedRegionVisibilityDecision.Unsupported, NeutralMemoryVisibilityOutcome.Unsupported),
        };
    }

    public NeutralOwnedRegionAcquireResult AcquireOwnedRegionVisibility(NeutralOwnedRegionMappingLease lease, NeutralMemoryAcquireRequirement requirement)
    {
        var validation = _leases.Validate(lease);
        if (validation.Status == NeutralLeaseValidationStatus.Valid) return new() { Lease = lease, Requirement = requirement, Decision = NeutralOwnedRegionAcquireDecision.NotClosed, Outcome = NeutralMemoryAcquireOutcome.Unsupported };
        if (validation.Status != NeutralLeaseValidationStatus.Revoked) return new() { Lease = lease, Requirement = requirement, Decision = ToAcquire(validation.Status), Outcome = NeutralMemoryAcquireOutcome.Unsupported };
        var parent = _leases.Validate(validation.State!.Lease.DomainLease);
        if (!parent.IsValid) return new() { Lease = lease, Requirement = requirement, Decision = parent.Status == NeutralLeaseValidationStatus.Revoked ? NeutralOwnedRegionAcquireDecision.RevokedDomain : ToAcquire(parent.Status), Outcome = NeutralMemoryAcquireOutcome.Unsupported };
        if (requirement != NeutralMemoryAcquireRequirement.AcquisitionFence) return new() { Lease = lease, Requirement = requirement, Decision = NeutralOwnedRegionAcquireDecision.Unsupported, Outcome = NeutralMemoryAcquireOutcome.Unsupported };
        return new() { Lease = validation.State.Lease, Requirement = requirement, Decision = NeutralOwnedRegionAcquireDecision.Satisfied, Outcome = NeutralMemoryAcquireOutcome.AcquisitionFenceSatisfied };
    }

    private static NeutralOwnedRegionVisibilityResult Visibility(NeutralOwnedRegionMappingLease lease, NeutralMemoryVisibilityRequirement requirement, NeutralOwnedRegionVisibilityDecision decision, NeutralMemoryVisibilityOutcome outcome) => new() { Lease = lease, Requirement = requirement, Decision = decision, Outcome = outcome };
    private static NeutralOwnedRegionMapDecision ToMappingMap(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralOwnedRegionMapDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralOwnedRegionMapDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralOwnedRegionMapDecision.Revoked, _ => NeutralOwnedRegionMapDecision.Faulted };
    private static NeutralOwnedRegionCloseDecision ToMappingClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralOwnedRegionCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralOwnedRegionCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralOwnedRegionCloseDecision.Revoked, _ => NeutralOwnedRegionCloseDecision.Faulted };
    private static NeutralOwnedRegionVisibilityDecision ToVisibility(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralOwnedRegionVisibilityDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralOwnedRegionVisibilityDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralOwnedRegionVisibilityDecision.Revoked, _ => NeutralOwnedRegionVisibilityDecision.Faulted };
    private static NeutralOwnedRegionAcquireDecision ToAcquire(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralOwnedRegionAcquireDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralOwnedRegionAcquireDecision.Stale, _ => NeutralOwnedRegionAcquireDecision.Faulted };
}
