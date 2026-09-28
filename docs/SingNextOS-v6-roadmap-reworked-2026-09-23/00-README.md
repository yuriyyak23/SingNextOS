# SingNextOS v6 Architectural Refactoring Roadmap — Corrected Implementation Package

**Status:** implementation-oriented architecture package after adversarial cross-repository audit.  
**Freeze date:** 2026-09-23.  
**SingNextOS master:** `690913e3500956aeb18d9823b35e9e64d40e42d7`.  
**HybridCPU-v2 master:** `794c4a53494f503855ac8cf209efab23fde083b2`.  
**Baseline status:** `DRIFT_NON_MATERIAL` — the SingNextOS roadmap originally pinned `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; the current master is one commit ahead and that commit adds the v6 roadmap package without changing runtime/source code. Exact qualification tuples nevertheless use the current SHA.

## 1. Purpose

This package replaces the original v6 proposal with an implementation plan whose claims are bounded by live code, executable tests, package/runtime contracts, and explicit qualification evidence. It intentionally does **not** treat documentation, DTOs, enums, parsers, helpers, fake providers, telemetry, receipts, certificates, or fixtures as runtime enforcement.

The package is designed around one non-negotiable rule:

```text
one logical fact -> one authoritative owner
```

The target composition remains:

```text
SingNextOS
  owns semantic/effect permission, Region ownership/use,
  quantitative resource accounting, publication and reclaim.

HybridCPU-v2 / provider
  owns current machine/runtime legality, provider-local admission,
  execution, retire, completion/visibility/containment evidence.

Compiler
  emits lowering evidence and optional verifiable semantic facts,
  but never permission and never runtime legality.

Refinement
  proves that a concrete provider/runtime guarantee satisfies
  the operation's semantic obligations for an exact tuple.
```

The admission formula is preserved and made explicit:

```text
AuthorizedBySingNext
AND ProviderAdmission
AND GuaranteesRefineObligations
AND HybridCpuRuntimeLegal
```

No term may be inferred from another.

## 2. Audit-driven corrections

The rework makes the following structural corrections:

1. No blanket `OperationObligationsV2` / `ExecutionGuaranteesV2` / `SemanticExecutionBindingV2` migration is started merely to add orthogonal semantics. V1 remains the stable base; additive sidecar extension families are preferred. A true V2 is deferred until a breaking representation requirement is demonstrated.
2. No `TemporalAuthority`, `PowerAuthority`, `ProofAuthority`, `TopologyAuthority`, `TranslationAuthority`, or distributed capability database is introduced.
3. P01 memory ownership/ordering and P04 translation/DMA remain separate because they have different authoritative owners. P04 depends on P01 but does not absorb it.
4. P07 locality/data motion and P08 preemption remain separate. Their accidental scheduling overlap is removed; locality is advisory policy/evidence, while preemption is an execution-lifecycle contract.
5. P05 machine-checkable refinement becomes a cross-cutting semantic spine. It starts early with a small mathematical core and closes individual contours incrementally; it is not a late theorem-prover project.
6. P06 IFC is optional and contour-gated, initially limited to protected/SecureCompute flows. It is not a second capability system.
7. P11 multi-host is removed from the single-host critical path and remains `FutureGated` until single-host memory, DMA, failure and recovery semantics are closed.
8. No current phase has a demonstrated need for a new ISA instruction. Any future ISA proposal requires a separate enforcement-gap ADR.
9. The first qualification vertical is deliberately narrow: single-host, staged output, read-only input, exclusive output, one budget, concrete HybridCPU/provider adapter, no shared mutable memory, no persistence, no hard real-time, no multi-host.
10. Claim promotion is tuple-specific and monotonic only through evidence; `ModelOnly` never implies `RuntimeEnforced`, and emulator evidence never implies physical hardware ordering, persistence, DMA isolation, attestation, RAS or production security.

## 3. Package map

| File | Role |
|---|---|
| `01-TECHNICAL-SPECIFICATION.md` | normative target architecture and contract rules |
| `02-AUTHORITY-OWNER-MAP.md` | reconstructed owner/generation/linearization map |
| `03-PHASE-DAG-AND-MIGRATION.md` | corrected dependency graph and migration sequence |
| `04-NORMATIVE-INVARIANTS.md` | invariants that every phase and PR must preserve |
| `05-FEATURE-GATES-AND-CLAIMS.md` | gates, claim ceilings and promotion rules |
| `06-BASELINE-FREEZE.md` | exact source/toolchain/package/runtime freeze |
| `07-GLOBAL-CONTRACT-EVOLUTION.md` | V1 additive evolution; V2 criteria |
| `08-FORMAL-SEMANTICS-CORE.md` | minimum mathematical/refinement core |
| `09-AUDIT-CORRECTION-LOG.md` | accepted/rejected/modified audit recommendations |
| `10`–`21` | corrected plans/TZ for P01–P12 |
| `22-PROOF-CARRYING-LOWERING.md` | bounded PCL plan |
| `23-FIRST-QUALIFICATION-VERTICAL.md` | first executable vertical |
| `30-CROSS-PROJECT-CONTRACTS.md` | SingNextOS ↔ HybridCPU-v2 contract boundary |
| `31-QUALIFICATION-AND-TEST-MATRIX.md` | required evidence per phase |
| `32-TRACEABILITY-MATRIX.md` | requirement → owner → phase → evidence |
| `33-PR-SLICING-AND-CUTOVER.md` | mergeable implementation slices |
| `34-NON-GOALS-AND-OPEN-QUESTIONS.md` | deliberately deferred scope |
| `35-LIVE-SOURCE-ANCHORS.md` | source/test/package anchors at freeze |
| `36-FULL-GAP-MATRIX.md` | roadmap claim vs live implementation gaps |
| `37-CLAIM-MATRIX.md` | current and target claim ceilings |
| `38-CROSS-PROJECT-DELTA.md` | required deltas by repository/layer |
| `39-FINAL-ARCHITECTURE-VERDICT.md` | final answers and GO/NO-GO matrix |
| `40-IMPLEMENTATION-ORDER.md` | practical PR execution order |

## 4. Source-of-truth order

```text
live source code
+ executable tests
+ qualification/conformance artifacts
+ current package/runtime contracts
    > current normative specifications
    > implementation evidence documents
    > this roadmap
    > historical roadmaps / vision documents
```

If this package conflicts with live code, the package is stale and must be patched before implementation proceeds.

## 5. Classification vocabulary

Every substantial statement is tagged conceptually as one of:

```text
VERIFIED_EXISTING
PARTIALLY_EXISTING
MODEL_ONLY
NEW_PROPOSED
DEFERRED
UNSUPPORTED
CONTRADICTED_BY_LIVE_CODE
REMOVE_OR_MERGE
```

The same discipline applies to every future PR description and qualification report.
