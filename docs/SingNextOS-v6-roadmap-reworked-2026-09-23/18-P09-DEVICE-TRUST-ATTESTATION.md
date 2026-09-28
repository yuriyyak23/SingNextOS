# P09 — Device Trust and Attestation Composition

## Structured verdict

**PHASE:** P09 — Device Trust and Attestation Composition  
**BASELINE:** SecureCompute/virtualization evidence contracts exist; generic SPDM/TDISP/IDE physical adapter qualification is not established by model presence.  
**VERDICT:** Use attestation only as a predicate input to policy. Bind freshness to device/provider generations; never treat evidence as permission.

### VERIFIED_EXISTING
- SecureDomain/VirtualDomain separation exists.
- Provider/ExternalRuntime already carries versioned generations and identity/evidence concepts.

### PARTIAL
- Device identity/measurement can be modeled, but production trust roots/freshness/reset semantics are provider/hardware-specific.

### GAPS
- DeviceIdentityEvidenceV1 provider-neutral envelope.
- Freshness/measurement/policy generation binding.
- Secure assignment/reset/reconfiguration state.
- Revalidation point when trust generation changes.

### CONTRADICTIONS
- Attestation != capability.
- Measurement success != secure device assignment or Region ownership.

### REMOVE_OR_MERGE
- No TrustAuthority or universal attestation ledger.

### NEW_REQUIRED
- TrustObligationV1 and TrustEvidenceV1 sidecars as predicates only.
- ProviderTrustGeneration correlated with device lease/reset generation.

### AUTHORITY IMPACT
Policy evaluator decides whether evidence satisfies obligation; actual permission stays in Capability/Region/device owners.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT` in ExternalRuntime/provider adapter; HybridCPU legality remains independent.

### COMPILER IMPACT
None required.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Replay of stale attestation after device reset.
- Confused deputy: trusted device identity used for wrong assignment/Region.
- Evidence downgrade accepted silently.

### CORRECTNESS RISKS
- Trust evidence remains valid after provider generation changed.
- SecureDomain/VirtualDomain generation mismatch.

### PERFORMANCE RISKS
- Attestation handshakes can add latency; cache only immutable/freshness-bounded evidence.

### REQUIRED TESTS
- Replayed evidence negative test.
- Reset after admission.
- Wrong device/firmware measurement.
- Secure assignment mismatch.
- Unknown mandatory assurance class.
- Evidence loss after submit.

### FORMAL WORK
- TLA+ small model for admission/reset/evidence freshness.

### DEPENDENCIES
Device/provider generation vocabulary from P04/P10 is a strong prerequisite for secure assignment. SecureCompute contour is optional. Not first vertical.

### EXIT CRITERIA
- Evidence freshness is generation-bound.
- No trust evidence grants authority.
- Production security claim names a real trust producer/device/firmware tuple.

### ROADMAP PATCH
Keep P09 as predicate composition. Separate model/emulator evidence from physical attestation qualification.

## Objective

Use attestation only as a predicate input to policy. Bind freshness to device/provider generations; never treat evidence as permission.

## Live baseline and existing mechanisms
- SecureCompute/Virtualization domain contracts.
- PlatformAuthorityBridge/provider adapter.
- Capability/Region/device lease owners.

## Required implementation changes
- Add trust obligation/evidence sidecars.
- Add provider trust adapter.
- Bind to reset/assignment generations.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-DEVICE-ATTESTATION`  
**Claim ceiling at emulator/runtime freeze:** `StaticAdmission/ExecutableAdapter; ProductionQualified only on named hardware trust stack`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P09-A schema/policy.
- P09-B provider adapter.
- P09-C reset/freshness integration.
- P09-D physical qualification later.

## Rollback

Disable trust gate; protected operation falls back to a contour whose policy does not require device attestation, or is denied. Never downgrade silently.
