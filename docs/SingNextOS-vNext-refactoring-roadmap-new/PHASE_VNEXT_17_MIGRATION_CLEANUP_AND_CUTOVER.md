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
