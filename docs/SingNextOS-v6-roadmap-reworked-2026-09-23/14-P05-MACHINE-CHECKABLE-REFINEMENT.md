# P05 — Machine-Checkable Cross-Layer Refinement

## Structured verdict

**PHASE:** P05 — Machine-Checkable Cross-Layer Refinement  
**BASELINE:** V1 semantic refinement exists in live runtime; current relation is narrower than v6 dimensions and lacks a common trace algebra.  
**VERDICT:** Make P05 a cross-cutting semantic spine. Build a small executable partial-order/trace checker first; no theorem prover and no "full formal verification" claim.

### VERIFIED_EXISTING
- `SemanticExecutionRefinementV1.Evaluate`-style live refinement exists.
- HybridCPU runtime legality is explicitly independent and returns LegalityDecision through IRuntimeLegalityService.
- ExternalRuntime provides versioned generation/admission/publication contracts.

### PARTIAL
- Existing refinement is executable but not yet expressive for all memory/DMA/time/failure dimensions.
- Current evidence is local to selected contours rather than a single cross-project trace schema.

### GAPS
- Dimension-specific partial orders.
- Mandatory/optional clause semantics.
- Numeric comparison rules with units and overflow/canonicalization.
- Trace projection from provider/HybridCPU events to SingNext semantic events.
- Counterexample recording and differential trace harness.

### CONTRADICTIONS
- A formal model must not become a runtime authority or GlobalState owner.
- Passing model checking cannot promote a hardware/production claim.

### REMOVE_OR_MERGE
- Remove any goal of proving all P01–P12 at once.
- Do not require theorem prover integration for v6 entry.

### NEW_REQUIRED
- SemanticTraceEventV1 schema.
- Dimension-specific refinement evaluators.
- Executable trace checker and counterexample format.
- Qualification artifact binding model version to source tuple.

### AUTHORITY IMPACT
None. Formal model is specification/evidence only. Live owners remain authoritative.

### HYBRIDCPU IMPACT
`RUNTIME_ONLY` instrumentation for trace events plus existing legality outputs; no legality ownership change.

### COMPILER IMPACT
Optional: emit evidence tags used by the checker. No compiler authority.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Evidence laundering: treating refinement proof as permission.
- Schema mismatch/unknown mandatory dimension silently accepted.

### CORRECTNESS RISKS
- Wrong partial-order direction.
- Numeric unit mismatch or overflow.
- Projection erases an irreversible effect or generation change.

### PERFORMANCE RISKS
- Trace instrumentation overhead; support sampled/offline modes after semantic equivalence is proven.

### REQUIRED TESTS
- Refinement truth-table tests per dimension.
- Unknown mandatory/optional tests.
- Mutation tests that weaken one guarantee and must fail.
- Differential SIP vs SipJob/HybridCPU traces.
- Counterexample reproducibility tests.
- Trace canonicalization tests.

### FORMAL WORK
- TLA+/PlusCal for cross-owner transitions.
- Finite exploration for configuration/refinement.
- Property tests for partial-order laws: reflexivity/transitivity/antisymmetry where applicable.

### DEPENDENCIES
Hard core starts after C0 and P01 vocabulary; each later phase adds a dimension. First qualification vertical requires minimal P01/P04 refinement, not all phases.

### EXIT CRITERIA
- Minimal mathematical core documented and executable.
- First vertical produces identical allowed semantic projection on reference and heterogeneous path.
- Counterexamples are actionable and tuple-bound.
- No claim exceeds model/trace evidence.

### ROADMAP PATCH
Recast P05 from a late phase to a continuously extended refinement framework with per-contour closure.

## Objective

Make P05 a cross-cutting semantic spine. Build a small executable partial-order/trace checker first; no theorem prover and no "full formal verification" claim.

## Live baseline and existing mechanisms
- V1 runtime refinement evaluator.
- HybridCPU LegalityDecision/IRuntimeLegalityService.
- ExternalRuntime generation/admission/publication artifacts.

## Required implementation changes
- Add trace schema and projection.
- Add memory/DMA refinement dimensions first.
- Add temporal/failure/durability dimensions only when their contracts stabilize.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-FORMAL-REFINEMENT`  
**Claim ceiling at emulator/runtime freeze:** `RuntimeEnforced for refinement check itself; ModelOnly for unimplemented semantic dimensions`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P05-A math/core definitions.
- P05-B executable evaluator library.
- P05-C trace schema/harness.
- P05-D first vertical differential checker.
- P05-E model-check artifacts.

## Rollback

Disable refinement-dependent optimized contour and fresh-admit on the reference staged path. The formal tooling itself has no runtime rollback semantics.
