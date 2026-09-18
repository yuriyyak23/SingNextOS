# Phase 10 Implementation Evidence — Provider Conformance and Deterministic Fault Injection

## Baseline

- Phase-start HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- The worktree remained intentionally dirty with prerequisite work and Phases 00–09. All existing changes were preserved; no reset, checkout, commit, push, force, or remote Git operation was performed.
- Actual solution: `SingNextOS.slnx`.

## Audited dependencies and reused semantic owners

- `ExternalOperationAuthority` remains the only source for operation state, effect boundary, publication, provider loss, exact release, and stale operation generation.
- `RegionAuthority` remains the only owner of `RegionUse` pins; `ResourceBudgetAuthority` remains the only owner of reservation charge/release.
- component teardown and `CapabilityAwareServiceSupervisor` remain the real reclaim/replacement and quarantine consumers.
- ordinary checkpoint classification remains the owner of checkpoint refusal for live or ambiguous external operations.
- `HybridCpuExternalOperationProvider` is used only at the provider-neutral executable-adapter boundary. HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture, and CPU architecture were not changed.

The reusable conformance abstractions are test-only: `ProviderConformanceSuite`, `ProviderScenario`, `ProviderFaultPlan`, and effect/closure/generation oracles. No unprivileged or production fault-injection API was introduced.

## Implemented conformance matrix

The common suite executes every scenario twice against a fresh driver instance and requires byte-for-byte equal typed observations. It covers:

- fail before effect;
- fail and throw after acceptance;
- malformed receipt, rejected by an exact request/version/stage/outcome validator in each test-only driver before the ambiguity is projected into the OS lifecycle;
- provider generation change;
- delayed and failed closure;
- reset during `Submitted` and `Visible`;
- stale closure receipt.

Both provider families use the same semantic oracle:

1. the generic SingNextOS ExternalOperation/model path;
2. the provider-neutral HybridCPU executable-adapter integration path.

For every post-effect ambiguity, the oracle verifies retained local pins and ExternalOperation budget charge, suppressed publication, checkpoint refusal, teardown/replacement blocking or quarantine, stale rejection where applicable, and exact eventual pin/budget release plus reclaim only after explicit containment.

## Prerequisite correction and authority split

Conformance exposed a missing prerequisite invariant in the owning ExternalOperation subsystem: teardown previously inferred provider closure from local `DeviceComplete`/`Visible` progress. That inference was removed.

- `DeviceComplete` and `Visible` no longer imply `ProviderResourcesClosed`.
- provider loss after submission remains `ProviderLost`, including staged visible work, and invalidates uses without releasing them;
- ordinary local completion/cancellation without provider closure returns a draining disposition;
- ambiguous provider loss returns `ExternalEffectUncontained`, causing supervisor quarantine;
- only an exact closure or effect-containment release receipt permits pin/budget release and reclaim.

This preserves the split: capabilities authorize local effects; fault plans produce test stimuli; provider receipts/containment establish closure facts; traces, telemetry, checkpoint results, and conformance observations are evidence only.

## Public/SIP and provider boundary

- The framework lives under `tests/SingPlus.Tests`; production contracts do not expose a fault plan.
- Neither conformance observations nor adapter receipts are authority-bearing DTOs.
- No CXL topology, BDF, HDM, DPA, route, raw mapping, provider-private lease, recovery credential, secure backend detail, or HybridCPU internal appears in an ordinary public/SIP contract.
- The executable-adapter model is not claimed as hardware qualification.

## Integration and tests

Focused and related regression command:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase10ProviderConformanceTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~HybridCpuExternalOperationProviderTests|FullyQualifiedName~ProcessTeardownTests|FullyQualifiedName~CxlType2|FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~Phase09StructuredTelemetryTests|FullyQualifiedName~Phase02ServiceSupervisorTests"
```

Result: passed, 71/71. The Phase 10 fixture contributes three tests: the ten-mode generic provider matrix, the same ten-mode executable-adapter matrix, and a guard proving fault-plan types remain test-only.

Final qualification commands and results are recorded below after execution:

```powershell
dotnet build SingNextOS.slnx --no-restore --nologo
dotnet test SingNextOS.slnx --no-build --no-restore --nologo
git diff --check
```

Results:

- build: succeeded, 0 warnings, 0 errors;
- `HybridCpu_ExecutableAdapter.Tests`: 12 passed;
- `HybridCPU_NeutralRuntime.Tests`: 58 passed;
- `SingPlus.Platform.HybridCpu.Tests`: 60 passed;
- `SingPlus.Tests`: 1075 passed, 2 skipped, 0 failed;
- `git diff --check`: exit 0; only existing LF-to-CRLF worktree warnings were emitted.

## Changed files for this phase

- `tests/SingPlus.Tests/Conformance/ProviderConformanceFramework.cs`
- `tests/SingPlus.Tests/Runtime/Phase10ProviderConformanceTests.cs`
- `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Services/CapabilityAwareServiceSupervisor.cs`
- `tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs`
- `tests/SingPlus.Tests/Runtime/Phase02ServiceSupervisorTests.cs`
- `tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs`
- this phase document, roadmap index, and this evidence file.

## Remaining gaps / FutureGated

- The deterministic modes are model/test stimuli at contract boundaries; no physical reset, power-loss, CXL fabric, accelerator, or device hardware was exercised.
- Recovery-token cryptographic authenticity is provider-specific and remains FutureGated; this phase proves exact identity/generation and stale-receipt rejection only.
- Provider wall-clock nondeterminism, multi-process executable crashes, and privileged production fault-control policy remain FutureGated.
- Trace/telemetry causal presentation across a full managed-service run is retained for Phase 11 integration; the conformance oracle checks the authoritative lifecycle facts rather than treating observation as closure.
- No production, hardware, performance, CXL, or confidentiality claim is made. Qualification uses repository model providers and the .NET 11 preview environment.
