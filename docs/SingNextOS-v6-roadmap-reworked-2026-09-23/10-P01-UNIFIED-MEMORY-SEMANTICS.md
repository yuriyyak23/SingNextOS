> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/10-P01-UNIFIED-MEMORY-SEMANTICS.md).

# P01 — Unified Heterogeneous Memory Semantics

## Scope

Proceed, but reduce scope from a universal memory model to an explicit semantic decomposition and qualify staged/exclusive contours before shared mutable memory.

### Target semantic requirements

- Provider-neutral ordering/atomicity/coherence vocabulary.
- Explicit alias/disjointness and shared-mutable Region use modes.
- CPU↔device fence/visibility contract and direct-coherent-output qualification.
- Per-contour distinction between coherence support and publication semantics.

### CONTRADICTIONS

- Any statement that coherence alone permits shared mutation is contradicted by the ownership model.
- Any direct-output claim inferred from descriptor or sideband presence is unsupported without retire/visibility/publication evidence.

### Excluded designs

- Do not create a MemoryAuthority or unified coherence ledger.
- Do not merge P01 with P04; memory ownership/use and translation mapping have different owners.

### Candidate contract shapes

- MemorySemanticsV1 sidecar: access class, ordering requirement, atomicity requirement, coherence assumption, visibility requirement, publication mode.
- Explicit Region use mode for shared atomic/mutable access if and only if RegionAuthority can enforce/track it.

### AUTHORITY IMPACT
No new owner. RegionAuthority may gain narrowly scoped use-mode/subrange state; publication owner remains separate; provider/HybridCPU guarantees remain evidence/legality.

### HYBRIDCPU IMPACT
`RUNTIME_ONLY` + optional `SIDEBAND_CONTRACT`: expose/validate existing ordering/fence/retire facts. No capability objects enter the ISE.

### COMPILER IMPACT
Optional metadata/proof for footprint, alias and ordering only; not needed to enable the first runtime contour.

### ISA IMPACT
`NONE`

### SECURITY RISKS

- Hidden aliasing across Region projections.
- Coherence laundering into ownership or publication permission.
- Stale Region generation used after planning.

### CORRECTNESS RISKS

- Unmodelled atomics or fence asymmetry between CPU, MatrixTile, DSC, L7 and external DMA.
- Completion observed before required visibility.
- Direct coherent output published before all writers are closed.

### PERFORMANCE RISKS

- Over-fencing can erase heterogeneous performance gains.
- Staged output is safer but may add copies; measure before enabling direct coherent paths.

### Applicable behavior and qualification tests

- CPU acquire/release litmus tests.
- CPU↔DSC and CPU↔L7 visibility litmus tests.
- Alias/disjointness negative tests.
- Region revoke/mutation race during execution.
- Completion-before-visibility and visibility-before-publication negative tests.
- Differential ordinary SIP vs heterogeneous staged-output traces.

### Property-specific model options

- Alloy/finite model for Region use/alias configurations.
- TLA+/trace model for writer/visibility/publication ordering.

### DEPENDENCIES
Hard: P00 + contract extension core. P04 depends on P01. P02, P07, P10 consume P01 semantics. P05 starts in parallel and closes this contour.

### EXIT CRITERIA

- Exclusive staged-output contour is fully specified and executable.
- All memory clauses have a partial-order/refinement rule.
- No direct shared-mutable path is enabled without provider+ISE evidence.
- Generation races fail closed or quarantine.

## Consumer requirements

- Add canonical MemorySemanticsV1 sidecar and refinement rules.
- Add explicit visibility/fence evidence from concrete adapters.
- Add Region shared-mutability mode only if enforcement is demonstrated.
- Instrument semantic trace events for submit/retire/visible/published.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-MEMORY-SEMANTICS`; `V6-SHARED-ATOMIC-REGION` remains OFF  
**Property-specific claim boundaries:** `ExecutableAdapter for staged/exclusive contour; shared atomic remains FutureGated`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P01-A schema/canonicalization.
- P01-B reference staged memory semantics and tests.
- P01-C HybridCPU/provider visibility adapter.
- P01-D direct-coherent experiment behind OFF gate only.
- P01-E evidence/claim update.

## Rollback

Disable memory-extension gate and fresh-admit on the existing staged V1 path. Already-submitted effects still follow normal visibility/publication/reclaim closure.
