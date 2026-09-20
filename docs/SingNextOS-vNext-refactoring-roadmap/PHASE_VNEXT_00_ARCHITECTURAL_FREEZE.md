# P00 — Architectural freeze and inherited baseline

## Goal

Freeze the vNext authority boundary before semantic code changes. Treat SingCap-M and SipJob roadmaps as completed prerequisites and prevent accidental creation of a second capability/scheduling universe.


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


## Required work

1. Record exact SingNextOS/HybridCPU SHAs, package versions/digests and .NET toolchain.
2. Produce an authority ownership table covering:
   - `CapabilityAuthority`;
   - `RegionAuthority`;
   - `ResourceBudgetAuthority` v1;
   - process/service/session/invocation owners;
   - `ExternalOperationAuthority`;
   - response/publication owners;
   - provider/HybridCPU admission/legality owners;
   - SipJob non-authoritative metadata/cache owners.
3. Declare `TemporalResourceAuthority` as a **new narrow owner only for resource/time authority**.
4. Freeze `ResourceBudgetAuthority` v1 behavior: reservations continue to admit accounting capacity and MUST NOT become effect authority.
5. Freeze HybridCPU boundary: no lane/opcode/ISA/pointer changes; only additive provider-neutral contract work may follow later.

## Deliverables

- ADR `VNEXT-AUTHORITY-BOUNDARY`.
- Machine-readable baseline tuple.
- Initial invariant catalog `VNX-*`.
- Feature gates all OFF.
- Negative dependency test preventing ManagedCap/contracts from referencing HybridCPU ISE/internal runtime types.

## Tests

- Reflection/static tests prove budget snapshots still report `MaterializesAuthority=false` / `AuthorizesEffect=false`.
- Dependency test: `SingPlus.Contracts` and public SIP ABI contain no HybridCPU lane/opcode/provider-private types.
- Stale existing capability/session/Region/provider generations remain fail-closed.

## Exit criteria

- no runtime behavior changed;
- exact baseline pinned;
- all existing owners named;
- CHERI-pointer/ISA changes explicitly forbidden;
- vNext feature gates present and off.
