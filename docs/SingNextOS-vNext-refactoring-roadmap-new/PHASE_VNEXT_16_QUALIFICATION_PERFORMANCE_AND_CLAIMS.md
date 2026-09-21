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

P16 is **OPEN**. The earlier `EVIDENCE_P16_QUALIFICATION_CLAIM_CLOSURE.md` and `P16_QUALIFICATION_TUPLE.json` are historical partial evidence, not phase closure. Revalidation at HEAD `1890a8e921cfe903b5b44857e4661168bf7bbceb` found nonexistent test references and insufficient Markdown/JSON and source-hash validation. See `P16_REVALIDATION_20260921.md` and `P16_REVALIDATION_20260921_TUPLE.json` for the correction, executed results and outstanding requirements.

`VNEXT_TRACEABILITY.json` now reports `PartialCoverage`. Its validation supports `StaticAdmission` of qualification metadata only. The additive `P16_RESOURCE_BUDGET_PERFORMANCE_20260922.md` and tuple execute one live host/JIT ComputeTime owner contour across 1/2/4/8/16 threads, shared/per-worker process domains, quarantine lifecycle and denial pressure. That artifact is `AccountingOnly` performance evidence: it has no latency threshold and does not prove controlled SMT topology, provider counts, an upper bound or a guarantee.

All gates remain OFF and the ISA-change category is empty. P17 must not start until P16 has the required executable qualification; keeping every gate OFF does not make that qualification vacuously complete. Provider, controlled-SMT, resource-aware SipJob differential, durable restart and live gate-rollback gaps remain explicit and cannot be replaced by a documentation or host-only test.

`P16_FULL_SUITE_REMEDIATION_20260922.md` updates stale consumers after the parallel move to `docs/Completed` and closes the previously recorded nine unrelated failures. After adding the remaining-contour regression, the current mandatory non-GUI suite is green (1706 passed, 0 failed, 2 explicitly skipped). This removes a CI blocker but does not close the remaining contour-specific requirements above.

`P16_REMAINING_CONTOURS_20260922.md` and its machine-readable matrix pin the six remaining FutureGated contours to exact owners, prerequisites, unsafe-bypass reasons, gates and claim ceilings. The local Contracts 1.14.0 package is byte-pinned and executable reflection confirms the resource-envelope/usage-evidence surface is absent; package presence is not provider execution or HybridCPU source verification.
