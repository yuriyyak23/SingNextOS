# P02 — RESOURCE USE GRANTS IN EXISTING CAPABILITYAUTHORITY

**Live disposition:** closed at `RuntimeEnforced` for the internal host/JIT owner contour on reviewed HEAD `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`; the feature gate remains OFF. See `EVIDENCE_P02_RESOURCE_GRANTS.md` and `P02_QUALIFICATION_TUPLE.json`.

## Purpose

Represent permission to consume a bounded resource envelope using the existing SingCap-M capability ledger and monotonic constraint algebra; do not create a new authority ledger.

## Preconditions

- P01 closed.
- SingCap-M derive/revoke/generation tests green.

## Architectural decisions

- Introduce a resource-use operation/constraint family inside existing `CapabilityAuthority` (exact type names follow existing ABI conventions).
- `ResourceBudgetAuthority` remains quantitative/accounting owner and is not converted into effect permission.
- A resource-use grant is opaque, generation-bound and non-amplifying.
- Derivation narrows class/envelope/validity/assurance/provider-semantic scope/delegation depth.
- No budget capacity is consumed merely by deriving a grant; quantitative capacity is committed in P03.

## State / linearization model

Grant lifecycle reuses existing capability lifecycle: Active -> Revoked/Retired with existing realm/incarnation and subject/resource generation semantics. No parallel resource-capability table exists.

## Negative-space obligations

- derive vs revoke race;
- two children whose declarative maxima overlap — allowed only because actual capacity is still committed once in P03; they must not imply duplicated reserved capacity;
- stale realm/process generation;
- forged/public DTO treated as a grant;
- provider-scope or assurance widening during derivation.

## Required executable tests

- Existing capability property suite extended with resource constraints.
- derive/revoke linearization race.
- stale realm/process/resource generation negatives.
- attempted assurance/class/provider widening.
- serialization evidence cannot validate as live authority without ledger lookup.

## Expected code / contract owners

- existing `CapabilityAuthority` implementation and SingCap-M constraint algebra
- ManagedCap/operation-authority admission surfaces only if existing conventions require them

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- No new capability ledger exists.
- Resource-use grant validation is live-ledger based.
- Effect capability and resource-use grant remain independently required.

## Prerequisite for next phase

P03 may use grants only as permission input; all quantitative conservation remains in `ResourceBudgetAuthority`.
