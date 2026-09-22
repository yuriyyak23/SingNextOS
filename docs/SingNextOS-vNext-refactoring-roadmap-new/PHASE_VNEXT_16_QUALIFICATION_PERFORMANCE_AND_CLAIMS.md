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

P16 is **CLOSED FOR THE EXACT HOST CONTOUR** at `ExecutableAdapter`, not `ProductionQualified`. The authoritative closure evidence is `P16_BLOCKER_REMEDIATION_20260922.md` with `P16_BLOCKER_REMEDIATION_20260922_TUPLE.json`. The earlier revalidation, performance, full-suite and remaining-contour artifacts remain historical additive evidence and must not be read as the current disposition.

The closed contour is Windows x64, .NET 11 JIT, opt-in host provider, `ComputeTime/Nanoseconds`, scope `host:compute-v1`. The provider contract, authenticated durable recovery journal, conservative cold-restart reconciliation, host adapter/bridge, generation-bound gate owner, ON→OFF rollback and ordinary/fused SipJob differential are executable. Controlled Windows topology evidence covers same-core SMT siblings and separate physical cores, but remains `AccountingOnly` performance evidence without a latency or realtime threshold.

All gates remain default OFF and the ISA-change category is empty. The canonical qualification command passed: solution build 0 warnings/errors; focused lane 196/196; full non-GUI suite 1,747 passed, 0 failed, 2 explicitly skipped; `git diff --check` exit 0. P17 may start additively, but removal remains forbidden until P17 proves no live consumer.

`P16_FULL_SUITE_REMEDIATION_20260922.md` updates stale consumers after the parallel move to `docs/Completed` and closes the previously recorded nine unrelated failures. After adding the remaining-contour regression, the current mandatory non-GUI suite is green (1706 passed, 0 failed, 2 explicitly skipped). This removes a CI blocker but does not close the remaining contour-specific requirements above.

Temporal upper-bound and guaranteed reservation remain FutureGated/OFF because no current owner enforces actual execution-time containment or minimum provider capacity under contention/loss. HybridCPU provider/source qualification also remains unavailable locally. These exclusions do not transfer the exact host proof and do not support ProductionQualified, NativeAOT, HybridCPU, hardware, QEMU, firmware or CXL claims.

## Sequential re-audit — 2026-09-22

`P16_REAUDIT_20260922.md` and its tuple preserve the exact host-only `ExecutableAdapter` disposition. The focused current P16 lane passed 81/81. The only confirmed defect was stale P14 bytes/hash in the executable historical remaining-contours tuple; it was refreshed without changing that tuple's `Open`, gates-OFF or non-production disposition. Current aggregate non-GUI evidence is 1,765 passed, 0 failed and 2 skipped. All unsupported temporal, guarantee, provider, NativeAOT and hardware contours remain FutureGated.
