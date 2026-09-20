# P01 — RESOURCE MODEL AND DIMENSIONAL ALGEBRA

**Live disposition:** closed at `ModelOnly` for immutable contracts on reviewed HEAD `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`. See `EVIDENCE_P01_RESOURCE_MODEL.md` and `P01_QUALIFICATION_TUPLE.json`. All gates remain OFF.

## Purpose

Define the conceptual model before adding handles or admission paths: permission, accounting, reservation, lease, evidence, settlement and guarantee remain distinct.

## Preconditions

- P00 closed.
- Current budget dimensions and capability constraint algebra enumerated from live code.

## Architectural decisions

- Initial executable family is one time resource (`ComputeTimeNs` or exact existing equivalent).
- Define separate time, throughput and occupancy families; no universal scalar.
- Define resource-use constraint family for existing `CapabilityAuthority`, but do not mint it yet.
- Define `AccountingOnly`, `RuntimeEnforced`, `EnforcedUpperBound`, `GuaranteedReservation` as different claims.
- All arithmetic is checked and canonicalization is versioned/fail-closed.

## State / linearization model

Pure model only. No mutable runtime owner is added in P01.

## Negative-space obligations

- Overflow/underflow and `ulong.MaxValue`/zero sentinel ambiguity.
- Period/window multiplication overflow and rounding drift.
- CPU-time vs bytes/window vs occupancy merge attempts.
- Unknown enum/constraint/version.
- Sibling-union amplification in pure constraint algebra.

## Required executable tests

- Property tests for reflexive/transitive subset relation.
- Canonical serialization round-trip and unknown-version fail-closed tests.
- Dimension mismatch tests.
- Boundary arithmetic fuzzing including max values and wrap.

## Expected code / contract owners

- Contracts/model project containing capability constraints and budget dimensions; no provider runtime changes.

## Claim boundary

`ModelOnly`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Resource truth table normative.
- One initial resource class selected.
- No mutable authority/resource owner added.

## Prerequisite for next phase

P02 requires a stable subset relation and explicit decision on how the new constraint family fits existing `CapabilityAuthority`.
