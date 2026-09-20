# SingNextOS vNext — staged refactoring roadmap

**Status:** proposed implementation roadmap. Каждая фаза требует отдельной executable qualification; сам план не является runtime/security/performance claim.


## Зафиксированный baseline

План подготовлен относительно следующих актуальных веток на момент анализа:

- **SingNextOS `master`**: `386e247e37f4e252d1bf4fda80a57b49858fb788` (`SipJob upd2`, 2026-09-20).
- **HybridCPU-v2 `master`**: `794c4a53494f503855ac8cf209efab23fde083b2`.

Следующие наборы работ считаются **выполненными prerequisites** и не должны переизобретаться в vNext:

- `docs/Completed/SingCap-Refactoring/` — SingCap-M, единый capability ledger, monotonic constraint algebra, revocation/generation, sealing, ManagedCap admission, Region semantics, exact HybridCPU pinning.
- `docs/SingNextOS-post-SingCap-M-SipJob-roadmap/` — SipJob plan/graph model, generated sentry path, Region edges, composed admission, barriers/async/DAG contours, cache discipline, provider-neutral scheduling semantics и qualification framework.

Source-of-truth order для реализации остаётся прежним:

```text
live code + executable tests
    > normative specifications
    > implementation evidence
    > roadmap/design documents
    > historical/vision material
```


## Жёсткие non-goals

Эта модернизация **не** вводит CHERI pointers, tagged memory, capability tags, tagged cache lines, memory tagging, новые pointer widths или ISA-visible capability registers.

Она **не требует изменений ISA, VLIW bundle format, typed lanes, retire machinery или register model HybridCPU-v2**. Допустимы только provider-neutral additive contracts/adapters, если существующая внешняя semantic boundary недостаточна.

Разрешено заимствовать CHERI-подобные принципы, совместимые с текущей архитектурой SingNextOS:

- monotonic derivation: `Authority(child) subset-of Authority(parent)`;
- fail-closed narrowing;
- non-amplifying split/delegation;
- provenance/lineage;
- explicit sealing/opaque handles на software-уровне;
- checked bounds/constraint algebra.

Нельзя превращать эти принципы в новый аппаратный pointer model.


## Архитектурная цель

vNext расширяет SingNextOS от capability-native domain-host ОС к **authority-native heterogeneous resource OS**, не меняя фундаментальную философию текущего проекта:

```text
WHO may do WHAT
    +
WHO owns WHICH DATA
    +
HOW MUCH / WHEN may resources be consumed
    +
WHERE may a provider execute it
    +
WHEN an effect becomes published system truth
```

Ключевое изменение — first-class `TemporalResourceAuthority`, ортогональная существующим `CapabilityAuthority`, `RegionAuthority`, session/invocation owners и `ExternalOperationAuthority`.

```text
EffectCapability
AND Object/RegionAuthority
AND TemporalResourceAuthority
AND live provider admission/legality
    -> operation may be admitted
```

`ResourceBudgetAuthority` v1 не превращается в permission system. Он остаётся accounting/capacity ledger. vNext добавляет отдельную authority lineage для resource/time rights.

## Что заимствуется из reference systems

- **seL4/MCS:** first-class authority to CPU time, budget/period, temporal isolation, donation через RPC, маленький enforcement core и policy outside core.
- **Singularity:** typed protocol FSM, explicit ownership transfer, manifest/admission discipline, invocation-specific closed authority projection.
- **CHERI (только software-compatible принципы):** monotonic narrowing, non-amplifying derivation, provenance, fail-closed constraint algebra. Никаких tagged pointers/ISA changes.
- **Barrelfish:** explicit provider communication, topology-aware policy, resource-local agents и replicated observations — но **не replicated authority truth**.
- **SingNextOS/HybridCPU-v2:** authority/evidence split, ownership semantics, provider-neutral contracts, double admission, retire/publication separation, exact generation and fail-closed external lifecycle.

## Целевая authority decomposition

```text
CapabilityAuthority       -- may this subject perform semantic effect X?
RegionAuthority           -- who owns/uses bytes and under what mode?
TemporalResourceAuthority -- how much/when may resource class R be consumed?
Session/Invocation owners -- who is talking to whom, in which protocol state?
ExternalOperationAuthority-- what external effect is in flight?
Publication owners        -- when does result become system-visible truth?
Provider/HybridCPU        -- can platform legally execute exact admitted work?
```

## Phase dependency graph

```text
P00 Architectural freeze
 |
 v
P01 Authority algebra + resource taxonomy
 |
 v
P02 Accounting/authority split
 |
 v
P03 TemporalResourceAuthority core
 |
 v
P04 Lease/reservation/settlement lifecycle
 | | +--> P05 SIP + Manifest resource contracts
 |        |
 |        v
 |     P06 Session donation / delegation
 |        |
 +------> P07 ExternalOperation resource binding
          |
          v
       P08 ComputePlanning v2
          |
          v
       P09 HybridCPU co-design integration
          |
          +--> P10 Resource scheduler / provider agents
          |       |
          |       v
          |    P11 Temporal isolation & deadlines
          |
          +--> P12 SipJob resource-aware composition
          |
          +--> P13 DMA/CXL/network/occupancy classes
                  |
                  v
               P14 Restart/checkpoint/reconciliation
                  |
                  v
               P15 Observability/audit
                  |
                  v
               P16 Qualification/performance/claims
                  |
                  v
               P17 Migration/cleanup/final cutover
```

## Phase index

| Phase | File | Main result |
|---|---|---|
| P00 | `PHASE_VNEXT_00_ARCHITECTURAL_FREEZE.md` | exact tuple, inherited owners, non-goals |
| P01 | `PHASE_VNEXT_01_AUTHORITY_ALGEBRA_AND_RESOURCE_TAXONOMY.md` | normative model for resource authority |
| P02 | `PHASE_VNEXT_02_ACCOUNTING_AUTHORITY_SPLIT.md` | preserve budget ledger, add authority boundary |
| P03 | `PHASE_VNEXT_03_TEMPORAL_RESOURCE_AUTHORITY_CORE.md` | resource capability lineage/derivation/revocation |
| P04 | `PHASE_VNEXT_04_RESOURCE_LEASE_RESERVATION_SETTLEMENT.md` | no-double-spend leases and settlement |
| P05 | `PHASE_VNEXT_05_SIP_AND_MANIFEST_RESOURCE_CONTRACTS.md` | typed resource requirements/donation metadata |
| P06 | `PHASE_VNEXT_06_ENDPOINTSESSION_RESOURCE_DONATION.md` | RPC/session donation without authority laundering |
| P07 | `PHASE_VNEXT_07_EXTERNAL_OPERATION_RESOURCE_BINDING.md` | bind lease to exact external effect |
| P08 | `PHASE_VNEXT_08_COMPUTE_PLANNING_V2.md` | semantic execution/resource envelopes |
| P09 | `PHASE_VNEXT_09_HYBRIDCPU_CO_DESIGN_INTEGRATION.md` | provider-neutral HybridCPU integration, no ISA changes |
| P10 | `PHASE_VNEXT_10_RESOURCE_SCHEDULER_AND_PROVIDER_AGENTS.md` | policy/placement layer outside authority core |
| P11 | `PHASE_VNEXT_11_TEMPORAL_ISOLATION_DEADLINES_PREEMPTION.md` | budget/period/replenishment/deadline semantics |
| P12 | `PHASE_VNEXT_12_SIPJOB_RESOURCE_AWARE_COMPOSITION.md` | budget-aware fused jobs preserving owners |
| P13 | `PHASE_VNEXT_13_IO_FABRIC_AND_OCCUPANCY_RESOURCE_CLASSES.md` | DMA/CXL/network/device-memory authorities |
| P14 | `PHASE_VNEXT_14_RESTART_CHECKPOINT_AND_RECONCILIATION.md` | stale/ambiguous resource safety |
| P15 | `PHASE_VNEXT_15_OBSERVABILITY_AUDIT_AND_TELEMETRY.md` | non-authoritative resource observability |
| P16 | `PHASE_VNEXT_16_QUALIFICATION_PERFORMANCE_AND_CLAIMS.md` | proof/claim closure |
| P17 | `PHASE_VNEXT_17_MIGRATION_CLEANUP_AND_CUTOVER.md` | additive rollout and cleanup |

Cross-cutting normative requirements are in `VNEXT_NORMATIVE_INVARIANTS.md`; feature promotion rules are in `VNEXT_FEATURE_GATES.md`; requirement-to-phase mapping is in `TRACEABILITY_MATRIX.md`.


## Сквозные правила

1. `identity != authority`.
2. `discovery != authority`.
3. `mapping != ownership`.
4. `budget/accounting != effect authority`.
5. `provider evidence != SingNext authority`.
6. `completion != visibility != publication != release`.
7. `SipJob plan/cache/hint != authority`.
8. Не допускается второй независимый capability ledger.
9. `RegionAuthority` остаётся единственной истиной ownership/use памяти.
10. Provider-private lane/opcode/token/topology не выходит в ManagedCap/SIP ABI.
11. Любой новый cross-project semantic contract versioned, fail-closed и qualified отдельно.
12. Unknown/ambiguous external state удерживает связанные ресурсы/leases до exact closure либо доказанного containment.
