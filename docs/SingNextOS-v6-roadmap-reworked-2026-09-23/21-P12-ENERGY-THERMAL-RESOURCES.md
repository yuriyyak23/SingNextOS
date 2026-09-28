# P12 — Energy, Power and Thermal Resource Semantics

## Structured verdict

**PHASE:** P12 — Energy, Power and Thermal Resource Semantics  
**BASELINE:** ResourceBudgetAuthority exists; provider counters/thermal/DVFS state are not a single authoritative SingNext budget today.  
**VERDICT:** Integrate committed quantitative energy/power dimensions into existing resource algebra only where enforceable. Treat measurement and thermal state separately.

### VERIFIED_EXISTING
- ResourceBudgetAuthority already owns quantitative budgets.
- Provider/runtime is the source of machine-local energy/thermal/performance state.

### PARTIAL
- Energy measurement may be available as telemetry but not necessarily enforceable or attributable per operation.

### GAPS
- Separate requested budget, measured energy, enforced power/energy cap, thermal state, DVFS state and performance guarantee.
- Attribution error/confidence semantics.

### CONTRADICTIONS
- Telemetry cannot become budget authority.
- Thermal throttling does not imply deadline guarantee.

### REMOVE_OR_MERGE
- No PowerAuthority/EnergyAuthority.

### NEW_REQUIRED
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

### REQUIRED TESTS
- Energy-account conservation.
- Counter reset/rollover.
- Replay/preemption charging.
- Measured-vs-enforced distinction tests.
- Thermal throttle without deadline guarantee test.

### FORMAL WORK
- Property tests for accounting; no theorem proving required.

### DEPENDENCIES
P03/resource algebra hard. P07 locality soft. Optional, off critical path.

### EXIT CRITERIA
- No new authority ledger.
- Claims distinguish measurement from enforcement.
- Any EnforcedUpperBound claim has concrete provider enforcement evidence.

### ROADMAP PATCH
Reframe P12 as resource-algebra extension + provider evidence; keep guaranteed energy/performance claims narrow.

## Objective

Integrate committed quantitative energy/power dimensions into existing resource algebra only where enforceable. Treat measurement and thermal state separately.

## Live baseline and existing mechanisms
- ResourceBudgetAuthority.
- Provider telemetry/enforcement.
- Scheduler policy.

## Required implementation changes
- Add optional energy resource classes.
- Add provider evidence adapter.
- Add settlement and reset handling.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-ENERGY-BUDGETS`  
**Claim ceiling at emulator/runtime freeze:** `Measurement/EnforcedUpperBound only where concrete enforcement exists; ProductionQualified later`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P12-A vocabulary.
- P12-B accounting dimensions.
- P12-C provider measurement.
- P12-D optional enforced cap.
- P12-E performance qualification.

## Rollback

Disable energy gate; resource accounting returns to existing dimensions. Provider thermal protection remains provider-owned and independent.
