# Phase 02 — Implementation Evidence

**Phase claim:** `RuntimeEnforced` for the P02 single-ledger identity, stale-generation, state and capacity slice

**Qualification date:** 2026-09-19

**Solution:** `SingNextOS.slnx`

## 1. Baseline and status preservation

- Phase-start `git rev-parse HEAD`: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- Phase-start status retained all pre-existing deleted legacy roadmap trees, untracked `docs/Completed`, P00/P01 files, and the separately appearing untracked `docs/SingCap-Refactoring.zip`.
- No reset, checkout, clean, force, commit, push or destructive filesystem operation was used.

## 2. Audited dependencies and reused types

The implementation reuses `CapabilityAuthority`, `RuntimeKernel`, `CapabilityId`, `CapabilityDescriptorV1`, `DomainId`, `ProcessHandle`, `ProcessRegistry`, `SingProcess`, `AuthorityInspector`, and existing platform revocation cascades. The audit covered all `MintCapability`, `ValidateCapability`, `SnapshotForDomain` and `InspectionSnapshot` consumers and the existing capability, supervisor, checkpoint and authority-inspector tests.

## 3. Selected design decisions

- Every `CapabilityAuthority` construction creates one non-empty `AuthorityRealmId`; restart therefore creates a distinct runtime incarnation.
- `CapabilityId` remains the monotonic, never-reused V1 compatibility slot. The internal opaque identity is `(realm, slot, random nonce)` and is checked against the same authoritative record.
- The identifier counter has terminal, non-wrapping exhaustion. Random nonce allocation permits eight bounded attempts and then returns `CapacityExhausted`.
- The authoritative record owns issuer, subject identity/generation, exact resource identity/generation, rights, revocation epoch, parent placeholder and `Active/Consumed/Revoked/Retired` state. `CapabilityDescriptorV1` is constructed only as a projection.
- Global resident-record capacity bounds table growth. Per-subject live capacity is keyed by `(DomainId, subject generation)` and is released exactly once when a record leaves `Active`; resident slots and identities are never recycled.
- P02 records the immutable parent placeholder but intentionally defers the child graph, bounded depth and subtree revoke semantics to P04.

## 4. Authority, identity, evidence and provider split

Only the private `_records : Dictionary<CapabilityId, CapabilityRecord>` is the capability authority ledger. Realm fingerprints, V1 descriptors, traces and authority-inspection nodes are projections/evidence and cannot validate without the live record. The nonce is absent from inspection and public DTOs. Provider identity, receipt, CPU guard and publication evidence are not consulted and do not confer local authority.

## 5. Single-ledger/non-duplication proof

No second capability record store, resolver, revocation table or completion registry was added. Opaque validation reaches `_records` through its non-reusable slot and compares the record nonce and realm in-place. The existing P00 architecture gate continues to require the only `Dictionary<CapabilityId, ...>` record store to be `CapabilityAuthority.cs`.

## 6. Lifecycle, stale, revoke, cancellation, quarantine and reclaim

The lock in `CapabilityAuthority` is the P02 linearization point for mint, delegate, validate, revoke, retire, bulk revoke and capacity accounting. Wrong realm, wrong nonce, stale subject generation, stale resource generation, revoked/retired state and insufficient rights fail before authority is returned. Bulk-revoke epoch increment is checked against wrap and fails closed at exhaustion. P02 does not alter submitted external-operation cancellation, provider closure, Region quarantine or reclaim; existing lifecycle owners and their tests remain authoritative.

## 7. Public/SIP/non-leak status

The only new public contract is opaque non-authorizing `AuthorityRealmId`. Existing V1 descriptors remain compatible projections; caller-supplied descriptors are not accepted by any mint/validate path. Internal references and nonce material are not public and do not cross SIP. No Region content, provider-private identity, reusable secret, generic resource resolver, sibling enumeration or ambient authority was added.

## 8. Changed files/projects

- `contracts/SingPlus.Contracts/Capabilities.cs` — `AuthorityRealmId` value contract.
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` — single authoritative record, realm/nonce identity, states, resource generation, bounded allocation and table quotas.
- `src/Runtime/SingPlus.Runtime/KernelResult.cs` — exact wrong-realm and forged-identity failures.
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs` — real consumer integration for mint failure propagation and exact resource-generation validation.
- `tests/SingPlus.Tests/Capabilities/SingCapPhase02CapabilityLedgerTests.cs` — positive, negative, adversarial and concurrent qualification.
- `docs/SingCap-Refactoring/roadmap/PHASE_02_IMPLEMENTATION_EVIDENCE.md` — this evidence.

## 9. Commands and actual results

1. `git status --short` and `git rev-parse HEAD` — captured the baseline above and preserved unrelated changes.
2. Initial `dotnet build SingNext.sln --no-restore` — failed immediately because that guessed filename does not exist; repository audit identified the actual solution `SingNextOS.slnx`, and no check was weakened.
3. Development `dotnet build SingNextOS.slnx --no-restore` — succeeded, 0 warnings, 0 errors.
4. Focused capability tests (`SingCapPhase02CapabilityLedgerTests` plus existing `CapabilityAuthorityTests`) — passed 16/16 before the RuntimeKernel consumer test was added.
5. Related regressions (`Capabilities`, `Phase03AuthorityInspectorTests`, `Phase02ServiceSupervisorTests`, `Phase08OrdinaryCheckpointTests`) — passed 54/54 after consumer integration.
6. Final focused `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~SingCapPhase02CapabilityLedgerTests` — passed 9/9.
7. Final `dotnet build SingNextOS.slnx --no-restore` — succeeded, 0 warnings, 0 errors.
8. Final `dotnet test SingNextOS.slnx --no-build --no-restore` — passed: executable adapter 12/12, neutral runtime 58/58, HybridCPU platform 60/60, main suite 1106 passed with 2 existing opt-in skips.
9. `git diff --check` — clean; only Git line-ending conversion notices were emitted.
10. `git diff --name-only -- tools/HybridCpu_ExecutableAdapter src/Platform/SingPlus.Platform.HybridCpu tools/Runtime/HybridCPU_NeutralRuntime` — empty.

## 10. Claim level, limitations and FutureGated work

The implemented P02 identity, stale validation, state denial and capacity behavior is `RuntimeEnforced` and covered by executable tests. The V1 descriptor is still a compatibility surface, not a portable authority wire form. Public V2 opaque handles and tamper/replay wire qualification remain P05. Typed constraints and quota lineage remain P03; derivation depth/subtree revocation, consume and exact effect leases remain P04. `Consumed` is deliberately representable but has no public transition before P04. No `QualifiedManaged` or `ProductionCandidate` claim is made.

## 11. HybridCPU boundary confirmation

HybridCPU core, ISE, ISA/opcodes, register file, compiler, scheduler, runtime legality, microarchitecture and provider adapter were not changed. No HybridCPU token, guard, admission receipt, generation or publication evidence was introduced into SingNext capability APIs.
