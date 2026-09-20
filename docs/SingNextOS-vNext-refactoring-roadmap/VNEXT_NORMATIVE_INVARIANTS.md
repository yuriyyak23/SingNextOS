# SingNextOS vNext normative invariants


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


Keywords **MUST**, **MUST NOT**, **SHOULD**, **MAY** are normative.

## VNX-001 — No new universal authority ledger

`TemporalResourceAuthority` owns only resource/time authority. It MUST NOT absorb `CapabilityAuthority`, `RegionAuthority`, `EndpointSessionRegistry`, `ExternalOperationAuthority`, sealing, process identity, provider legality or publication truth.

## VNX-002 — Accounting is not authority

Existing `BudgetAccountHandle`, `BudgetReservationHandle`, `BudgetAccountSnapshot` and `BudgetReservationSnapshot` remain accounting/capacity objects. A budget snapshot or configured limit MUST NOT authorize an effect or mint resource authority.

## VNX-003 — Resource authority is orthogonal to effect authority

Possession of a `ResourceBudgetCapability` MUST NOT imply `Compute.Execute`, DMA, network, virtualization, CXL, device or service permission.

```text
ResourceAuthority != EffectAuthority
```

## VNX-004 — Monotonic derivation

For every derivation:

```text
Authority(child) subset-of Authority(parent)
```

Subset relation MUST include resource class, amount, time window, period, burst, concurrency, provider constraints, assurance class, delegation depth, subject constraints and validity interval.

## VNX-005 — Conservation / no inflation

A parent MUST NOT create simultaneously consumable child authority whose aggregate envelope exceeds the amount it reserved for delegation. Implement with shared quota lineage or atomic reservation transfer; independent descendant counters are forbidden.

## VNX-006 — No double spend

One exact resource lease/reservation generation MUST bind to at most one active consuming operation unless the lease contract explicitly models divisible subleases. Copying a public struct does not clone authority.

## VNX-007 — Generation and realm exactness

Resource capabilities and leases MUST be authority-realm, subject-generation and resource-generation bound. Runtime/service/provider restart MUST invalidate stale authority unless an explicit re-authorization protocol exists.

## VNX-008 — Donation cannot amplify

IPC/SIP donation MUST preserve provenance and MUST NOT raise resource class, maximum priority, assurance, amount, period, validity or provider set.

## VNX-009 — QoS is not authority

`AdmissionQosHint`, scheduling priority, topology preference and provider performance evidence remain policy/evidence only. They cannot substitute for a live resource capability.

## VNX-010 — Resource capability is not a guarantee

Authority to consume a maximum amount does not imply minimum service. Claims distinguish:

```text
AccountingOnly
EnforcedUpperBound
GuaranteedReservation
```

Promotion between classes requires separate executable evidence.

## VNX-011 — Provider evidence does not settle itself

Provider usage/completion/time receipts are evidence. Only SingNext authoritative settlement code may charge, refund, quarantine or release a local resource lease.

## VNX-012 — Unknown external consumption fails closed

After submission, provider loss/timeout/ambiguity MUST NOT automatically refund budget. The lease remains pinned/quarantined until exact closure, bounded worst-case settlement or trusted effect containment.

## VNX-013 — Budget exhaustion does not rewrite effect truth

After an external effect boundary:

```text
BudgetExhausted => NoFurtherResourceAdmission
```

but MUST NOT imply `EffectDidNotHappen`. Existing completion/visibility/publication/release semantics remain authoritative.

## VNX-014 — Region safety stays independent

Resource settlement MUST NOT release `RegionUse`, transfer ownership, publish output or authorize reclaim. Region lifecycle remains owned by `RegionAuthority` and external-operation closure logic.

## VNX-015 — SipJob is not a resource authority owner

`SipJobPlan`, graph descriptors, caches, execution classes and fused stage metadata may reference where resource authority is validated but MUST NOT cache `authorized=true` or own budget lineage.

## VNX-016 — Provider-private topology remains private

No public resource capability or SIP contract may contain HybridCPU lane IDs, opcodes, physical slot indexes, internal DSC tokens, provider queue IDs, CXL HDM/DPA topology, IOMMU private handles or similar placement details.

## VNX-017 — HybridCPU legality remains independent

Resource authority never makes a HybridCPU operation legal. CPU/runtime guard and provider admission remain independent from SingNext local authority.

## VNX-018 — Retire, completion, settlement and publication remain distinct

For accelerator paths the implementation MUST preserve at least:

```text
local resource reservation
provider admission
submission
CPU/provider execution/retire as applicable
DeviceComplete
resource usage settlement
Visible
Published
Released
```

No implementation may collapse these merely for performance.

## VNX-019 — Replicated evidence, singular authority

Topology, queue depth, load, latency and thermal/performance state MAY be replicated/cached by provider agents. Ownership/capability/lease lineage MUST retain a single authoritative local truth.

## VNX-020 — No CHERI pointer architecture

No vNext requirement may require tagged pointers, tagged memory, capability registers, memory tag propagation or changes to HybridCPU ISA/microarchitecture. CHERI-derived concepts are software constraint/derivation rules only.

## VNX-021 — No service/provider code under authority locks

Reserve/derive/revoke/bind transitions MUST linearize inside authority owners, then release locks before invoking user service code, provider code, callbacks or continuations.

## VNX-022 — Cross-project semantics are versioned

If HybridCPU ExternalRuntime contracts cannot represent a required semantic resource request/usage receipt losslessly, add an explicit additive versioned provider-neutral contract. Never smuggle semantics into opaque strings, hints or existing fields.

## VNX-023 — Feature gates are contour-specific

Evidence for CPU time does not qualify MatrixCompute; evidence for Host model does not qualify HybridCPU executable path; evidence for accounting does not qualify enforced upper bounds; evidence for upper bounds does not qualify hard guarantees.

## VNX-024 — No authority laundering through brokers

Resource brokers/planners may choose allocation policy but cannot enlarge delegated authority, reinterpret lower assurance as higher assurance, or substitute their identity for the original lineage.

## VNX-025 — Deterministic audit correlation

Every admitted resource-consuming operation SHOULD be traceable through a stable correlation chain:

```text
Authority lineage -> reservation/lease -> invocation/external operation -> provider receipt -> settlement
```

Trace data is evidence, not authority.
