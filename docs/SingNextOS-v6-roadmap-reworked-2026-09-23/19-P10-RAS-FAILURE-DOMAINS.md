# P10 — RAS and Failure-Domain Semantics

## Structured verdict

**PHASE:** P10 — RAS and Failure-Domain Semantics  
**BASELINE:** External operation quarantine/settlement and Region ownership exist; granular provider health/subrange damage semantics are not fully unified.  
**VERDICT:** Extend consequences in existing owners. Provider health is evidence; RegionAuthority owns damaged memory usability; ExternalOperationAuthority owns in-flight ambiguity.

### VERIFIED_EXISTING
- RegionAuthority can represent subranges/ownership/use concepts from prior architecture.
- External operation lifecycle already supports quarantine/reconcile-style closure.
- Provider adapters own device/fabric health observations.

### PARTIAL
- Granular states such as poisoned range/partial unavailability/reconfiguration are not uniformly executable across providers.

### GAPS
- ProviderHealthEvidenceV1 with generation/failure-domain scope.
- Region subrange damage/quarantine transitions.
- In-flight operation consequence rules.
- Remap/reconfigure generation increments.

### CONTRADICTIONS
- Health telemetry must not directly mutate CapabilityAuthority.
- Provider loss does not prove closure/reclaim safety.

### REMOVE_OR_MERGE
- No RasAuthority or second Region ledger.

### NEW_REQUIRED
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

### REQUIRED TESTS
- ECC/poison subrange fault injection.
- Provider reset/path loss in every external-operation state.
- Partial Region loss/remap tests.
- Ambiguous write -> quarantine.
- Reconfiguration generation ABA tests.
- CXL memory degradation model tests.

### FORMAL WORK
- TLA+ provider-loss/in-flight/reclaim model.
- Alloy/finite model for subrange damage ownership.

### DEPENDENCIES
P01 hard; P04 for device/DMA consequences; P05 failure semantics. P02 only for persistent contours. P11 hard-depends on P10.

### EXIT CRITERIA
- Subrange damage is expressible without a new ownership ledger.
- No reclaim occurs while a possible writer is unclosed.
- Reconfiguration increments exact generations and stale bindings fail closed.

### ROADMAP PATCH
Center P10 on consequence ownership and closure, not a new RAS authority hierarchy.

## Objective

Extend consequences in existing owners. Provider health is evidence; RegionAuthority owns damaged memory usability; ExternalOperationAuthority owns in-flight ambiguity.

## Live baseline and existing mechanisms
- RegionAuthority.
- ExternalOperationAuthority.
- Provider/fabric/device owners.

## Required implementation changes
- Add health evidence schema.
- Add Region subrange consequence transitions.
- Add quarantine/reconcile state machine.
- Add fault injection adapters.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-RAS-PARTIAL-FAILURE`  
**Claim ceiling at emulator/runtime freeze:** `ModelOnly/ExecutableAdapter on emulator fault model; hardware RAS requires named platform evidence`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P10-A failure vocabulary.
- P10-B Region consequences.
- P10-C external-operation quarantine.
- P10-D provider fault injection.
- P10-E hardware qualification later.

## Rollback

Disable granular RAS gate; use existing coarse failure/quarantine behavior. Never assume provider loss means safe reclaim.
