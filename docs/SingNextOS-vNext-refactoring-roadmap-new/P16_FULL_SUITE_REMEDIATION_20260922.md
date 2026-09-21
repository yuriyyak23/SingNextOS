# P16 full-suite consumer remediation

## Disposition

P16 remains **OPEN**. This additive remediation removes the nine unrelated failures recorded by the preceding P16 revalidation and performance evidence; it does not supply the still-missing provider, controlled-SMT, durable-restart, resource-aware SipJob differential or live gate-rollback evidence.

Normative baseline is `6227ea7cf258ef6ffce52001d4d2ffee07355b35`; execution HEAD is `1890a8e921cfe903b5b44857e4661168bf7bbceb`. The parallel/user-owned completed-roadmap files were neither edited nor moved.

## Confirmed drift and remediation

Eight tests still opened historical roadmap artifacts at their former `docs/...` locations after those trees were moved to `docs/Completed/...`. Every exact referenced artifact existed at the completed location. Only the consumers were changed; no compatibility copy, duplicate truth or rewritten historical evidence was created.

`eng/singcap-security-profiles-v1.json` omitted the existing `tools/SingPlus.Boot.DebugHost/SingPlus.Boot.DebugHost.csproj`. The project was added as `BuildTool/TestOnly`, owned by boot debug tooling, with `ModelOnly` claim. This does not add a runtime profile or authority.

The moved SipJob P14 tuple exposed two separate historical-evidence defects once its path was reachable:

1. Its `singNextOsSourceSha` is the historical `6227ea7cf258ef6ffce52001d4d2ffee07355b35`, while the test incorrectly compared it with every future current HEAD.
2. Its artifact hashes refer to mutable `bin/Debug` paths rather than archived immutable artifacts. Those historical hashes cannot be re-derived from later builds. The consumer now validates the pinned hash format, repository-contained paths and artifact presence, but does not misrepresent current binaries as the historical snapshot.

The completed tuple itself remains untouched and supports no current vNext claim. The limitation is explicit rather than hidden by refreshing its SHA fields.

## Executed evidence

```text
dotnet build tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --verbosity minimal
  Exit 0; 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~SingCapPhase11MigrationReviewTests|FullyQualifiedName~SingCapPhase12ProviderMappingTests|FullyQualifiedName~SingCapPhase13ClosureTests|FullyQualifiedName~HybridBootQualificationMatrixTests|FullyQualifiedName~Phase148QualificationClosureTests|FullyQualifiedName~SingCapPhase01ToolchainTests.EveryProjectHasExactlyOneOwnedSecurityProfile" --verbosity minimal
  Exit 0; passed 11, failed 0, skipped 0

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Exit 0
  SingPlus.Tests: passed 1495, failed 0, skipped 2
  HybridCpu_ExecutableAdapter.Tests: passed 90
  HybridCPU_NeutralRuntime.Tests: passed 58
  SingPlus.Platform.HybridCpu.Tests: passed 60
  Aggregate: passed 1703, failed 0, skipped 2
```

Final canonical `.\eng\qualify-vnext.ps1`: solution build passed with 0 warnings/errors; focused lane passed 152/152; full non-GUI suite produced the counts above; `git diff --check` exited 0 with line-ending warnings only.

The two suspended-child tests remain explicitly skipped by their existing qualification policy; they were not changed or relabelled.

## Claim boundary

This is CI/evidence-consumer remediation only. All `FG-VNX-*` gates remain OFF. No production, provider, HybridCPU, NativeAOT, realtime, upper-bound or guaranteed-capacity claim follows. Completed roadmaps remain historical evidence, not authority. Ordinary SIP/Compute/ExternalOperation behavior and all runtime authority owners are unchanged.

No HybridCPU ISA, VLIW format, pointer/register model, pipeline, replay, scheduler legality, memory controller or microarchitecture work was performed. No hardware, QEMU, firmware or CXL boot execution occurred.
