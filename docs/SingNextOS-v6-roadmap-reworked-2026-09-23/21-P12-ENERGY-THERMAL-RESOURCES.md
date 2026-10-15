> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/21-P12-ENERGY-THERMAL-RESOURCES.md).

# P12 — Energy, Power and Thermal Resource Semantics

## Scope

Integrate committed quantitative energy/power dimensions into existing resource algebra only where enforceable. Treat measurement and thermal state separately.

### Target semantic requirements

- Separate requested budget, measured energy, enforced power/energy cap, thermal state, DVFS state and performance guarantee.
- Attribution error/confidence semantics.

### CONTRADICTIONS

- Telemetry cannot become budget authority.
- Thermal throttling does not imply deadline guarantee.

### Excluded designs

- No PowerAuthority/EnergyAuthority.

### Candidate contract shapes

- New ResourceClass dimensions only for committed budgets.
- ProviderEnergyEvidenceV1 for measurement/enforcement evidence.

### AUTHORITY IMPACT
ResourceBudgetAuthority owns committed budget quantities; provider owns measurement/thermal/DVFS state; scheduler uses policy only.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT` for measurement/enforcement evidence where available.

### COMPILER IMPACT
Optional estimates; no authority.

### ISA IMPACT
`NONE`

### SECURITY RISKS

- Telemetry spoof/attribution laundering.
- Energy budget interpreted as execution permission.

### CORRECTNESS RISKS

- Double charging across replay/preemption.
- Clock/counter rollover or provider reset corrupts settlement.

### PERFORMANCE RISKS

- Fine-grained metering overhead and throttling can hurt throughput; quantify.

### Applicable behavior and qualification tests

- Energy-account conservation.
- Counter reset/rollover.
- Replay/preemption charging.
- Measured-vs-enforced distinction tests.
- Thermal throttle without deadline guarantee test.

### Property-specific model options

- Property tests for accounting; no theorem proving required.

### DEPENDENCIES
P03/resource algebra hard. P07 locality soft. Optional, off critical path.

### EXIT CRITERIA

- No new authority ledger.
- Claims distinguish measurement from enforcement.
- Any EnforcedUpperBound claim has concrete provider enforcement evidence.

## Consumer requirements

- Add optional energy resource classes.
- Add provider evidence adapter.
- Add settlement and reset handling.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-ENERGY-BUDGETS`  
**Property-specific claim boundaries:** `Measurement/EnforcedUpperBound only where concrete enforcement exists; ProductionQualified later`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P12-A vocabulary.
- P12-B accounting dimensions.
- P12-C provider measurement.
- P12-D optional enforced cap.
- P12-E performance qualification.

## Rollback

Disable energy gate; resource accounting returns to existing dimensions. Provider thermal protection remains provider-owned and independent.
