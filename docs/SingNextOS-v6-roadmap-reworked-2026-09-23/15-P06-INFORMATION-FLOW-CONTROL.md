# P06 — Protected-Contour Information-Flow Control

## Structured verdict

**PHASE:** P06 — Protected-Contour Information-Flow Control  
**BASELINE:** Sealed objects, capabilities and SecureCompute isolation exist; no generic system-wide IFC enforcement is proven.  
**VERDICT:** De-scope from generic IFC subsystem to an optional protected-contour label policy. Do not create a second capability system.

### VERIFIED_EXISTING
- CapabilityAuthority already authorizes declassification-like privileged effects where explicitly modeled.
- SealedObjectAuthority and SecureCompute provide protected data contours.

### PARTIAL
- Label propagation through SIP/SipJob/accelerator intermediates is not a generally enforced runtime feature.

### GAPS
- Minimal confidentiality/integrity lattice for selected contour.
- Propagation rules across Region/value projections.
- Explicit declassify/endorse effects authorized by existing capabilities.
- Accelerator intermediate/output label handling.

### CONTRADICTIONS
- Label must not grant access.
- SecureDomain identity/measurement must not imply information-flow permission.

### REMOVE_OR_MERGE
- Remove system-wide mandatory IFC from critical path.
- No IFC authority ledger.

### NEW_REQUIRED
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

### REQUIRED TESTS
- Propagation property tests.
- Unknown label/policy version deny.
- Declassify/endorse without capability negative tests.
- SipJob fusion/barrier label tests.
- SecureCompute accelerator intermediate tests.

### FORMAL WORK
- Finite lattice model / Alloy.
- Noninterference model only for narrowly selected contour; do not claim whole-system noninterference.

### DEPENDENCIES
P01 for data/Region projection semantics. SecureCompute/provider contour is a soft dependency. Not on core v6 critical path.

### EXIT CRITERIA
- A protected contour has executable label propagation and explicit authorized declassification.
- No application can obtain authority solely from a label.
- Whole-system IFC remains unclaimed.

### ROADMAP PATCH
Reclassify P06 as optional protected-contour security feature, not generic OS authority subsystem.

## Objective

De-scope from generic IFC subsystem to an optional protected-contour label policy. Do not create a second capability system.

## Live baseline and existing mechanisms
- CapabilityAuthority.
- SealedObjectAuthority.
- SecureCompute domain contracts.

## Required implementation changes
- Add minimal label/policy sidecars behind gate.
- Integrate with selected SIP/SipJob/provider path.
- Add explicit capability-gated declassify/endorse effects.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-IFC`  
**Claim ceiling at emulator/runtime freeze:** `ModelOnly/StaticAdmission initially; RuntimeEnforced only for named protected contour`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P06-A minimal lattice spec.
- P06-B protected Region/value labels.
- P06-C SIP/SipJob propagation.
- P06-D accelerator contour.
- P06-E security qualification.

## Rollback

Disable IFC gate and use existing capability/sealing/SecureCompute isolation. No label is retained as authority.
