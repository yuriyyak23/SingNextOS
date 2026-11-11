namespace YAKSys_Hybrid_CPU.Core;

public enum NeutralOwnedRegionMapDecision { Mapped, InvalidRange, InvalidAccess, Revoked, Stale, NotFound, Faulted }
public enum NeutralOwnedRegionVisibilityDecision { Satisfied, Unsupported, Revoked, Stale, NotFound, Faulted }
public enum NeutralOwnedRegionCloseDecision { Closed, Revoked, ActiveDependents, Stale, NotFound, Faulted }
public enum NeutralOwnedRegionAcquireDecision { Satisfied, Unsupported, NotClosed, RevokedDomain, Stale, NotFound, Faulted }

public readonly record struct NeutralOwnedRegionMappingHandle(ulong Value);
public readonly record struct NeutralOwnedRegionMappingEpoch(ulong Value);
public readonly record struct NeutralOwnedRegionSlice(long Offset, long Length, NeutralMemoryAccess Access, NeutralMemoryCoherenceModel Coherence = NeutralMemoryCoherenceModel.NonCoherent);
public readonly record struct NeutralOwnedRegionMappingLease(NeutralDomainBindingLease DomainLease, NeutralOwnedRegionSlice Slice, NeutralMemoryCoherenceModel Coherence, NeutralOwnedRegionMappingHandle Handle, NeutralOwnedRegionMappingEpoch Epoch);

public sealed class NeutralOwnedRegionMapResult { public bool IsMapped { get; init; } public NeutralOwnedRegionMappingLease Lease { get; init; } public NeutralOwnedRegionMapDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralOwnedRegionVisibilityResult { public NeutralOwnedRegionVisibilityDecision Decision { get; init; } public NeutralOwnedRegionMappingLease Lease { get; init; } public NeutralMemoryVisibilityRequirement Requirement { get; init; } public NeutralMemoryVisibilityOutcome Outcome { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralOwnedRegionAcquireResult { public NeutralOwnedRegionAcquireDecision Decision { get; init; } public NeutralOwnedRegionMappingLease Lease { get; init; } public NeutralMemoryAcquireRequirement Requirement { get; init; } public NeutralMemoryAcquireOutcome Outcome { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralOwnedRegionCloseResult { public NeutralOwnedRegionCloseDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
