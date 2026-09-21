# P16 resource-budget contention and quarantine-pressure qualification

## Disposition and provenance

P16 remains **OPEN**. Normative baseline is `6227ea7cf258ef6ffce52001d4d2ffee07355b35`; execution HEAD is `1890a8e921cfe903b5b44857e4661168bf7bbceb`. This additive qualification narrows one previously missing P16 item: host/JIT contention and failure-pressure measurement for the existing `ResourceBudgetAuthority` and `ComputeTimeNanoseconds` family. It does not close provider, controlled-SMT, SipJob differential, live gate rollback, durable restart, temporal guarantee or production qualification.

The pre-existing/parallel move of `docs/SingNextOS-post-SingCap-M-SipJob-roadmap` into `docs/Completed`, the new `docs/singnextos_semantic_codesign_refactoring` tree and all earlier P16 revalidation edits were preserved. No commit or remote operation was performed.

## Owner and contour

- Quantitative owner: the existing `ResourceBudgetAuthority`; one authority instance is used per measurement, never one ledger per worker.
- Dimension: `ComputeTimeNanoseconds` only.
- Runtime: Windows x64, .NET 11.0.0 preview runtime, Release/JIT host, 16 logical processors reported by the runtime.
- Thread counts: 1, 2, 4, 8 and 16.
- Process-domain topologies: one shared process domain and one distinct process domain per worker, while retaining one system authority and its shared root lock.
- Operations: reserve/release; reserve/bind/consume/quarantine/reconcile/zero-settle; admission denial while an exact ExternalEffect lease is quarantined.
- Sampling: 1,000 iterations per worker and operation/topology. Complete call latency includes lock acquisition; lock waiting is not separately instrumented.

The executable report contains 20 measurement rows and 124,000 attempts. Every normal lifecycle completed, every pressure attempt failed with the expected `BudgetExceeded`, and there were zero unexpected outcomes. Successful/reconciled measurements ended with zero live leases and zero used amount. Pressure measurements retained exactly one quarantined lease, one charged unit and `PinnedByExternalEffect`; they never inferred a refund from ambiguity.

Latency and throughput values are observations from this exact run, not thresholds or guarantees. The complete values remain in `P16_RESOURCE_BUDGET_PERFORMANCE_20260922.json`; they are not promoted into a deadline, realtime, upper-bound or minimum-capacity claim.

## Regression and failure behavior

`VNextPhase16ResourcePerformanceQualificationTests` executes a small live-owner matrix, validates conservation and exact denial behavior, rejects zero/out-of-range worker and iteration bounds, checks the recorded Release matrix, verifies its claim exclusions and hash-validates the additive tuple. The first Release attempt exposed a harness setup defect: a fixed success limit of 10 caused 1,231 legitimate `BudgetExceeded` outcomes at 16 workers. The setup was corrected to `workers + 1`; the intentional-denial stage remains isolated at limit 1. No runtime owner behavior was weakened or relabeled.

Output creation uses `FileMode.CreateNew`, so reruns cannot silently overwrite this evidence. DTO snapshots, timings and the JSON report remain evidence only: none exposes an owner transition or authorizes effects, settlement, release or publication.

## Commands and actual results

```text
dotnet build tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --verbosity minimal
  Exit 0; 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~VNextPhase16ResourcePerformanceQualificationTests" --verbosity minimal
  Initial focused run: Exit 0; passed 4, failed 0, skipped 0

dotnet build tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj -c Release --no-restore --verbosity minimal
  Final incremental build: Exit 0; 0 warnings, 0 errors

dotnet run --project tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj -c Release --no-build --no-restore -- --vnext-resource-performance --iterations 1000 --workers 1,2,4,8,16 --output docs/SingNextOS-vNext-refactoring-roadmap-new/P16_RESOURCE_BUDGET_PERFORMANCE_20260922.json
  Exit 0; 20 rows; 124000 attempts; 0 unexpected outcomes
```

```text
dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  Exit 0; 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~VNext|FullyQualifiedName~RepositoryArchitecturePolicyTests|FullyQualifiedName~ProjectDependencyBoundary" --verbosity minimal
  Exit 0; passed 156, failed 0, skipped 0

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Exit 1; aggregate passed 1693, failed 9, skipped 2
  SingPlus.Tests: passed 1485, failed 9, skipped 2
  Other projects: passed 90 + 58 + 60, failed 0, skipped 0
```

The nine failures are unchanged from the preceding P16 revalidation: eight historical-document consumers still open paths moved by parallel/user work (SingCap P11/P12/P13, HybridBoot claim matrix and SipJob P14 tuple), and `SingPlus.Boot.DebugHost.csproj` remains absent from `eng/singcap-security-profiles-v1.json`. They are outside this contour and were not hidden, changed, skipped or treated as passes. Because the mandatory full suite is not green, P16 remains open.

The canonical `.\eng\qualify-vnext.ps1` was then executed. Its solution build passed with 0 warnings/errors, its focused lane passed 151/151, and it exited 1 at the same full-suite failure with the explicit message `Full non-GUI suite failed; vNext qualification remains incomplete.` Because the script intentionally stops there, `git diff --check` was executed separately and exited 0 (line-ending warnings only). Final P00/P16 architecture tests passed 45/45.

The later additive `P16_FULL_SUITE_REMEDIATION_20260922.md` corrected those stale consumers without altering completed artifacts; the final canonical lane then passed 1703 tests with zero failures and two unchanged explicit skips. The measurements in this report are unchanged.

## Claims and exclusions

Claim: `AccountingOnly` performance evidence for the exact host/JIT ComputeTime owner contour. All `FG-VNX-*` gates remain OFF. This evidence does not prove `RuntimeEnforced` rollout, `EnforcedUpperBound`, `GuaranteedReservation` or `ProductionQualified`.

FutureGated gaps remain owned by: provider/runtime adapters for provider-count/resource evidence; the execution environment and performance owner for controlled SMT topology; SipJob/ordinary owners for authoritative trace differential; gate/configuration owners for ON-to-OFF rollback under live work; checkpoint/budget owners plus an authenticated journal for cold restart. Bypassing any would transfer host accounting measurements to a different contour.

Ordinary SIP/Compute/ExternalOperation fallback was not changed. Plan, cache, receipt, telemetry and this report remain non-authoritative. No HybridCPU ISA, VLIW, register, pointer, pipeline, scheduler legality, memory-controller or microarchitecture work occurred. No NativeAOT, provider, hardware, QEMU, firmware or CXL boot execution is claimed.
