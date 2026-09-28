# P04 — Translation and DMA Authority Composition

## Structured verdict

**PHASE:** P04 — Translation and DMA Authority Composition  
**BASELINE:** Region ownership exists; provider/external operation generations exist; address-space/IOMMU generation composition is not yet a single qualified runtime contract.  
**VERDICT:** High-priority correctness/security phase. Add generation-bound correlation, not a new authority ledger.

### VERIFIED_EXISTING
- RegionUse remains the byte-access/ownership fact.
- ExternalOperationAuthority and provider generation sets already support generation-aware external lifecycles.
- Provider/platform layers already own device-local mapping and execution details.

### PARTIAL
- Some PASID/SVA/IOMMU-style concepts exist in models/adapters, but presence is not proof of invalidation/revoke closure.
- Device reset/provider restart generations exist in external-runtime style contracts but are not yet bound to a full DMA execution composition.

### GAPS
- Address-space incarnation generation.
- Translation/mapping generation and invalidation completion evidence.
- Device lease/reset generation binding.
- Page-fault revalidation rules.
- In-flight DMA behavior when capability/Region/mapping changes.

### CONTRADICTIONS
- PASID/IOVA/raw address must not become authority.
- Capability revoke cannot be claimed to stop already-submitted DMA without provider containment/closure evidence.

### REMOVE_OR_MERGE
- No TranslationAuthority.
- No IOMMU ledger inside RegionAuthority.
- Keep P04 separate from P01.

### NEW_REQUIRED
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

### REQUIRED TESTS
- Unmap-vs-submit race.
- Capability/Region revoke after provider admission and before submit.
- Translation change during in-flight DMA.
- PASID/domain ID reuse ABA.
- Page-fault revalidation positive/negative tests.
- Device reset at every lifecycle state.
- Guest/SecureDomain mapping mismatch tests.
- Provider-loss with possible write -> quarantine test.

### FORMAL WORK
- TLA+ for map/unmap/revoke/submit/reset interleavings.
- Finite model for RegionUse × address-space × translation × device lease composition.

### DEPENDENCIES
Hard: P01 + contract extension core. P05 closes the refinement relation. Required before DMA-capable P08/P10 and before multi-host.

### EXIT CRITERIA
- Every DMA effect binds RegionUse + address-space + device + translation + provider generations.
- No raw address-like value passes an authority boundary as permission.
- Invalidation/closure is executable for the selected provider contour.
- Ambiguous possible writes quarantine rather than reclaim.

### ROADMAP PATCH
Make P04 the generation/TOCTOU closure phase. Treat DmaExecutionBinding as correlation evidence only.

## Objective

High-priority correctness/security phase. Add generation-bound correlation, not a new authority ledger.

## Live baseline and existing mechanisms
- RegionAuthority and external operation lifecycle.
- ExternalGenerationSet-style provider generation contracts.
- Platform/provider adapter boundaries.

## Required implementation changes
- Introduce DmaExecutionBindingV1 sidecar.
- Add final live revalidation immediately before submit.
- Add invalidation/containment adapter contract.
- Add device page-fault revalidation hook.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-DMA-TRANSLATION-BINDING`  
**Claim ceiling at emulator/runtime freeze:** `RuntimeEnforced + ExecutableAdapter for named emulator/provider adapter; physical IOMMU isolation not ProductionQualified`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P04-A contract sidecar.
- P04-B owner-generation capture/revalidation.
- P04-C provider invalidation/reset adapter.
- P04-D race/fault suite.
- P04-E qualification artifact.

## Rollback

Disable DMA-binding gate; new operations use existing staged/copy path. In-flight operations must still reconcile/contain before Region reclaim.
