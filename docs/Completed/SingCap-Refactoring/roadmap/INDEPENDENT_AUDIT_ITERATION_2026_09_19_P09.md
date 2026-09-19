# Independent audit iteration — ManagedCap admission closure

Date: 2026-09-19  
Baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`  
Pre-existing worktree changes: the independent P02-P08 fixes and evidence from the preceding iterations; all were preserved.

This file records new audit executions only. It does not rewrite historical phase evidence.

## Disposition

| Status | Requirement | Actual path / impact | Resolution / evidence |
|---|---|---|---|
| ConfirmedDefect | ManagedCap must reject ambient mutable reference state, including references hidden inside local value types. | `AdmissionVerifier.ScanStaticState` classified the field signature itself. A static local struct was treated as a value even when one of its instance fields was `object`, allowing ambient authority/state to cross the full-module closure check. | `AssemblyModel.FieldValueTypeCanCarryReference` now recursively closes local value-type instance fields. Cycles are bounded by a visited set; malformed signatures and unresolved/external value types fail closed. A negative executable fixture with `static HiddenState` containing `object` was added. |
| EvidenceDrift | The policy identity and supply-chain digest must bind the behavior actually executed. | The semantic change initially invalidated the pinned AdmissionVerifier source digest. Leaving the old ruleset name would also have hidden a policy change behind the same descriptor. | Ruleset advanced from V12 to V13 and names recursive local value-type closure. The source SHA-256 pin and P13 TCB review now contain `991A295E05E41F9DFAF1D2C0CAEDD24013321BFEAE135F06DBA0EA6C2F87DA45`. The first P13 run rejected the stale pin; the rerun passed only after the explicit update. |
| FalsePositive | Precompiled/local dependencies, framework overloads, unknown framework members/types/policies, and native inventory must not bypass ManagedCap. | The verifier loads identity-matching local assemblies, hashes the transitive closure, scans every method and static field in that closure, matches exact framework member identities, rejects unknown versions/members/types/profiles, and inventories adjacent native files. | Existing focused tests plus the full P13 qualification passed. No admission authority was moved into manifests or proof records. |

## Minimal design decision and authority split

The fix extends the existing static verifier's signature interpretation; it creates no runtime authority, capability ledger, resolver, or copied remaining counter. Admission proofs, manifests, policy descriptors, source digests, and package identities remain evidence. They neither mint a SingNext capability nor grant provider authority.

The finding is `StaticAdmission`, with `QualifiedManaged` evidence for the isolated NativeAOT fixture. NativeAOT publication is closed-world qualification only: it is not a process-isolation, hardware-enforcement, ProductionCandidate, CHERI-equivalent, cancellation, publication, release, quarantine, or reclaim claim.

## Commands actually run

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~AdmissionVerifierTests|FullyQualifiedName~ManagedAsync
Passed: 65, failed: 0, skipped: 0

eng/qualify-singcap-p13.ps1  (first run after source change)
Failed closed at the AdmissionVerifier source-digest pin, as expected; no passing result was claimed.

eng/qualify-singcap-p13.ps1  (after ruleset V13 and digest-pin update)
Exit code: 0
HybridCpu_ExecutableAdapter.Tests: passed 12
HybridCPU_NeutralRuntime.Tests: passed 58
SingPlus.Platform.HybridCpu.Tests: passed 60
SingPlus.Tests: passed 1196, skipped 2, failed 0
Solution build: succeeded; warnings 0; errors 0
Preview/toolchain build: succeeded with the five documented RS1041 warnings; errors 0
Isolated win-x64 NativeAOT publish: succeeded from a unique intermediate/output tree
Performance qualification: regenerated `artifacts/singcap-p13/SingCapPerformanceQualificationV1.json`; the report explicitly says lock acquisition is not separately instrumented and excludes provider latency
git diff --check: exit code 0; only LF-to-CRLF working-copy warnings were emitted
```

Public/SIP/non-leak status: this change exposes no API and no CLR object graph. It only rejects an additional forbidden static-state shape before ManagedCap admission.

HybridCPU boundary status: no HybridCPU core, ISA, compiler, scheduler, microarchitecture, or adapter source was modified. The required `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains absent and is a recorded limitation.

Next slice: P10 manifest, audit, and supply-chain binding against the live V13 policy and generated qualification artifacts.
