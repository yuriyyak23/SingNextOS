# Phase 03 Implementation Evidence — Typed Constraints and Quota Lineage

**Date:** 2026-09-19  
**Claim:** `RuntimeEnforced` for capability derivation subset checks and shared consumable quota only.

## 1. Local baseline and preservation

- Baseline HEAD: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- Initial `git status --short` was dirty: it contained the in-progress P00-P02 files, modifications to capability/runtime contracts, many pre-existing documentation moves/deletions, and untracked SingCap material.
- No reset, checkout, clean, force, commit, push, bulk move, or destructive filesystem operation was performed. Existing changes were preserved.

## 2. Audited dependencies and reused owners

The live audit covered `CapabilityAuthority`, `RuntimeKernel` mint/delegate/validate, `ResourceBudgetAuthority`, `CapabilityDescriptorV1`, `CapabilitySet`, P02 tests, and the normative P03/ACP/deferred documents. P03 reuses:

- the sole `CapabilityAuthority` record dictionary and gate;
- P02 authority realm, opaque nonce, subject/resource generation, record state and capacity rules;
- existing `RuntimeKernel.DelegateCapability` as a production consumer;
- `EndpointSessionHandle`, `DomainId`, `ResourceKind`, `CapabilityRights`, and V1 descriptor projection.

`ResourceBudgetAuthority` remains the distinct owner of service resource-admission capacity. It is not called, mirrored, or double-charged by capability quota consumption.

## 3. Selected design decisions

- Canonical constraints are immutable internal values precomputed at mint/derive time; unknown schema, enum values and malformed values fail closed.
- V1 families are rights, exact resource plus optional typed subresource, operation set, optional byte range with element/alignment semantics, lifetime, exact target subject, optional session, remaining delegation depth, and quota/account reference.
- Canonical serialization is deterministic binary serialization with explicit schema and ordered operation values; no culture-dependent string formatting or policy parsing occurs during validation.
- Range construction and subset checks use checked end arithmetic and explicit element-size/alignment rules.
- A root mint creates one quota account inside `CapabilityAuthority`. All descendants retain the same opaque account reference and the same live atomic remaining counter. A child may narrow its per-handle ceiling; it never copies remaining capacity.
- No sibling-union API was added.
- Correctness retains the existing single authority gate; P13 already mandates 1/2/4/8/16/32-worker throughput, median, p95, p99 and maximum lock-wait measurement before a stronger performance claim.

## 4. Authority / identity / evidence / provider split

- Authority: live `CapabilityRecord`, its canonical constraints, and the shared live quota account inside `CapabilityAuthority`.
- Identity: P02 realm/capability/nonce and exact resource/subject/session generations.
- Evidence: `CapabilityDescriptorV1`, inspection snapshots and canonical bytes cannot authorize or restore quota.
- Provider state: untouched and never interpreted as local authority.

## 5. Single-ledger and non-duplication proof

No capability, quota or resource-budget registry was added. `QuotaAccount` is private to the existing `CapabilityAuthority` implementation and referenced directly by every record in one lineage. The only aggregate remaining value is its atomic field. Per-record `QuotaConsumed` enforces only that handle's narrowing ceiling; it is not a copied remaining balance. `ResourceBudgetAuthority` continues to account a different truth: service resource admission/reservation.

## 6. Linearization and lifecycle semantics

- Derivation validates the active parent and every child constraint and inserts the child while holding the existing capability ledger gate; this is the P03 derivation linearization point.
- Quota admission validates the record, atomically decrements the shared account, and advances the handle ceiling under the ledger gate. Concurrent siblings cannot exceed the root account.
- P02 stale realm/subject/resource generation and direct/domain revoke behavior is preserved.
- P03 does not claim an effect lease, subtree revocation, provider cancellation, publication control, quarantine, or reclaim. Those remain P04+ work and were not fabricated by quota accounting.

## 7. Public/SIP boundary and non-leak status

Constraint records, account references, quota consumption and inspection helpers are internal runtime surfaces. Public handles and `CapabilityDescriptorV1` gained no authoritative constraint/quota fields. SIP still carries capability IDs only and does not parse strings or build constraint graphs on its fast path. Tests prove edited descriptor fields cannot grant rights, change the resource, or restore consumed quota.

## 8. Changed files/projects

- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityConstraints.cs` — canonical typed algebra and deterministic serialization.
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` — record integration, subset-gated derivation and shared atomic quota.
- `tests/SingPlus.Tests/Capabilities/SingCapPhase03ConstraintAlgebraTests.cs` — positive, negative, generated/property and concurrent tests.
- `docs/SingCap-Refactoring/roadmap/PHASE_03_IMPLEMENTATION_EVIDENCE.md` — this evidence.

No contract ABI, SIP project, Region owner, admission verifier, provider adapter or HybridCPU source was changed by P03.

## 9. Qualification commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~SingCapPhase03ConstraintAlgebraTests|FullyQualifiedName~CapabilityAuthorityTests|FullyQualifiedName~SingCapPhase02CapabilityLedgerTests"
Passed: 29, Failed: 0, Skipped: 0

dotnet build "SingNextOS.slnx"
Build succeeded. Warnings: 0, Errors: 0

dotnet test "SingNextOS.slnx" --no-build
HybridCPU_NeutralRuntime.Tests: 58 passed
HybridCpu_ExecutableAdapter.Tests: 12 passed
SingPlus.Platform.HybridCpu.Tests: 60 passed
SingPlus.Tests: 1118 passed, 2 skipped
Aggregate: 1248 passed, 2 skipped, 0 failed

git diff --check
Exit 0; no whitespace errors. Git emitted only existing LF-to-CRLF working-copy notices.
```

Coverage includes rights widening, different resource, range expansion/overflow/alignment/element mismatch, operation addition, later expiry, wrong target, session widening, depth reset, quota widening, unknown schema/operation, descriptor forgery, generated valid subsets, sibling amplification and concurrent aggregate exhaustion.

## 10. Claim and limitations

The justified claim is `RuntimeEnforced` for P03 derivation and quota semantics. It is not `QualifiedManaged` or `ProductionCandidate`. Future-gated items include P04 exact operation leases/subtree revocation/cross-authority pilot, public V2 handles, Region range ownership, SIP sentries, ManagedCap, and P13 contention/performance qualification.

The optional `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` file was absent. P03 does not touch that boundary, so no content was inferred.

## 11. HybridCPU boundary confirmation

HybridCPU core, ISE, ISA/opcodes, registers, compiler, scheduler, load/store, retire and architectural semantics were not modified. No SingNextOS-to-HybridCPU implementation dependency or provider-as-authority interpretation was introduced.
