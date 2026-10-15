# Phase 10 Implementation Evidence

## 1. Baseline and preservation

- HEAD: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- Pre-existing dirty/untracked/deleted files were preserved. No reset, checkout, clean, commit, push, or destructive filesystem operation was used.
- `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains absent; its content was not inferred.

## 2. Audited and reused implementation

- Reused `ServiceManifestV1` identity/image/process/contracts/dependencies/platform/resources/budgets/lifecycle policies and canonical digest as the V2 base.
- Reused `AdmissionVerifier`, `SingPlusAdmissionProofV1`, `ComponentAdmissionPlan`, `RuntimeKernel.AdmitComponent`, and the existing `CapabilityAuthority` mint path.
- Reused provider-neutral package qualification vocabulary as evidence only; no provider token became OS authority.

## 3. Design decisions

- Added schema-versioned `ServiceManifestV2`; V1 ABI and callers remain intact.
- V2 canonically adds security profile, dependency content closure, exact static capability imports, sealed type imports/exports, memory/table-quota/runtime/delegation/AOT policy references, and admission/framework policy version+digest.
- `AdmissionVerifier.GetManagedCapPolicyDescriptor()` exports the live P09 policy and positive-surface identities/digests; audit construction rejects any drift.
- `SingCapAuditV1` is deterministic evidence and contains no live handles. Same package id/version with different bytes explicitly requires requalification.

## 4. Authority / evidence / provider split

- Manifest and audit describe intent and qualification evidence only.
- Runtime authority is still minted exclusively through `RuntimeKernel` into the existing `CapabilityAuthority` ledger.
- Before process, budget, or capability creation, a V2 plan must supply grants exactly equal to canonical `StaticCapabilityImports`.
- Provider package/version/digest/source/schema values are supply-chain evidence, never local capability authority.

## 5. Single-ledger proof

- No capability ids, handles, remaining counters, Region state, seal state, session state, or completion state are stored in V2 or audit.
- Runtime continues to call the existing `MintCapability` path; P10 adds only pre-mint exact-intent validation.

## 6. Lifecycle semantics

- V2 mismatch fails before process/budget/capability creation.
- Existing reverse-order component rollback, stale, revoke, cancellation, quarantine, and reclaim behavior is unchanged.
- Audit possession cannot start a component and is not accepted by any `ComponentAdmissionPlan` constructor.

## 7. Public / SIP / non-leak status

- V2 is additive; no existing SIP method or V1 public contract was changed.
- Audit public properties contain evidence objects and strings, not `CapabilityId`, `CapabilityHandleV2`, `ProcessHandle`, Region handles, or provider-private authority.

## 8. Changed files/projects

- `contracts/SingPlus.Contracts/ServiceManifestV2.cs`.
- `tools/SingPlus.Admission/AdmissionVerifier.cs` and `SingCapAudit.cs`.
- `src/Runtime/SingPlus.Runtime/Components/ComponentAdmission.cs` and `RuntimeKernel.Components.cs`.
- `tests/SingPlus.Tests/Runtime/SingCapPhase10ManifestAuditTests.cs`.
- `eng/singcap-toolchain-v1.json` updated to the qualified verifier source SHA-256 `5887E38D4FB73E869DAA3801C25CF4652F082588178DC7DD41918D684A080D40`.

## 9. Commands and results

- Focused P10: 7 passed, 0 failed, 0 skipped.
- Related manifest/admission/toolchain set: implementation tests passed; the first run exposed the expected verifier-source golden drift, then the golden was updated to the actual digest.
- `dotnet build "SingNextOS.slnx"`: succeeded, 0 warnings, 0 errors.
- First full test run was rejected because the known unrelated one-second GUI `SpinWait` test failed; isolated rerun passed in 306 ms.
- Final `dotnet test "SingNextOS.slnx" --no-build`: adapter 12, neutral 58, platform 60, SingPlus 1172 passed and 2 skipped; aggregate 1302 passed, 2 skipped, 0 failed.
- `git diff --check`: exit 0; line-ending conversion notices only.

## 10. Claim and FutureGated items

- Claim: `RuntimeEnforced` for V2 static-import/grant consistency and deterministic admission/audit binding; evidence is not authority.
- External provider evidence is represented and mutation-detected; creating or publishing a real rebuilt HybridCPU package was not performed and is not required from the local checkout.
- Signature/trust-store policy, broader provider provenance import, and CI authority-diff presentation remain FutureGated.

## 11. HybridCPU boundary

- HybridCPU core, ISE, ISA/opcodes, registers, load/store, retire, scheduler, compiler semantics, runtime legality, microarchitecture, and architecture were not changed.
- Only provider-neutral package/source/digest/schema evidence was modeled.
