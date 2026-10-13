> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/15-P06-INFORMATION-FLOW-CONTROL.md).

# P06 — Protected-Contour Information-Flow Control

## Scope

De-scope from generic IFC subsystem to an optional protected-contour label policy. Do not create a second capability system.

### Target semantic requirements

- Minimal confidentiality/integrity lattice for selected contour.
- Propagation rules across Region/value projections.
- Explicit declassify/endorse effects authorized by existing capabilities.
- Accelerator intermediate/output label handling.

### CONTRADICTIONS

- Label must not grant access.
- SecureDomain identity/measurement must not imply information-flow permission.

### Excluded designs

- Remove system-wide mandatory IFC from critical path.
- No IFC authority ledger.

### Candidate contract shapes

- DataLabelV1 only for protected contours.
- FlowPolicyV1 and Declassify/Endorse effect descriptors bound to existing capability checks.

### AUTHORITY IMPACT
CapabilityAuthority still owns permission; labels/policy evaluator only restrict admissible flows for enabled contours.

### HYBRIDCPU IMPACT
Likely `SIDEBAND_CONTRACT` only if protected accelerator path must carry labels/evidence; no ISE authority change.

### COMPILER IMPACT
Optional label propagation metadata; compiler does not authorize declassification.

### ISA IMPACT
`NONE`

### SECURITY RISKS

- Label laundering through unlabeled intermediate/copy.
- Declassification without capability authorization.
- Integrity label confused with trust/attestation.

### CORRECTNESS RISKS

- Join/meet mismatch across versions.
- Implicit downgrade at provider boundary.
- SipJob fusion drops label transition.

### PERFORMANCE RISKS

- Label tracking overhead may be significant; enable only on protected contours until measured.

### Applicable behavior and qualification tests

- Propagation property tests.
- Unknown label/policy version deny.
- Declassify/endorse without capability negative tests.
- SipJob fusion/barrier label tests.
- SecureCompute accelerator intermediate tests.

### Property-specific model options

- Finite lattice model / Alloy.
- Noninterference model only for narrowly selected contour; do not claim whole-system noninterference.

### DEPENDENCIES
P01 for data/Region projection semantics. SecureCompute/provider contour is a soft dependency. Not on core v6 critical path.

### EXIT CRITERIA

- A protected contour has executable label propagation and explicit authorized declassification.
- No application can obtain authority solely from a label.
- Whole-system IFC remains unclaimed.

## Consumer requirements

- Add minimal label/policy sidecars behind gate.
- Integrate with selected SIP/SipJob/provider path.
- Add explicit capability-gated declassify/endorse effects.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-IFC`  
**Property-specific claim boundaries:** `ModelOnly/StaticAdmission initially; RuntimeEnforced only for named protected contour`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P06-A minimal lattice spec.
- P06-B protected Region/value labels.
- P06-C SIP/SipJob propagation.
- P06-D accelerator contour.
- P06-E security qualification.

## Rollback

Disable IFC gate and use existing capability/sealing/SecureCompute isolation. No label is retained as authority.
