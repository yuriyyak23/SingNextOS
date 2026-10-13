> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/12-P03-TEMPORAL-EXECUTION-CONTRACTS.md).

# P03 — Temporal Execution and Resource Contracts

## Scope

Extend the existing resource algebra. Separate accounting, upper bounds, reservations and real-time service guarantees. Hard real-time remains unsupported by default.

### Target semantic requirements

- Temporal clause vocabulary with units and scope.
- Interference model and provider guarantee semantics.
- Donation/priority ceiling accounting rules.
- Explicit claim distinction: requested deadline vs enforced service.

### CONTRADICTIONS

- Any `TemporalAuthority` duplicates resource ownership.
- Average latency telemetry cannot satisfy a deadline guarantee.

### Excluded designs

- Remove TemporalAuthority.
- Do not merge temporal accounting with scheduler policy.

### Candidate contract shapes

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

### Applicable behavior and qualification tests

- Budget conservation property tests.
- Donation cycle/ceiling negative tests.
- Replay/squash charging tests.
- Deadline miss reporting tests.
- Provider stall/failure tests.
- Guarantee-vs-measurement claim tests.

### Property-specific model options

- TLA+ for donation/settlement/preemption accounting.
- Finite schedulability model only for selected policy/provider contour.

### DEPENDENCIES
P00 + C0 + existing budget algebra. Runs in parallel with P01. P08 consumes P03 for bounded preemption/service claims; P12 reuses resource dimensions.

### EXIT CRITERIA

- No duplicate ledger.
- Upper-bound enforcement and reservation claims are separately testable.
- Guaranteed deadline remains OFF unless end-to-end schedulability+enforcement evidence exists.

## Consumer requirements

- Add temporal clause sidecar.
- Extend resource dimensions if needed.
- Add provider guarantee/evidence mapping.
- Add claim guards preventing reservation→deadline promotion.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-TEMPORAL-CONTRACTS`; stronger `V6-GUARANTEED-DEADLINE` remains OFF  
**Property-specific claim boundaries:** `RuntimeEnforced for accounting/upper bounds; GuaranteedReservation only for named committed resource; hard deadline FutureGated`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P03-A vocabulary/units.
- P03-B budget algebra extension.
- P03-C provider timing adapter.
- P03-D interference/donation tests.
- P03-E optional schedulability evidence.

## Rollback

Disable temporal extension gate; accounting returns to existing budget semantics. Already-reserved resources settle through existing owner paths.
