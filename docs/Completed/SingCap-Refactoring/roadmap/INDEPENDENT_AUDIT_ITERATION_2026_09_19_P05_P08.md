# Independent audit iteration — opaque handles, sealing, Region, and SIP schemas

Date: 2026-09-19  
Baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`  
Pre-existing worktree changes: the independent P02-P04 fixes and evidence from the preceding iteration; all were preserved.

This file records new audit executions only. It does not rewrite historical phase evidence.

## Disposition

| Status | Slice | Finding | Resolution / evidence |
|---|---|---|---|
| FalsePositive | P05 opaque V2 | `CapabilityHandleV2` carries only version/realm/token. V1 and V2 resolve the same `_records` ledger; wrong realm/version/token fail closed. | Existing P05 executable tests and full regression pass. No second capability store exists. |
| ConfirmedDefect | P06 sealing | `Revalidate` and `BeginClose` accepted any pin with a matching token while any record pin was active. A disposed pin or a pin issued by another authority with a colliding token could borrow another pin's liveness. | Both operations now require the exact pin's live owner reference to be this `SealedObjectAuthority`. Added disposed-pin and cross-authority collision tests. No pin registry or rights ledger was added. |
| FalsePositive | P07 Region | The inspected authoritative paths use one `RegionRecord`, checked range/type arithmetic, per-record synchronization, non-reused IDs/generations, explicit use/borrow lifetimes, and lookup-free Span element loops. | P07 and full solution tests pass. `SharedReadMostly` and `DirectCoherentWrite` remain explicitly fail-closed/FutureGated. |
| MissingCoverage | P07 CLR token handoff | Existing races prove Region MOVE linearization but do not directly prove atomicity against a simultaneous user `OwnedBuffer.Move()` on the same CLR token. | No speculative redesign was made. This remains a focused adversarial coverage item for the cross-authority race/performance closure slice. |
| ConfirmedDefect | P08 deep value schema | The generator recursively inspected public properties but ignored instance fields. A readonly payload could hide a private `object`, array, or `List<T>` and cross the SIP boundary. | All instance fields are now recursively checked. Only four exact existing defensive-copy wrappers are terminal allowlist nodes: `BoundedBytes`, `SurfacePlaneSet`, `ProcessManifestValue`, and `InitialCapabilitySet`. Hidden mutable-field negative tests were added. |

## Authority split and claims

Seals remain identity/lifecycle-only and contain no rights. Region authority remains in `RegionAuthority`; capability effect authority remains in `CapabilityAuthority`; generated value-schema metadata is static admission evidence only. No completion, publication, cancellation, quarantine, reclaim, or provider authority was fabricated or moved.

P05-P08 corrected paths are supported at `RuntimeEnforced` (runtime identity/lifecycle) or `StaticAdmission` (generator schema) level. No ProductionCandidate, hardware-isolation, capability-hardware, or CHERI-equivalent claim is made.

## Commands actually run

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~SingCapPhase05OpaqueV2Tests|FullyQualifiedName~SingCapPhase06SoftwareSealingTests|FullyQualifiedName~NativeSystemServiceVerticalSliceTests
Passed: 33, failed: 0, skipped: 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~GeneratorTests|FullyQualifiedName~SingCapPhase08GeneratedSentryTests
Passed: 35, failed: 0, skipped: 0

dotnet build SingNextOS.slnx --no-restore
Build succeeded; warnings: 0; errors: 0

dotnet test SingNextOS.slnx --no-build --no-restore
HybridCPU_NeutralRuntime.Tests: passed 58
HybridCpu_ExecutableAdapter.Tests: passed 12
SingPlus.Platform.HybridCpu.Tests: passed 60
SingPlus.Tests: passed 1195, skipped 2
Total: passed 1325, failed 0, skipped 2

eng/qualify-singcap-p01.ps1
Exit code 0. Focused profile tests passed 5/5; default build passed; preview build passed with the five documented RS1041 warnings; isolated win-x64 NativeAOT publish completed.
```

HybridCPU boundary status: no HybridCPU core, ISA, compiler, scheduler, microarchitecture, or adapter source was modified. The required `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains absent and is a recorded limitation.

Next slice: P09 ManagedCap full-module closure and unknown framework member/type fail-closed behavior.
