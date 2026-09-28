# ADR-001: P01 memory order clauses are not a numeric strength scale

Status: accepted for the internal v6 memory sidecar while its feature gate is OFF.

## Context

`MemorySemanticPartialOrderV1` previously compared `MemoryOrderClassV1` by enum value. That made `SequentiallyConsistent` refine `CompletionBeforeVisibilityFence`. Sequential consistency of memory operations does not, by itself, establish completion of a device effect or the required CPU/device visibility fence. The v6 staged output contour needs the latter fact before publication. No current clause represents the conjunction of both guarantees.

## Decision

`Unspecified` is the weak requirement. `CompletionBeforeVisibilityFence` and `SequentiallyConsistent` refine themselves but are incomparable. An exclusive staged output clause must explicitly name `CompletionBeforeVisibilityFence`; a provider that also supplies sequential consistency needs a future versioned conjunctive clause and exact executable evidence before claiming both.

The enum values, serialized size, class ID, and V1 external-operation API remain unchanged. This changes only v6 memory sidecar validation/refinement behavior; formerly accepted SC-as-fence claims now fail admission. The runtime still revalidates Region use, mutation epochs, provider generation, independent admission, legality, visibility, and publication at their existing owner boundaries.

## Consequences and qualification

- No memory sidecar or refinement proof becomes an authority or machine legality decision.
- `V6-MEMORY-SEMANTICS` and `V6-SHARED-ATOMIC-REGION` remain OFF.
- Rejected callers may fresh-admit on the existing staged V1 path; already submitted effects must complete their normal closure protocol.
- Contract partial-order and managed contour tests cover the new negative cases. Physical CPU/device ordering, coherence, direct output, and production readiness remain unqualified.
- Java-dependent checks are excluded by instruction; cross-language compatibility is not claimed.

ISA/opcode/CPU architecture impact: NONE.
