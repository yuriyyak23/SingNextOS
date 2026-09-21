# SingNextOS — Semantic CPU/OS Co-design Refactoring Package

**Purpose:** implementation-ready phased modernization plan for strengthening the semantic CPU/OS contract between SingNextOS and HybridCPU-v2 without collapsing independent authority domains.

## Frozen planning baseline

| Item | Verified value |
|---|---|
| SingNextOS `master` | `1890a8e921cfe903b5b44857e4661168bf7bbceb` |
| HybridCPU-v2 `master` | `794c4a53494f503855ac8cf209efab23fde083b2` |
| Planning date | 2026-09-21 (Europe/Warsaw) |
| SingNextOS SDK | `11.0.100-rc.1.26425.128` |
| HybridCPU-v2 SDK | `10.0.201` |
| SingNext runtime contract dependency | `HybridCPU.ExternalRuntime.Contracts [1.14.0]` |
| HybridCPU Contracts package project | `1.14.0`, `net11.0` |
| External operation schema | `ExternalOperationContract.Version = 1.4.0` |
| Known Contracts nupkg SHA-256 | `B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2` |
| Known Contracts NuGet content hash | `wLH8suR5xnj9C0CmqKTvUBSp2fLQsnS18ELZbILGosnGU3KIRJTZykX6bfvL/IWNOFOzGmQVvKI3q7irmjeM/Q==` |
| Known `HybridCPU.ExternalRuntime 1.3.0` SHA-256 | `191A1976DECAF607425B3F93378BA11446B32AF2EBFD09DFF45A26944E7E765F` |

> Package digests above are existing qualification evidence and MUST be recomputed at P00. Same SemVer with different bytes is a different artifact and blocks promotion.

## Verification labels

- **VERIFIED_EXISTING** — exact current repository path/symbol/test exists on the frozen HEAD and was inspected or located in the live tree/search.
- **VERIFIED_GAP** — live source/search confirms the proposed semantic surface is absent or insufficient.
- **NEW_PROPOSED** — proposed code/API/file; it does not exist today and MUST NOT be cited as current behavior.
- **TEST_ONLY**, **DOC_ONLY**, **FORMAL_ONLY** — evidence work; never authority.

## Corrections incorporated from the audit

1. Resource charging is **not retire-only**. Current SingNextOS binds resource state to an ExternalOperation, transitions resource binding to `Consuming` after `Submitted`, and settles from exact provider usage evidence after completion/visibility/publication. Retire may become a measurement source, but is not the universal accounting linearization point.
2. `OperationObligations` MUST be an immutable semantic descriptor derived from current authoritative owner snapshots. It is not a signed/minted authority object.
3. `ExecutionGuarantees` are not produced by a single new authority. They are provider/runtime claims whose enforceability is contour-specific and whose evidence never substitutes OS authority or HybridCPU runtime legality.
4. HybridCPU MUST NOT directly “wait for a SingNext PublishPermit”. SingNext owns the publication decision. For a staged contour, the adapter invokes a provider publication fence/gate only after the exact OS publication decision.
5. SingNextOS already has an unrelated internal `SecureExecutionBinding`. Therefore this plan uses provisional name **`SemanticExecutionBinding`** to avoid a semantic collision.
6. HybridCPU already has `ExternalOperationAdmissionBinding` combining an exact CPU guard with provider admission; the new co-design layer composes OS obligations/refinement around this seam rather than replacing runtime legality.
7. Current vNext feature gates exist but `IsEnabled(...)` always returns `false`; all new contours remain default-off until evidence promotion.

## Target admission rule

```text
AuthorizedBySingNext
AND ProviderAdmission
AND GuaranteesRefineObligations
AND HybridCpuRuntimeLegal
    -> execution may cross the contour's declared irreversible boundary
```

The conjunction does not create a universal owner. Every conjunct remains owned and linearized by its current authority domain.

## Package layout

- `01_BASELINE_AND_CODE_AUDIT.md` — de-facto code verification.
- `02_MASTER_REFACTORING_ROADMAP.md` — dependency graph and maturity path.
- `03_NORMATIVE_INVARIANTS.md` — spec-ready invariants.
- `04_TARGET_ARCHITECTURE.md` — target semantic surfaces and negative space.
- `05_AUTHORITY_OWNER_MAP.md` — owner map before/after.
- `06_STATE_MACHINE_AND_TRANSITIONS.md` — global composition model.
- `07_CONTRACT_AND_VERSIONING_STRATEGY.md` — additive ABI/versioning rules.
- `08_TEST_AND_FORMAL_QUALIFICATION.md` — evidence strategy.
- `09_ADVERSARIAL_MATRIX.md` — mandatory attacks/races.
- `10_CROSS_REPO_DELTA.md` — minimal SingNext/HybridCPU changes.
- `11_MIGRATION_ROLLBACK.md` — staged rollout.
- `12_REJECTED_ALTERNATIVES.md` — rejected coupling designs.
- `13_CODEBASE_VERIFICATION.md` — verified symbols/gaps used by every task.
- `MATRIX_MULTIPLY_WORKED_EXAMPLE.md` — end-to-end reference contour.
- `PHASE_P00_...` through `PHASE_P18_...` — phase plans.
- `TZ_P00_...` through `TZ_P18_...` — task-level implementation specifications.
- `TASK_INDEX.md`, `TRACEABILITY_MATRIX.md`, `OPEN_QUESTIONS.md`.

No phase in this package requires an ISA change.
