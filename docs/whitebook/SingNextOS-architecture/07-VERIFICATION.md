# Verification and evidence

## What this revision actually verified

The 18 October 2026 English Whitebook was prepared from the GitHub `master` snapshot `4cbc8e36a54da7cf193e13e7c18d773b36da2c04`. The complete Git tree inventory was enumerated; selected implementation paths and package/build configuration were read at the pinned revision.

**No build, integration test, hardware boot, physical isolation experiment, performance campaign, or formal proof run was performed for this documentation change.** Prior Russian Whitebook test counts from 15 October are historical statements, not revalidated current test results. The previously referenced `artifacts/v6/iteration-20261015-original-model-revoke/report.md` does not exist in the pinned `master` tree; it is intentionally not linked as live evidence.

## Source-backed checks and reproducible anchors

| Check | Actual evidence |
| --- | --- |
| Runtime owners are independently constructed | [RuntimeKernel.cs](../../../src/Runtime/SingPlus.Runtime/RuntimeKernel.cs) |
| `DestroyAsync` dispatch correlates an invocation | [RuntimeVirtualizationServiceHost.cs](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeVirtualizationServiceHost.cs) |
| Domain destroy binds invocation before drain | [RuntimeKernel.Virtualization.cs](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs) |
| Host revoke stores an original before effect, resolves same request | [HostPlatformAuthorityProvider.Virtualization.cs](../../../src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.Virtualization.cs) |
| V6 enablement returns false | [V6FeatureGates.cs](../../../src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs) |
| VNext enablement returns false | [VNextFeatureGates.cs](../../../src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs) |
| Budget recovery uses authenticated local replay | [ResourceBudgetRecoveryJournal.cs](../../../src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetRecoveryJournal.cs) |
| Exact external runtime contract version | [SingPlus.Runtime.csproj](../../../src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj) |
| SDK pinned with roll-forward disabled | [global.json](../../../global.json) |
| Kernel stage overload reports host-debug readiness | [KernelEntryPoint.cs](../../../src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs) |
| API contracts exist separately from full implementations | [IFileService](../../../src/Sip/SingPlus.Sip/FileSystem/IFileService.cs), [INetworkService](../../../src/Sip/SingPlus.Sip/Networking/INetworkService.cs) |

For provenance and source-level review details see [code verification log](12-CODE-VERIFICATION.md).

## How to run an applicable development check

Use the [pinned SDK](../../../global.json) and exact immutable dependency assets; confirm that the SDK is installed before running a command:

```bash
dotnet --info
dotnet restore SingNextOS.slnx
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj
dotnet test tests/SingPlus.Platform.HybridCpu.Tests/SingPlus.Platform.HybridCpu.Tests.csproj
dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj
```

These are **suggested developer commands**, not claimed executed evidence. Choose the smallest relevant suite and affected graph; provider-only tests do not replace central runtime checks. Revalidate package restore, lockfiles, environment, input artifacts and CI mode for the actual commit.

## Evidence grading

| Label | Acceptable claim |
| --- | --- |
| Source-present | Implementation symbol/path exists |
| StaticAdmission | A bounded static check accepted a precise input |
| ModelOnly | A software/model contour exhibited a specified property |
| ExecutableAdapter | An identified adapter executed the named contract |
| RuntimeEnforced | A named runtime route actually checked the property |
| EnforcedUpperBound | Defined resource upper limit was enforced |
| GuaranteedReservation | Separate quantitative/temporal guarantee was established |
| ProductionQualified | Exact deployment requirements independently closed |
| FutureGated | Interface/model exists; feature not qualified/activated |

These labels are property-specific and not an automatic promotion ladder.

## Test/reporting discipline

Record exact commands, input revisions, assets, targeted tests, exit codes, timing, failed test history, source/owner identity and scope. For original-operation correctness, check the effect count, foreign-resource survival and independent RegionUse/backing/charge retention or reclaim. A green suite never justifies extrapolation to untested hardware, failure models or security boundaries.
