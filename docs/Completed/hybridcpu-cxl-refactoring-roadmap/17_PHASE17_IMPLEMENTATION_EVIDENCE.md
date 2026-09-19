# H17 Cross-Project Validation And Exit Evidence

Status: closed for the deterministic repository executable model on
2026-09-18. This status does not claim physical CXL deployment or
`ProductionSecure` capability.

## Baseline

- SingNextOS baseline HEAD:
  `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- The worktree already contained user and parallel-session changes.
- No reset, checkout, commit, push or remote operation was performed.
- HybridCPU ISE and its H00-H17 phase evidence were consumed read-only.
- The cross-project ABI remains exact package pin
  `HybridCPU.ExternalRuntime.Contracts` `[1.14.0]`.

## Exit matrix

| # | Required contour | Executable evidence | Result |
|---:|---|---|---|
| 1 | Type-3 `OwnedRegion` -> guest mapping -> child -> teardown | `SecureVirtualizedType3BackedType2WorkloadPublishesEventBeforeExactClosure` | PASS |
| 2 | Type-3 guest lineage -> secure overlay | same composed test plus secure guest-lineage tests | PASS |
| 3 | CXL.io -> `DeviceLease` -> bounded VirtualIo -> exact close | same composed test and child-domain close tests | PASS |
| 4 | exact secure virtual Type-2 execution | same composed test with `VirtualComputeContext` | PASS |
| 5 | stale child epoch before effect | secure-execution stale-binding matrix | PASS |
| 6 | stale secure generation before effect | secure-execution generation matrix | PASS |
| 7 | stale guest/parent mapping generation | secure guest mapping stale tests | PASS |
| 8 | stale fabric/backing generation | Type-2/Type-3 stale binding tests | PASS |
| 9 | stale security-evidence generation | `SecuritySessionResetInvalidatesCapturedEvidenceGenerationOnly` | PASS |
| 10 | FM reconfiguration during live guest accelerator | `FabricReconfigurationDuringSecureVirtualGuestWorkClosesEffectBeforeReclaim` | PASS |
| 11 | Type-3 hot-remove with live guest mapping | `Type3HotRemoveWithLiveSecureGuestMappingsBlocksPublicationUntilExactClosure` | PASS |
| 12 | security reset after completion before publication | `SecurityResetAfterDeviceCompleteBlocksVisibilityPublicationAndGuestEvent` | PASS |
| 13 | ambiguous close blocks reclaim | secure/Type-2/Type-3 quarantine and reclaim tests | PASS |
| 14 | malformed creation and failed compensation | secure admission recovery matrix | PASS |
| 15 | event only after visibility/security/publication | composed positive and post-completion reset tests | PASS |
| 16 | same endpoint/generation, distinct opaque binding | Type-2 binding identity test | PASS |
| 17 | semantic compiler intent without topology | HybridCPU H16/H17 compiler exit-gate evidence | PASS |
| 18 | unsupported secure operation denied | HybridCPU secure admission policy matrix | PASS |

## Corrected completion race

`CxlType2AcceleratorService` previously evaluated required CXL security before
calling `ObserveCompletion`. A security session reset concurrent with that
call could occur after the check and before visibility/publication.

The service now performs an exact second security evaluation after recording
`DeviceComplete` and before acquiring visibility. A deterministic provider
hook resets the security generation at that exact boundary. The operation
returns `StaleGeneration`, the staged publication callback does not run, the
guest event is not delivered, and provider closure releases the operation
without treating the reset as success.

## Qualification

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore \
  --filter FullyQualifiedName~SecureExecutionBindingTests
PASS - 51/51

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build \
  --filter '<H17 focused lifecycle/CXL matrix>'
PASS - 150/150

dotnet build SingNextOS.slnx --no-restore
PASS - 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build
PASS - 994 passed, 2 skipped

dotnet test SingNextOS.slnx --no-build -m:1
PASS - 1124 passed, 2 skipped, 0 failed
```

The first parallel full-solution test exposed a transient timing failure in an
unrelated GUI ownership test. That exact test passed immediately in isolation,
the complete `SingPlus.Tests` project passed, and the serialized full-solution
test then passed all four test projects.

## Claim boundary

H17 is closed at `Executable` for deterministic repository providers. CXL
topology and hardware identity remain below the adapter boundary. Provider
evidence does not create CPU legality or SingNextOS authority, and completion
does not imply visibility, publication or release.

Physical CXL hardware, firmware, Fabric Manager, secure monitor, multi-host
deployment and production provider conformance remain unproven. Therefore
`ProductionSecure` remains unavailable.
