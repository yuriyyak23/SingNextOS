# P15 — OBSERVABILITY AUDIT AND TELEMETRY

## Purpose

Expose decision and lifecycle evidence for debugging/qualification while guaranteeing telemetry cannot mint, validate, settle, publish or release authority.

## Preconditions

- P14 closed.

## Architectural decisions

- Emit semantic events for grant derivation/revocation, reserve/bind/consume/settle/quarantine, donation, provider ambiguity and deadline/upper-bound miss.
- Correlation IDs are opaque projections, not reusable handles.
- Cross-tenant/provider-private topology is redacted.
- Metrics separate admission, lock/linearization, scheduler, provider, execution, visibility/publication and settlement latency.

## State / linearization model

Telemetry has no authoritative state transitions. Dropped/duplicated/reordered events cannot change owner state.

## Negative-space obligations

- replayed telemetry event triggers settlement;
- monitoring consumer treats snapshot as freshness proof;
- log leaks provider-private token or raw capability handle;
- telemetry backpressure changes authority ordering;
- duplicate metrics interpreted as duplicate consumption.

## Required executable tests

- Replay/no-side-effect telemetry test.
- Redaction/reflection tests.
- Dropped/reordered event invariance test.
- Audit-only gate comparison: same authoritative state with telemetry on/off.
- Correlation completeness check without exposing secret handles.

## Expected code / contract owners

- telemetry/trace subsystem only
- read-only projections from authoritative owners

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Observability can be disabled with no semantic change.
- No telemetry API mutates owners.

## Prerequisite for next phase

P16 consumes telemetry as evidence only and pins claims to executable tests.

## Current disposition

Closed for the existing host/JIT deterministic-trace replay and structured-telemetry non-interference contour at `RuntimeEnforced`; see `EVIDENCE_P15_OBSERVABILITY_AUTHORITY_BOUNDARY.md` and `P15_QUALIFICATION_TUPLE.json`. `FG-VNX-AUDIT-ONLY` remains OFF. Fine-grained events for still-disabled vNext owner transitions remain FutureGated and are not simulated by telemetry.

## Sequential re-audit (2026-09-22)

Current HEAD is `800027893dcf1ed23d8d7fe775841dd9d01a9fff`. Replay/drop/reorder/backpressure invariance and public redaction were reconfirmed without a production defect. Current evidence is `P15_REAUDIT_20260922.md` with tuple `P15_REAUDIT_20260922_TUPLE.json`; telemetry remains read-only and the gate remains OFF.
