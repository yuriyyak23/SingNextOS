# P17 — MIGRATION CLEANUP AND CUTOVER

## Purpose

Roll out the corrected architecture additively, support mixed versions, and remove compatibility paths only after executable proof that no live consumer depends on them.

## Preconditions

- P16 qualified exact contours.

## Architectural decisions

- Keep old budget/accounting contracts working while resource grants/leases are opt-in.
- Ordinary SIP resource path precedes SipJob resource path.
- Host/model precedes HybridCPU executable contour; ComputeTime precedes non-compute families.
- Provider contract evolution is additive/versioned; mixed old/new providers fail closed to the old semantic contour rather than guessing.
- Old manifests/generated SIP code remain supported during dual-stack window.
- Remove temporary adapters/duplicate validation only after usage scan + mixed-version tests prove no consumers.

## State / linearization model

Migration state is feature-gate/configuration state. Authoritative owners do not move during cutover; only callers switch to new validated paths.

## Negative-space obligations

- old SIP caller -> new service;
- new caller -> old provider;
- mixed contract versions in cluster/domain;
- rollback after partial rollout;
- old manifest requests unsupported resource feature;
- gate disabled with in-flight operation;
- cleanup deletes path still used by live consumer.

## Required executable tests

- Dual-stack compatibility matrix.
- Mixed-version provider/OS tests.
- Rollback with in-flight operations.
- Old manifest/generated-code tests.
- No-live-consumer proof before removal.
- Final regression that legacy gate-off path retains previous semantics.

## Expected code / contract owners

- versioned SIP/manifest contracts
- feature-gate/configuration system
- provider adapter version negotiation
- documentation/evidence archival

## Claim boundary

`ProductionQualified`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Cutover preserves single owners.
- Rollback path tested.
- No compatibility owner/path removed while live consumers exist.
- Completed docs may move under `docs/Completed/` with exact evidence tuple.

## Prerequisite for next phase

Terminal phase. Future resource/guarantee contours start as new gated work, not silent expansion of this qualification.

## Current disposition

P17 is **CLOSED FOR THE EXACT HOST CONTOUR** at `RuntimeEnforced`; `ProductionQualified remains false`.

The additive dual-stack policy accepts legacy SIP v1 without resource intent, exact resource-aware SIP v2 with provider resource contract v1, and an absent/v0 resource contract as the legacy provider contour. Optional work may use ordinary fallback before submission. Required resource semantics and unknown versions fail closed. Gate rollback after possible submit yields `Quarantine` and requires exact reconciliation rather than fallback or resubmission.

Old manifest canonical shape, old generated/ordinary SIP transport and old provider behavior remain supported. No compatibility path was removed. `VNextCompatibilityUsageRegistry` can produce a generation-bound no-live-consumer proof only after all tracked legacy consumers drain; any new consumer immediately invalidates the proof. Future cleanup must independently re-run that proof before deleting a path.

See `EVIDENCE_P17_MIGRATION_CLEANUP_AND_CUTOVER.md`, `P17_MIGRATION_CUTOVER_20260922.json` and `P17_QUALIFICATION_TUPLE.json`. All gates remain default OFF and the ISA-change category is empty. HybridCPU/provider-source, NativeAOT, temporal upper-bound and guaranteed-reservation contours remain unqualified FutureGated work.

## Sequential re-audit — 2026-09-22

`P17_REAUDIT_20260922.md` and its tuple retain the host-only `RuntimeEnforced` claim. Focused P17 tests passed 16/16 after making compatibility-registry generation exhaustion atomic/fail-closed and rejecting malformed proof digests without throwing. Final solution build completed with 0 warnings/errors and the complete non-GUI suite passed 1,767 tests with 0 failures and 2 existing explicit skips. No compatibility path was removed, and a local empty-registry proof is not evidence that production has no live consumer. All gates remain default OFF and all provider, NativeAOT, temporal, guarantee and hardware exclusions remain in force.
