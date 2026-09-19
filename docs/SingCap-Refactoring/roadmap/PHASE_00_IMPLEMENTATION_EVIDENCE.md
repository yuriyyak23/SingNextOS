# Phase 00 — Implementation Evidence

**Claim level:** `ModelOnly`

**Qualification date:** 2026-09-19

**Solution:** `SingNextOS.slnx`

## 1. Baseline and preservation

- `git rev-parse HEAD` returned `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- Initial `git status --short` already contained deletions under four older roadmap trees plus untracked `docs/Completed/` and `docs/SingCap-Refactoring/`.
- Those pre-existing changes were preserved. No reset, checkout, clean, force, commit, push or destructive filesystem operation was used.
- This phase added only the P00 decision/evidence documents and one architecture-test source file.

## 2. Audited dependencies and reused types

The audit read the normative SingCap-M README/specification/roadmap/traceability/P00/P90 documents and inspected live implementations and tests for:

`CapabilityAuthority`, `CapabilityDescriptorV1`, `RegionAuthority`, `OwnedBuffer<T>`, `OwnedRegion<T>`, `BorrowLease<T>`, `RegionUseHandle`, `RegionBackingLeaseHandle`, `EndpointSessionRegistry`, `ExternalOperationAuthority`, `RuntimeKernel` external-operation flows, `HybridCpuExternalOperationProvider`, `ServiceManifestV1`, `SingPlusGenerator`, `SingPlusAnalyzer`, `AdmissionVerifier`, `FileObjectHandle`, `SocketObjectHandle`, `ProcessAuthority` and representative native service hosts.

The requested `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` was searched in SingNextOS and the locally available HybridCPU trees and was absent. No replacement document was treated as equivalent evidence.

## 3. Selected ADR decisions

The authoritative P00 selection is in `PHASE_00_ARCHITECTURAL_DECISIONS.md`:

- one `CapabilityAuthority` ledger;
- random 128-bit runtime realm and opaque capability token;
- non-wrapping runtime-owned service incarnation;
- maximum delegation depth 16, owned by runtime policy;
- shared atomic root quota account;
- exact operation-lease admission linearized with revoke under the ledger gate;
- ephemeral realm-bound SIP serialization only;
- distinct Region/Borrow/Mutation generations;
- real address-space isolation for `NativeIsolated`;
- NativeAOT required for the selected `QualifiedManaged` production lane;
- exact HybridCPU package/source/digest/schema tuple.

## 4. Authority / identity / evidence / provider split

- Local authority: `CapabilityAuthority` records only.
- Memory ownership/use truth: `RegionAuthority` only.
- External effect truth: existing `ExternalOperationAuthority` lifecycle only.
- Identity, descriptors, manifests, audit and inspection are non-authoritative projections.
- HybridCPU CPU guard, admission/publication/release receipts and generations are provider-side gates/evidence, never SingNext capability authority or Region ownership.
- Completion remains distinct from visibility, local publication, provider release and local reclaim.

## 5. Single-ledger and non-duplication proof

Live source search found one production capability-record dictionary keyed by `CapabilityId`:

`src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`.

`SingCapPhase00ArchitectureTests.CapabilityIdRecordStoreExistsOnlyInCapabilityAuthority` makes that contour executable across production `contracts`, `src`, `sdk` and `tools` sources. Adversarial fixtures prove the detector rejects both `Dictionary` and `ConcurrentDictionary` duplicate forms. This architecture test is a repository contour gate, not a substitute for P02 runtime semantics or code review of deliberately obfuscated storage.

The HybridCPU test rejects direct ISE/compiler/concrete external-runtime project edges while explicitly allowing provider-neutral contract references. Existing package/layer architecture tests remain in force.

## 6. Lifecycle guarantees frozen for later implementation

- Mint/derive linearizes at insertion of a fully validated record.
- Revoke linearizes at the authoritative active-to-revoked transition under the same gate as effect admission.
- Revocation denies new admissions and does not fabricate cancellation or closure for submitted work.
- Runtime restart changes realm; service restart changes service incarnation; old ephemeral/sealed handles are stale by construction once implemented.
- Unknown/malformed/stale provider evidence does not prove closure.
- Ambiguous post-effect outcomes require quarantine; reclaim waits for exact local and provider closure.
- Region MOVE/reclaim remain `RegionAuthority` transitions; generation invalidation does not replace confidentiality zeroization.

These are frozen decisions, not P00 runtime-enforcement claims.

## 7. Public / SIP / non-leak status

P00 adds no production public API or SIP field. It freezes V2 public handles to schema/version + realm + opaque token and forbids rights, resource, quota, provider-private identity and mutable resource references in the wire form. No generic resolver, enumerable capability bag, ambient authority or raw mutable object graph was added.

## 8. Changed files/projects

- `docs/SingCap-Refactoring/roadmap/PHASE_00_ARCHITECTURAL_DECISIONS.md` — selected decisions, inventory and linearization ledger.
- `tests/SingPlus.Tests/Architecture/SingCapPhase00ArchitectureTests.cs` — single-ledger and HybridCPU dependency gates plus negative fixtures.
- `docs/SingCap-Refactoring/roadmap/PHASE_00_IMPLEMENTATION_EVIDENCE.md` — this evidence.

Only `SingPlus.Tests` gains executable code; production projects and contracts are unchanged.

## 9. Commands and actual results

1. `git status --short; git rev-parse HEAD` — baseline recorded; pre-existing deletions/untracked roadmap work observed; HEAD matched the pinned commit.
2. `git -C "C:\Users\Yuriy Kurnosov\Desktop\HybridCPU v2" rev-parse HEAD` — `794c4a53494f503855ac8cf209efab23fde083b2`.
3. `Get-FileHash -Algorithm SHA256 ...HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg` for both artifact and restored package — both returned `B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2`.
4. `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter FullyQualifiedName~SingCapPhase00ArchitectureTests --nologo -v:minimal` — passed, 9/9.
5. `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter "FullyQualifiedName~Architecture|FullyQualifiedName~CapabilityAuthorityTests" --no-restore --nologo -v:minimal` — passed, 45/45.
6. `dotnet build SingNextOS.slnx --nologo -v:minimal` — succeeded, 0 warnings, 0 errors.
7. First `dotnet test SingNextOS.slnx --no-build --nologo -v:minimal` — one timeout assertion failed in unrelated `Phase9GuiOwnershipTests.TripleBuffersRemainSeparateAndUnavailableScanoutFallsBackToCpuOwnershipProtocol` line 225; `SingPlus.Tests` result was 1091 passed, 2 skipped, 1 failed. Other test projects passed.
8. Focused rerun of that exact GUI test with `--no-build --filter FullyQualifiedName~TripleBuffersRemainSeparateAndUnavailableScanoutFallsBackToCpuOwnershipProtocol --nologo -v:normal` — passed, 1/1 in 314 ms, demonstrating timing-sensitive non-reproduction without changing the test or product code.
9. Second `dotnet test SingNextOS.slnx --no-build --nologo -v:minimal` — passed: `HybridCpu_ExecutableAdapter.Tests` 12/12, `HybridCPU_NeutralRuntime.Tests` 58/58, `SingPlus.Platform.HybridCpu.Tests` 60/60, `SingPlus.Tests` 1092 passed with 2 existing opt-in skips. The initial transient failure remains recorded above.
10. `git diff --check` — recorded after this evidence file in the final section below.

Warnings seen during the first focused build were existing .NET preview/RS1041 diagnostics. The subsequent solution build reported zero warnings and zero errors.

## 10. Claim level, limitations and FutureGated work

Claim level is `ModelOnly`. Architecture contour tests are active, but P00 deliberately implements no V2 capability semantics.

FutureGated: realm/token runtime records, service incarnation enforcement, shared quota debit, subtree revocation, exact effect leases, sealing, ManagedCap full-module admission, NativeAOT qualification, deterministic audit and final HybridCPU provider conformance. `CapRef<T>`, `SharedArena`, generic shared mutable CLR graphs and an unbounded capability heap remain excluded from v1.

The missing `refctor master plan2.md`, empty repository commit metadata in the package nuspec and the first-run GUI timing failure are explicit limitations/evidence facts. None was converted into a stronger claim.

## 11. HybridCPU boundary confirmation

No file in HybridCPU core, ISE, ISA, compiler, scheduler, runtime legality or architecture was changed. SingNextOS production code and the executable adapter were not changed in P00. HybridCPU inputs were read only to verify source commit, package artifact digest and schema values. No HybridCPU token, guard, receipt, generation or domain tag was promoted to local SingNext authority.

## Final repository checks

`git diff --check` completed with no output. Because the new phase files are untracked within the pre-existing untracked roadmap tree, an explicit trailing-whitespace scan was also run over all three P00 files and completed clean after removing Markdown hard-break whitespace. Final HEAD remains `a67eea1aafc72054d22f1586b62c6883cdc71681`; final status retains the pre-existing roadmap deletions/untracked trees and adds the P00 architecture-test file.
