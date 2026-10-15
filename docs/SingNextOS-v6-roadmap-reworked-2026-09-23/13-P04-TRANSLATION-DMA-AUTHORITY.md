> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/13-P04-TRANSLATION-DMA-AUTHORITY.md).

# P04 — Translation and DMA Authority Composition

## Scope

High-priority correctness/security phase. Add generation-bound correlation, not a new authority ledger.

### Target semantic requirements

- Address-space incarnation generation.
- Translation/mapping generation and invalidation completion evidence.
- Device lease/reset generation binding.
- Page-fault revalidation rules.
- In-flight DMA behavior when capability/Region/mapping changes.

### CONTRADICTIONS

- PASID/IOVA/raw address must not become authority.
- Capability revoke cannot be claimed to stop already-submitted DMA without provider containment/closure evidence.

### Excluded designs

- No TranslationAuthority.
- No IOMMU ledger inside RegionAuthority.
- Keep P04 separate from P01.

### Candidate contract shapes

- Non-authoritative `DmaExecutionBindingV1` or equivalent correlation record.
- TranslationInvalidationEvidenceV1 with provider generation and completion semantics.
- Effect-ambiguity quarantine rule for revoke/reset races.

### AUTHORITY IMPACT
RegionAuthority does not own mappings. Process/VM address-space owner owns incarnation; platform/IOMMU provider owns mapping; device owner owns lease/reset; ExternalOperationAuthority owns operation lifecycle.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT`/`RUNTIME_ONLY` in ExternalRuntime/provider adapter for current mapping/reset generation and invalidation/containment evidence.

### COMPILER IMPACT
No required compiler change. Compiler may later supply footprint evidence only.

### ISA IMPACT
`NONE`

### SECURITY RISKS

- Confused-deputy use of a valid mapping for the wrong Region/use generation.
- Stale PASID/IOMMU handle reuse (ABA).
- DMA continues after authority revoke while OS assumes cancellation.

### CORRECTNESS RISKS

- TOCTOU between final mapping check and device submit.
- Page fault remaps to bytes outside authorized Region.
- Device reset loses containment state while operation is considered closed.

### PERFORMANCE RISKS

- Frequent invalidation/generation checks can be expensive; use narrow pinned leases/caches only for immutable facts.

### Applicable behavior and qualification tests

- Unmap-vs-submit race.
- Capability/Region revoke after provider admission and before submit.
- Translation change during in-flight DMA.
- PASID/domain ID reuse ABA.
- Page-fault revalidation positive/negative tests.
- Device reset at every lifecycle state.
- Guest/SecureDomain mapping mismatch tests.
- Provider-loss with possible write -> quarantine test.

### Property-specific model options

- TLA+ for map/unmap/revoke/submit/reset interleavings.
- Finite model for RegionUse × address-space × translation × device lease composition.

### DEPENDENCIES
Hard: P01 + contract extension core. P05 closes the refinement relation. Required before DMA-capable P08/P10 and before multi-host.

### EXIT CRITERIA

- Every DMA effect binds RegionUse + address-space + device + translation + provider generations.
- No raw address-like value passes an authority boundary as permission.
- Invalidation/closure is executable for the selected provider contour.
- Ambiguous possible writes quarantine rather than reclaim.

## Consumer requirements

- Introduce DmaExecutionBindingV1 sidecar.
- Add final live revalidation immediately before submit.
- Add invalidation/containment adapter contract.
- Add device page-fault revalidation hook.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-DMA-TRANSLATION-BINDING`  
**Property-specific claim boundaries:** `RuntimeEnforced + ExecutableAdapter for named emulator/provider adapter; physical IOMMU isolation not ProductionQualified`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P04-A contract sidecar.
- P04-B owner-generation capture/revalidation.
- P04-C provider invalidation/reset adapter.
- P04-D race/fault suite.
- P04-E qualification artifact.

## Rollback

Disable DMA-binding gate; new operations use existing staged/copy path. In-flight operations must still reconcile/contain before Region reclaim.
