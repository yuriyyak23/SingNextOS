> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/14-P05-MACHINE-CHECKABLE-REFINEMENT.md).

# P05 — Machine-checkable refinement

## Scope

Make P05 a cross-cutting semantic spine. Build a small executable partial-order/trace checker first; no theorem prover and no "full formal verification" claim.

### Target semantic requirements

- Dimension-specific partial orders.
- Mandatory/optional clause semantics.
- Numeric comparison rules with units and overflow/canonicalization.
- Trace projection from provider/HybridCPU events to SingNext semantic events.
- Counterexample recording and differential trace harness.

### CONTRADICTIONS

- A formal model must not become a runtime authority or GlobalState owner.
- Passing model checking cannot promote a hardware/production claim.

### Excluded designs

- Remove any goal of proving all P01–P12 at once.
- Do not require theorem prover integration for v6 entry.

### Candidate contract shapes

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

### Applicable behavior and qualification tests

- Refinement truth-table tests per dimension.
- Unknown mandatory/optional tests.
- Mutation tests that weaken one guarantee and must fail.
- Differential SIP vs SipJob/HybridCPU traces.
- Counterexample reproducibility tests.
- Trace canonicalization tests.

### Property-specific model options

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

## Consumer requirements

- Add trace schema and projection.
- Add memory/DMA refinement dimensions first.
- Add temporal/failure/durability dimensions only when their contracts stabilize.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-FORMAL-REFINEMENT`  
**Property-specific claim boundaries:** `RuntimeEnforced for refinement check itself; ModelOnly for unimplemented semantic dimensions`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P05-A math/core definitions.
- P05-B executable evaluator library.
- P05-C trace schema/harness.
- P05-D first vertical differential checker.
- P05-E model-check artifacts.

## Rollback

Disable refinement-dependent optimized contour and fresh-admit on the reference staged path. The formal tooling itself has no runtime rollback semantics.
