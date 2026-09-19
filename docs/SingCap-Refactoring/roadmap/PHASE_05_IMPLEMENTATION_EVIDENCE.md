# Phase 05 Implementation Evidence — Opaque V2 Compatibility Surface

**Date:** 2026-09-19  
**Claim:** `RuntimeEnforced` opaque V2 reference resolution over the existing capability ledger.

## 1. Baseline and preservation

- HEAD before P05: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- Existing P00-P04 and unrelated dirty-worktree changes were preserved.
- No reset, checkout, clean, force, commit, push or destructive filesystem operation was performed.

## 2. Audited dependencies and reused types

Audited `CapabilityDescriptorV1`, `CapabilityId`, P02 opaque nonce/realm records, P03 quota/constraints, P04 operation leases, RuntimeKernel mint/delegate/validate/revoke, SIP generator behavior, `IFileService`, native file host and related executable tests.

Reused types/owners: `AuthorityRealmId`, the P02 record nonce, the sole `CapabilityAuthority` record dictionary, `RuntimeKernel`, generated SIP transport, endpoint sessions and P04 composed effect admission.

## 3. Design decisions

- `CapabilityHandleV2` is a freely copyable readonly value containing only schema version, authority realm and opaque 128-bit token.
- It has a fixed 36-byte canonical wire representation and rejects unknown version, wrong length, empty realm/token and restart-realm mismatch.
- Rights, resource, subject, generations, constraints, quota and state are absent from the handle.
- A nonce-to-`CapabilityId` index provides O(1) lookup inside `CapabilityAuthority`. It contains identity routing only; all authority remains in the existing record.
- `CapabilityInspectionDescriptorV2` is explicitly an inspection projection and cannot be submitted back as authority.
- V2 mint, upgrade, delegation, validation and revoke resolve the same record used by V1.
- `OpenFileRequestV2`/`OpenV2Async` is additive SIP message 5; V1 message IDs and requests remain unchanged.

## 4. Authority / identity / evidence / provider split

- Authority: existing live `CapabilityRecord`, constraints, lineage and quota account.
- Identity/reference: `CapabilityHandleV2` realm/version/token.
- Evidence: V1 and V2 inspection descriptors.
- Provider state: unchanged and not present in V2 handles.

## 5. Single-ledger proof

V2 mint calls existing RuntimeKernel mint and projects the newly created record's nonce. Upgrade projects an already-owned live record. Validation/revoke/delegate resolve through the identity-only index into `_records`. No V2 rights, quota, revocation or lifecycle table exists. Copying or serializing a handle copies only lookup identity.

## 6. Lifecycle semantics

- Runtime restart changes `AuthorityRealmId`; old serialized handles fail before token lookup.
- Token tampering fails exact index/record nonce comparison.
- V1 revoke immediately invalidates V2 validation, and V2 revoke invalidates V1 validation.
- One-shot and quota transitions remain in the shared P03/P04 record/account and therefore have one aggregate outcome across all handle copies.
- Cancellation, quarantine, publication and reclaim semantics are unchanged; V2 adds no lifecycle owner.

## 7. Public/SIP boundary and non-leak status

Public V2 fields expose no rights, resources, ranges, quota, generations of protected objects or internal record slot. The additive generated file-service fixture carries a V2 namespace handle and resolves it through P04 exact operation admission. The response remains the existing V1-compatible file-object authority pending the P06 sealing migration. No ambient authority or generic resolver was added.

## 8. Changed files/projects

- `contracts/SingPlus.Contracts/Capabilities.cs`
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Capabilities/RuntimeKernel.EffectAdmission.cs`
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs`
- `src/Sip/SingPlus.Sip/FileSystem/IFileService.cs`
- `src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs`
- `tests/SingPlus.Tests/Capabilities/SingCapPhase05OpaqueV2Tests.cs`
- this evidence file.

## 9. Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~SingCapPhase05OpaqueV2Tests|FullyQualifiedName~SingCapPhase04EffectAdmissionTests|FullyQualifiedName~NativeSystemServiceVerticalSliceTests"
Passed: 30, Failed: 0, Skipped: 0

dotnet build "SingNextOS.slnx"
Build succeeded. Warnings: 0, Errors: 0

dotnet test "SingNextOS.slnx" --no-build
HybridCpu_ExecutableAdapter.Tests: 12 passed
HybridCPU_NeutralRuntime.Tests: 58 passed
SingPlus.Platform.HybridCpu.Tests: 60 passed
SingPlus.Tests: 1132 passed, 2 skipped
Aggregate: 1262 passed, 2 skipped, 0 failed

git diff --check
Exit 0; no whitespace errors. Git emitted only existing LF-to-CRLF working-copy notices.
```

Tests cover deterministic serialization, unknown schema, forged token, wrong realm, V1/V2 same-record projection, cross-surface revoke, copied-handle one-shot/quota non-amplification, edited inspection projection and a generated SIP/runtime-host V2 call.

## 10. Claim and limitations

Claim is `RuntimeEnforced` for opaque V2 resolution and compatibility. V1 remains supported and is not yet globally deprecated because services migrate incrementally. P06 must replace file/socket object identity with identity-only sealed handles while retaining capability-ledger effect rights. Full SIP sentry migration remains P08.

The optional `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent; no content was inferred.

## 11. HybridCPU boundary

HybridCPU core, ISE, ISA/opcodes, registers, compiler, scheduler, load/store, retire and architecture were not modified. V2 handles do not cross or authorize the provider boundary.
