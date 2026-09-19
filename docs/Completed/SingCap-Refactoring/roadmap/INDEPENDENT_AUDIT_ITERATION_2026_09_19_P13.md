# Independent audit iteration — cross-authority and claim closure

Date: 2026-09-19  
Baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`  
Pre-existing worktree changes: independent P02-P12 fixes and evidence; all were preserved.

## Disposition

| Status | Requirement | Actual path / impact | Resolution |
|---|---|---|---|
| ConfirmedDefect | MOVE of an ownership token must have one linearization winner. | `OwnedBuffer<T>.Move()` used an unsynchronized `_valid` check/update. Two concurrent callers could both construct valid wrappers over the same storage before either invalidated the source. Runtime transfer/borrow reservation on the same token had the same check/use race class. | All owner-token state transitions now serialize on the token's `_ownerGate`; validity and storage borrow/runtime-reservation checks are performed within that critical section. A 250-iteration adversarial test proves exactly one concurrent Move winner and releases the winning Region. This adds no Region or capability ledger. |
| EvidenceDrift | Section 22 must be traced item-by-item and incomplete items must not be called complete. | Historical P13 evidence said all sixteen DoD items were complete, while MAN-003 itemized native inventory, explicit AOT compiler identity, and signing/provenance remain absent from the emitted proof/audit pipeline. | Added `P13_DEFINITION_OF_DONE_LIVE_MATRIX.json` with all 16 numbered items. Item 11 is explicitly FutureGated and `singCapMComplete` is false. The project-wide maximum claim is reduced from QualifiedManaged to RuntimeEnforced; the independently qualified ManagedCap verifier lane remains QualifiedManaged. |
| FalsePositive | Performance evidence must not claim lock wait, provider latency, an SLO, or a security property. | The regenerated report labels maximum operation latency as the closest available metric, states lock acquisition is not separately instrumented, excludes provider latency, and is classified ModelOnly. | No stronger claim was made. |
| FalsePositive | Cross-authority races must name the winner and prove cleanup. | Existing P04/P06/P13 tests cover session close, seal close/restart, Region MOVE/reclaim, publication/revoke, provider completion/restart, and adapter callback re-entry. | Focused and full qualification passed; active leases/pins/uses are asserted released. |
| MissingCoverage | Region confidentiality policy needs executable reuse/zeroization evidence. | Managed Region buffers are newly allocated CLR arrays and are not pooled, but no direct cross-domain regression proved that released contents cannot reappear. | Added a test that releases a nonzero buffer, proves its token stale, allocates the same size in another domain, and proves a new Region identity with zero-initialized contents. The policy is no storage reuse, not a hardware erasure claim. |

## Authority and lifecycle split

`CapabilityAuthority`, `RegionAuthority`, `EndpointSessionRegistry`, `SealedObjectAuthority`, and `ExternalOperationAuthority` remain the only lifecycle owners in their respective scopes. The new owner-token gate only linearizes consumption of one CLR ownership token; it stores no Region truth and cannot authorize an operation.

Revocation blocks new admission but does not synthesize cancellation. Completion remains distinct from visibility, publication, and release. Provider loss does not release Region uses without exact closure or established containment. Reclaim remains blocked by active uses. No callback, wait, provider call, or user publication delegate is invoked under the newly added owner-token lock.

## Commands actually run

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~SingCapPhase13CrossAuthorityRaceTests|FullyQualifiedName~SingCapPhase07RegionHardeningTests|FullyQualifiedName~RegionUseTests"
Passed: 23, failed: 0, skipped: 0.

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~SingCapPhase13ClosureTests|FullyQualifiedName~SingCapPhase13CrossAuthorityRaceTests"
Passed: 11, failed: 0, skipped: 0.

eng/qualify-singcap-p13.ps1
Exit code: 0.
Locked restore: succeeded.
Solution build: succeeded, warnings 0, errors 0.
HybridCpu_ExecutableAdapter.Tests: 12 passed.
HybridCPU_NeutralRuntime.Tests: 58 passed.
SingPlus.Platform.HybridCpu.Tests: 60 passed.
SingPlus.Tests: 1199 passed, 2 explicitly skipped, 0 failed.
Nested P01/NativeAOT lane: succeeded; preview build emitted the five documented RS1041 warnings and no errors; isolated win-x64 publish succeeded.
Performance report regenerated at `artifacts/singcap-p13/SingCapPerformanceQualificationV1.json` with capturedUtc `2026-09-19T10:46:03.0156325+00:00`.
git diff --check: exit 0; LF-to-CRLF working-copy notices only.

dotnet build "SingNextOS.slnx"
Succeeded: 5 documented RS1041 warnings, 0 errors.

dotnet test "SingNextOS.slnx"
HybridCpu_ExecutableAdapter.Tests: 12 passed.
HybridCPU_NeutralRuntime.Tests: 58 passed.
SingPlus.Platform.HybridCpu.Tests: 60 passed.
SingPlus.Tests: 1200 passed, 2 explicitly skipped, 0 failed (final run after adding the storage-reuse test).
Aggregate: 1330 passed, 2 skipped, 0 failed.
```

Public/SIP/non-leak status: no public resolver, capability bag, provider token, mutable CLR graph, or authority inspection expansion was added.

Final claim from this iteration: project-wide `RuntimeEnforced`; ManagedCap verifier lane `QualifiedManaged`; deterministic manifest/audit `StaticAdmission` with MAN-003 itemization/provenance `FutureGated`; performance and hardware isolation `ModelOnly`/`FutureGated`. SingCap-M v1 is not labelled complete while DoD item 11 remains open.

HybridCPU boundary status: no HybridCPU core, ISE, ISA/opcode, compiler, register, load/store, scheduler, retire, runtime-legality, microarchitecture, or architecture file was modified. `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains absent.
