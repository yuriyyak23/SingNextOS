> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/19-P10-RAS-FAILURE-DOMAINS.md).

# P10 — RAS and Failure-Domain Semantics

## Scope

Extend consequences in existing owners. Provider health is evidence; RegionAuthority owns damaged memory usability; ExternalOperationAuthority owns in-flight ambiguity.

### Target semantic requirements

- ProviderHealthEvidenceV1 with generation/failure-domain scope.
- Region subrange damage/quarantine transitions.
- In-flight operation consequence rules.
- Remap/reconfigure generation increments.

### CONTRADICTIONS

- Health telemetry must not directly mutate CapabilityAuthority.
- Provider loss does not prove closure/reclaim safety.

### Excluded designs

- No RasAuthority or second Region ledger.

### Candidate contract shapes

- FailureConsequenceV1 semantic sidecar.
- Subrange health/damage state in RegionAuthority where it is an ownership/use consequence.
- Explicit quarantine closure protocol.

### AUTHORITY IMPACT
Provider owns health evidence; RegionAuthority owns memory usability consequences; ExternalOperationAuthority owns in-flight effect state; device/fabric owner owns reset/rebind.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT` for health/poison/reset evidence only.

### COMPILER IMPACT
None required.

### ISA IMPACT
`NONE`

### SECURITY RISKS

- Health evidence laundering into ownership mutation.
- Unsafe reclaim after ambiguous writer/provider loss.
- ABA after fabric remap/reset.

### CORRECTNESS RISKS

- Partial Region damage incorrectly closes whole Region or misses damaged subrange.
- Poison/remap races with publication.

### PERFORMANCE RISKS

- Quarantine can pin resources indefinitely; require bounded operational/reconciliation policy without weakening safety.

### Applicable behavior and qualification tests

- ECC/poison subrange fault injection.
- Provider reset/path loss in every external-operation state.
- Partial Region loss/remap tests.
- Ambiguous write -> quarantine.
- Reconfiguration generation ABA tests.
- CXL memory degradation model tests.

### Property-specific model options

- TLA+ provider-loss/in-flight/reclaim model.
- Alloy/finite model for subrange damage ownership.

### DEPENDENCIES
P01 hard; P04 for device/DMA consequences; P05 failure semantics. P02 only for persistent contours. P11 hard-depends on P10.

### EXIT CRITERIA

- Subrange damage is expressible without a new ownership ledger.
- No reclaim occurs while a possible writer is unclosed.
- Reconfiguration increments exact generations and stale bindings fail closed.

## Consumer requirements

- Add health evidence schema.
- Add Region subrange consequence transitions.
- Add quarantine/reconcile state machine.
- Add fault injection adapters.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-RAS-PARTIAL-FAILURE`  
**Property-specific claim boundaries:** `ModelOnly/ExecutableAdapter on emulator fault model; hardware RAS requires named platform evidence`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P10-A failure vocabulary.
- P10-B Region consequences.
- P10-C external-operation quarantine.
- P10-D provider fault injection.
- P10-E hardware qualification later.

## Rollback

Disable granular RAS gate; use existing coarse failure/quarantine behavior. Never assume provider loss means safe reclaim.
