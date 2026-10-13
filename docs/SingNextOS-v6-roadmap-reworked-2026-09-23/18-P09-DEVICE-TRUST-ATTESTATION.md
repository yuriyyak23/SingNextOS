> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/18-P09-DEVICE-TRUST-ATTESTATION.md).

# P09 — Device Trust and Attestation Composition

## Scope

Use attestation only as a predicate input to policy. Bind freshness to device/provider generations; never treat evidence as permission.

### Target semantic requirements

- DeviceIdentityEvidenceV1 provider-neutral envelope.
- Freshness/measurement/policy generation binding.
- Secure assignment/reset/reconfiguration state.
- Revalidation point when trust generation changes.

### CONTRADICTIONS

- Attestation != capability.
- Measurement success != secure device assignment or Region ownership.

### Excluded designs

- No TrustAuthority or universal attestation ledger.

### Candidate contract shapes

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

### Applicable behavior and qualification tests

- Replayed evidence negative test.
- Reset after admission.
- Wrong device/firmware measurement.
- Secure assignment mismatch.
- Unknown mandatory assurance class.
- Evidence loss after submit.

### Property-specific model options

- TLA+ small model for admission/reset/evidence freshness.

### DEPENDENCIES
Device/provider generation vocabulary from P04/P10 is a strong prerequisite for secure assignment. SecureCompute contour is optional. Not first vertical.

### EXIT CRITERIA

- Evidence freshness is generation-bound.
- No trust evidence grants authority.
- Production security claim names a real trust producer/device/firmware tuple.

## Consumer requirements

- Add trust obligation/evidence sidecars.
- Add provider trust adapter.
- Bind to reset/assignment generations.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-DEVICE-ATTESTATION`  
**Property-specific claim boundaries:** `StaticAdmission/ExecutableAdapter; ProductionQualified only on named hardware trust stack`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P09-A schema/policy.
- P09-B provider adapter.
- P09-C reset/freshness integration.
- P09-D physical qualification later.

## Rollback

Disable trust gate; protected operation falls back to a contour whose policy does not require device attestation, or is denied. Never downgrade silently.
