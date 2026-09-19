# Phase 04 Implementation Evidence — Revocation and Effect Admission

**Date:** 2026-09-19  
**Claim:** `RuntimeEnforced` for bounded subtree revocation, exact capability operation admission, and the native file-open capability/session composition pilot.

## 1. Baseline and preservation

- Local HEAD before P04: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- The worktree already contained P00-P03 work and unrelated documentation moves/deletions. All existing changes were preserved.
- No reset, checkout, clean, force, commit, push or destructive filesystem operation was used.

## 2. Audited dependencies and reused owners

Audited live implementations: `CapabilityAuthority`, `RuntimeKernel`, `EndpointSessionRegistry`, `ExternalOperationAuthority`, `RegionAuthority`, native file/network/process hosts, CXL Type-2 external-operation composition, P02/P03 tests, and the normative Authority Composition Protocol.

Reused authoritative owners:

- `CapabilityAuthority` remains the sole permission, lineage, one-shot and quota owner.
- `EndpointSessionRegistry` remains the sole session lifecycle owner and now issues exact internal pins.
- `ProcessRegistry` remains the subject-incarnation owner and is resolved before admission.
- The native file host remains the service-private file-object catalog; the P04 pilot creates an object only after composed admission.

No Region, sealed-object, completion, publication, provider or resource-budget ledger was added.

## 3. Design decisions

- Selected subtree model: bounded ancestry walk through immutable parent links. Depth is bounded by the P03 delegation constraint; missing, revoked or epoch-stale ancestors deny validation/admission.
- Derive and revoke use the existing capability gate. Child validation/link/insertion is atomic relative to revoke.
- `AcquireOperationAuthority` validates subject/resource generation, exact resource, exact typed operation, lifetime, optional session, complete ancestry, optional quota and one-shot state in one capability-ledger transaction.
- Operation leases are internal, non-serializable, one-attempt runtime objects. A V1 descriptor cannot substitute for one.
- Static closed policy mapping is runtime code, not caller/provider data. The pilot uses `GrandfatherAdmitted` for read-open and `AdmissionOnly` for create/write-open.
- Session composition uses reservation/revalidation: acquire session pin, acquire capability lease, revalidate the pinned session, allocate the correlated attempt, then return with registry locks released.
- Failed preparation disposes capability lease and session pin in reverse acquisition order.

## 4. Authority / evidence / provider split

- Authority remains in the live capability record/lineage, exact operation lease and live session record/pin.
- `EffectAdmissionAttemptId` correlates those live objects; it does not copy their authority.
- Descriptor, trace, inspection and handle values remain evidence/identity only.
- The pilot is an in-runtime file-object creation effect and invokes no external provider. Existing provider admission/completion/publication state remains independent and unchanged.

## 5. Single-ledger proof

Parent links, revocation state, operation lease identities, one-shot transition and quota consumption live inside the existing `CapabilityAuthority`. The operation-lease set tracks live exact admissions; it is not a second capability or completion registry. Session pins live in `EndpointSessionRegistry`, which already owns session lifecycle. The coordinator owns no copied permission/session truth.

## 6. Linearization and lifecycle semantics

- Derivation linearization: parent validation plus child linkage/insertion under the capability gate.
- Revocation linearization: record transition under the same gate. Later descendants fail their ancestry walk.
- Capability operation admission linearization: lease creation, optional quota decrement and optional `Consumed` transition under the capability gate.
- Composed pilot commit: successful final session-pin revalidation after exact capability lease acquisition.
- Session close while pinned transitions to `Draining`; final revalidation denies a mixed-time admission, and releasing the last pin completes closure.
- Revoke before acquisition denies. Revoke after acquisition cannot fabricate rollback; the exact lease follows its closed policy. The synchronous file-open pilot has no provider submission, completion, publication, quarantine or reclaim stage.

Pilot transition table:

| Point | Read-open (`GrandfatherAdmitted`) | Create/write-open (`AdmissionOnly`) |
|---|---|---|
| revoke before admit | denied | denied |
| revoke after admit, before local mutation | exact admitted attempt may finish | exact admitted attempt may finish |
| provider submission/completion | not applicable; no provider | not applicable; no provider |
| publication/release | response lifecycle remains SIP-owned | response lifecycle remains SIP-owned |

## 7. Public/SIP and non-leak status

Operation leases, session pins, policies and admission attempts are internal runtime types. No ambient authority, enumerable capability bag, public resolver or serialized lease was introduced. `RuntimeFileServiceHost.Open` is the real consumer: it no longer relies on a descriptor validation across the object-creation window. Other native operations remain unmigrated and are FutureGated to P06/P08/P11 as applicable.

## 8. Changed files/projects

- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityConstraints.cs` (retargetable subject constraint needed for bounded multi-hop derivation)
- `src/Runtime/SingPlus.Runtime/Capabilities/OperationAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Capabilities/RuntimeKernel.EffectAdmission.cs`
- `src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs`
- `src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs`
- `tests/SingPlus.Tests/Capabilities/SingCapPhase04EffectAdmissionTests.cs`
- this evidence file.

## 9. Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~SingCapPhase04EffectAdmissionTests|FullyQualifiedName~NativeSystemServiceVerticalSliceTests|FullyQualifiedName~SingCapPhase03ConstraintAlgebraTests"
Passed: 36, Failed: 0, Skipped: 0

dotnet build "SingNextOS.slnx"
Build succeeded. Warnings: 0, Errors: 0

dotnet test "SingNextOS.slnx" --no-build
HybridCpu_ExecutableAdapter.Tests: 12 passed
HybridCPU_NeutralRuntime.Tests: 58 passed
SingPlus.Platform.HybridCpu.Tests: 60 passed
SingPlus.Tests: 1126 passed, 2 skipped
Aggregate: 1256 passed, 2 skipped, 0 failed

git diff --check
Exit 0; no whitespace errors. Git emitted only pre-existing LF-to-CRLF working-copy notices.
```

Tests cover deep ancestor revoke, stale snapshot exclusion, validate/revoke/acquire denial, admitted-before-revoke policy, exactly-one one-shot winner, 100 derive/revoke races, session close between prepare/commit, partial acquisition compensation, and re-entry into capability/session registries while an admitted lease is live.

## 10. Claim and limitations

Claim is limited to `RuntimeEnforced` for the implemented P04 primitives and file-open pilot. It is not a general claim that every effectful path has migrated. Region composition, sealed-object pins, generated SIP sentries, provider submission/publication policy integration and all-service migration remain P06-P12 work. P13 contention/deadlock/performance qualification remains required before `ProductionCandidate`.

The optional `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains absent. No content was inferred.

## 11. HybridCPU boundary

HybridCPU core, ISE, ISA/opcodes, architectural registers, compiler, scheduler, load/store, retire and microarchitecture were not changed. No provider receipt or provider generation was treated as SingNext capability authority.
