> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/16-P07-LOCALITY-DATA-MOTION.md).

# P07 — Semantic Locality and Data-Motion Planning

## Scope

Keep P07 advisory and performance-oriented. Explicit data movement is an operation; topology never enters authority ABI.

### Target semantic requirements

- Cost model schema with confidence/freshness.
- Explicit data-motion operation lifecycle and budget charge.
- Topology-private provider hints mapped to provider-neutral cost evidence.

### CONTRADICTIONS

- Physical topology ID must not become capability/session/Region authority.
- A cheaper path cannot bypass provider admission or refinement.

### Excluded designs

- Do not merge with P08 preemption.
- No TopologyAuthority.

### Candidate contract shapes

- LocalityCostEstimateV1 evidence schema.
- DataMotionPlanV1 policy object, explicitly non-authoritative.

### AUTHORITY IMPACT
Planner owns only policy ranking. RegionAuthority and ResourceBudgetAuthority own actual movement/charge facts; provider owns topology evidence.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT` only for provider-neutral cost/placement evidence if needed.

### COMPILER IMPACT
Optional static locality/resource estimates; no permission.

### ISA IMPACT
`NONE`

### SECURITY RISKS

- Topology identity leakage into authority ABI.
- Planner cached cost used as authorization.

### CORRECTNESS RISKS

- Region moved after plan but before execution.
- Cost estimate stale under contention.

### PERFORMANCE RISKS

- Planner overhead and poor estimates can regress performance; advisory rollout required.

### Applicable behavior and qualification tests

- Topology-ID absence ABI tests.
- Stale-cost plan tests.
- Movement accounting conservation.
- Concurrent Region movement race.
- Performance A/B against reference planner.

### Property-specific model options

- No heavy formal proof required; property tests for accounting and stale-generation invalidation.

### DEPENDENCIES
P01 and existing resource accounting. P04 required for DMA-backed movement. P12 may enrich energy cost later. Optional, not critical path.

### EXIT CRITERIA

- No authority ABI contains provider-private topology IDs.
- Data movement has explicit lifecycle/accounting.
- Performance benefit is measured before policy promotion.

## Consumer requirements

- Add cost evidence schema.
- Add movement operation trace/accounting.
- Add stale-plan revalidation.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-LOCALITY-PLANNING`  
**Property-specific claim boundaries:** `ModelOnly/advisory then performance-qualified; never authority`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P07-A cost schema.
- P07-B explicit movement accounting.
- P07-C advisory planner.
- P07-D performance qualification.

## Rollback

Disable locality gate; planner uses previous policy. No ownership state is rolled back based on a policy estimate.
