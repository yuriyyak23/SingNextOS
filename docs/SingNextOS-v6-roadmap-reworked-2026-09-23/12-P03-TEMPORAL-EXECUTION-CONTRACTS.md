# P03 — Temporal Execution and Resource Contracts

## Structured verdict

**PHASE:** P03 — Temporal Execution and Resource Contracts  
**BASELINE:** `ResourceBudgetAuthority` and `ResourceScheduler` exist; hard deadline/minimum-service enforcement is not generally qualified.  
**VERDICT:** Extend the existing resource algebra. Separate accounting, upper bounds, reservations and real-time service guarantees. Hard real-time remains unsupported by default.

### VERIFIED_EXISTING
- ResourceBudgetAuthority owns quantitative capacity/lease/settlement.
- Scheduler is policy, not permission/quantitative truth.
- Provider runtime can expose timing measurements/admission independently.

### PARTIAL
- Budget accounting is executable; deadline/minimum-service/WCET/schedulability are only partially represented or provider-specific.

### GAPS
- Temporal clause vocabulary with units and scope.
- Interference model and provider guarantee semantics.
- Donation/priority ceiling accounting rules.
- Explicit claim distinction: requested deadline vs enforced service.

### CONTRADICTIONS
- Any `TemporalAuthority` duplicates resource ownership.
- Average latency telemetry cannot satisfy a deadline guarantee.

### REMOVE_OR_MERGE
- Remove TemporalAuthority.
- Do not merge temporal accounting with scheduler policy.

### NEW_REQUIRED
- TemporalSemanticsV1 sidecar using ResourceClass/ResourceBudget dimensions.
- ProviderTemporalGuaranteeV1 for enforceable service only.

### AUTHORITY IMPACT
ResourceBudgetAuthority may gain new quantitative resource dimensions; scheduler remains policy; provider owns local service enforcement/evidence.

### HYBRIDCPU IMPACT
`RUNTIME_ONLY` or `SIDEBAND_CONTRACT` for provider timing enforcement/evidence; GuardPlane need not own OS deadlines.

### COMPILER IMPACT
Optional static resource estimates only; no deadline authority.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Budget laundering through donation.
- Priority inversion confused with permission.
- Telemetry promoted to guarantee.

### CORRECTNESS RISKS
- Incorrect settlement on replay/squash/preemption.
- Deadline unit/clock-domain mismatch.
- Provider stall not charged/contained consistently.

### PERFORMANCE RISKS
- Instrumentation and conservative reservation may reduce utilization.
- Measure contention/interference before stronger scheduling policies.

### REQUIRED TESTS
- Budget conservation property tests.
- Donation cycle/ceiling negative tests.
- Replay/squash charging tests.
- Deadline miss reporting tests.
- Provider stall/failure tests.
- Guarantee-vs-measurement claim tests.

### FORMAL WORK
- TLA+ for donation/settlement/preemption accounting.
- Finite schedulability model only for selected policy/provider contour.

### DEPENDENCIES
P00 + C0 + existing budget algebra. Runs in parallel with P01. P08 consumes P03 for bounded preemption/service claims; P12 reuses resource dimensions.

### EXIT CRITERIA
- No duplicate ledger.
- Upper-bound enforcement and reservation claims are separately testable.
- Guaranteed deadline remains OFF unless end-to-end schedulability+enforcement evidence exists.

### ROADMAP PATCH
Rewrite P03 as resource/temporal semantics, not a real-time authority subsystem.

## Objective

Extend the existing resource algebra. Separate accounting, upper bounds, reservations and real-time service guarantees. Hard real-time remains unsupported by default.

## Live baseline and existing mechanisms
- ResourceBudgetAuthority.
- ResourceScheduler.
- Invocation/session donation/lifecycle surfaces.

## Required implementation changes
- Add temporal clause sidecar.
- Extend resource dimensions if needed.
- Add provider guarantee/evidence mapping.
- Add claim guards preventing reservation→deadline promotion.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-TEMPORAL-CONTRACTS`; stronger `V6-GUARANTEED-DEADLINE` remains OFF  
**Claim ceiling at emulator/runtime freeze:** `RuntimeEnforced for accounting/upper bounds; GuaranteedReservation only for named committed resource; hard deadline FutureGated`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P03-A vocabulary/units.
- P03-B budget algebra extension.
- P03-C provider timing adapter.
- P03-D interference/donation tests.
- P03-E optional schedulability evidence.

## Rollback

Disable temporal extension gate; accounting returns to existing budget semantics. Already-reserved resources settle through existing owner paths.
