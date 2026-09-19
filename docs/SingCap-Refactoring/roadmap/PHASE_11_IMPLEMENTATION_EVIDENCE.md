# Phase 11 Implementation Evidence

## 1. Baseline and preservation

- HEAD `a67eea1aafc72054d22f1586b62c6883cdc71681`; pre-existing dirty/deleted/untracked work preserved.
- No reset, checkout, clean, commit, push, or destructive filesystem operation.
- HybridCPU adapter master-plan file remains absent and was not invented.

## 2. Reused owners

- Reused `CapabilityAuthority`, operation leases, `SealedObjectAuthority`, endpoint-session pins, generated SIP clients/protocols, process registry, and native service drain lifecycle.
- Network/socket P06 pilot remained the reference composition. File and process services were migrated into the same universes.

## 3. Design decisions

- File handles now carry `SealedHandle<FileObjectSeal>` identity; read/write/close require both the exact seal and exact object capability, then acquire a correlated session/capability operation admission.
- Process creation returns `ProcessControlObjectHandle` plus the independent control capability. New process V2 messages 8-13 require the seal and capability. Messages 2-7 remain explicitly documented non-ManagedCap compatibility surfaces.
- Seal marker ids are closed trusted-runtime registry entries; unknown marker types fail closed.
- Deterministic confused-deputy review JSON covers caller, object, authority, target, delegation, external/Region effects, closure, participants, revocation policy, lock order, and reverse compensation.

## 4. Authority / evidence / provider split

- Seals bind object/service/session/generation identity only. Rights and operation/quota authority remain solely in `CapabilityAuthority`.
- Review JSON is CI-checked evidence and contains no live authority.
- These in-memory service pilots invoke no external provider; no provider receipt is interpreted as capability.

## 5. Single-ledger proof

- File/process records retain capability ids only to correlate and revoke the single ledger entry; they do not copy rights or remaining quota.
- Every migrated effect calls existing `AdmitSessionCapabilityEffect`; no second capability/resolver/quota registry was added.

## 6. Linearization, stale, revoke, cancellation, quarantine, reclaim

- Admission order: sealed-object pin -> session/capability operation admission -> seal revalidation -> lifecycle mutation.
- Close linearizes at `BeginClose`; new pins fail afterward, then the capability is revoked.
- Session/service drain revokes seals and capabilities; attached process children are terminated and reclaimed by existing lifecycle code.
- Capability revocation blocks new admissions but does not forge cancellation of already admitted work.
- No external ambiguous result exists in these pilots, so provider quarantine semantics are unchanged.

## 7. Public/SIP boundary

- File handle evolution is additive via an optional/default seal field; service-produced handles always contain a live seal.
- Process V2 uses new message ids and bounded copied-value records. Legacy process messages are explicitly non-ManagedCap rather than silently upgraded.
- No ambient authority, object resolver, sibling enumeration, or raw mutable CLR graph was added.

## 8. Changed files/projects

- `contracts/SingPlus.Contracts/NativeServiceContracts.cs`.
- `src/Sip/SingPlus.Sip/Process/IProcessService.cs`.
- `src/Runtime/SingPlus.Runtime/Capabilities/SealedObjectAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs`.
- Native-service adversarial tests and `P11_CONFUSED_DEPUTY_REVIEW.json` plus its architecture gate.

## 9. Qualification

- Focused migration/sealing/effect tests: 34 passed, 0 failed.
- Native service regression: 16 passed before the two new adversarial tests; both new tests also passed.
- `dotnet build "SingNextOS.slnx"`: 0 warnings, 0 errors.
- `dotnet test "SingNextOS.slnx" --no-build`: adapter 12, neutral 58, platform 60, SingPlus 1175 passed and 2 skipped; aggregate 1305 passed, 2 skipped, 0 failed.
- `git diff --check`: exit 0; line-ending notices only.

## 10. Claim and FutureGated

- Claim: `RuntimeEnforced` for network, file, and process V2 composition paths.
- Legacy process messages 2-7 are not ManagedCap and remain compatibility-only pending consumer migration/removal.
- File backend is an in-memory pilot; a real provider must use P04 containment and P12 adapter audit before stronger claims.

## 11. HybridCPU boundary

- HybridCPU core, ISA/ISE, registers, compiler, scheduler, load/store, retire, runtime legality, microarchitecture, and architecture were not changed.
