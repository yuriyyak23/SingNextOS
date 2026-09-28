# P07 — Semantic Locality and Data-Motion Planning

## Structured verdict

**PHASE:** P07 — Semantic Locality and Data-Motion Planning  
**BASELINE:** Planner/scheduler and Region movement/accounting mechanisms exist; provider topology and cost observations are evidence, not authority.  
**VERDICT:** Keep P07 advisory and performance-oriented. Explicit data movement is an operation; topology never enters authority ABI.

### VERIFIED_EXISTING
- Planner can rank providers/locations.
- RegionAuthority owns ownership transfer/movement consequences.
- ResourceBudgetAuthority can charge resource consumption.

### PARTIAL
- No stable provider-neutral semantic cost model spans compute, copies, migration, contention and energy.

### GAPS
- Cost model schema with confidence/freshness.
- Explicit data-motion operation lifecycle and budget charge.
- Topology-private provider hints mapped to provider-neutral cost evidence.

### CONTRADICTIONS
- Physical topology ID must not become capability/session/Region authority.
- A cheaper path cannot bypass provider admission or refinement.

### REMOVE_OR_MERGE
- Do not merge with P08 preemption.
- No TopologyAuthority.

### NEW_REQUIRED
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

### REQUIRED TESTS
- Topology-ID absence ABI tests.
- Stale-cost plan tests.
- Movement accounting conservation.
- Concurrent Region movement race.
- Performance A/B against reference planner.

### FORMAL WORK
- No heavy formal proof required; property tests for accounting and stale-generation invalidation.

### DEPENDENCIES
P01 and existing resource accounting. P04 required for DMA-backed movement. P12 may enrich energy cost later. Optional, not critical path.

### EXIT CRITERIA
- No authority ABI contains provider-private topology IDs.
- Data movement has explicit lifecycle/accounting.
- Performance benefit is measured before policy promotion.

### ROADMAP PATCH
De-scope P07 to provider-neutral evidence + explicit movement. Keep it advisory until performance-qualified.

## Objective

Keep P07 advisory and performance-oriented. Explicit data movement is an operation; topology never enters authority ABI.

## Live baseline and existing mechanisms
- ComputePlanner/ResourceScheduler policy.
- RegionAuthority movement/ownership.
- ResourceBudgetAuthority charging.

## Required implementation changes
- Add cost evidence schema.
- Add movement operation trace/accounting.
- Add stale-plan revalidation.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-LOCALITY-PLANNING`  
**Claim ceiling at emulator/runtime freeze:** `ModelOnly/advisory then performance-qualified; never authority`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P07-A cost schema.
- P07-B explicit movement accounting.
- P07-C advisory planner.
- P07-D performance qualification.

## Rollback

Disable locality gate; planner uses previous policy. No ownership state is rolled back based on a policy estimate.
