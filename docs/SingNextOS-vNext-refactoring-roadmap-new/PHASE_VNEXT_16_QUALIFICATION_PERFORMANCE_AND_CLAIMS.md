# P16 — QUALIFICATION PERFORMANCE AND CLAIMS

## Purpose

Qualify every enabled contour against exact source/package/provider/runtime/test evidence and prevent proof transfer between models, providers, resource classes or execution modes.

## Preconditions

- P15 closed.

## Architectural decisions

- Every claim record includes exact SingNext SHA, HybridCPU/provider SHA/package digest, contract version, runtime profile, feature gate and test IDs.
- Separate lanes: model/property, runtime authority, concurrency, external effect, SipJob differential, provider adapter, temporal upper-bound/guarantee, restart, performance.
- No fake/provider-parser/DTO evidence promotes executable claims.
- Performance reports separate each overhead component and include failure/quarantine pressure.
- Production qualification requires security regressions and rollback path.

## State / linearization model

Qualification records are evidence artifacts, not runtime authority. Gate promotion is a release/configuration decision tied to an exact evidence tuple.

## Negative-space obligations

- model -> runtime claim transfer;
- host -> HybridCPU claim transfer;
- one provider -> all providers;
- time resource -> bandwidth/occupancy;
- JIT -> NativeAOT;
- average latency -> deadline/guarantee;
- upper bound -> guaranteed minimum capacity.

## Required executable tests

- CI evidence manifest validation.
- Unit/property/race/restart/fault/provider-ambiguity/differential/integration/qualification/performance suites.
- Gate rollback smoke tests.
- Contention tests across thread/domain/SMT/provider counts.
- Negative documentation/claim linting for unsupported words/levels.

## Expected code / contract owners

- CI/release qualification framework
- all prior phase test owners
- provider package/source pinning

## Claim boundary

`ProductionQualified`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Every ON gate has exact executable evidence and exclusions.
- Unsupported gates remain OFF.
- ISA-change category empty.

## Prerequisite for next phase

P17 cutover may remove transitional code only when P16 proves no live dependency on it.

## Current disposition

Qualification closure is fail-closed at `StaticAdmission`; see `EVIDENCE_P16_QUALIFICATION_CLAIM_CLOSURE.md`, `P16_QUALIFICATION_TUPLE.json` and `VNEXT_TRACEABILITY.json`. All gates remain OFF and the ISA-change category is empty. `ProductionQualified` is withheld because the full suite is not green and required provider/performance/NativeAOT evidence is absent. P17 may therefore exercise additive compatibility and rollback only; it may not remove transitional paths.
