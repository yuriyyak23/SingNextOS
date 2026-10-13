# Phase 11 Implementation Evidence — Cross-cutting Integration, Performance, and Hardening

## Baseline

- Phase-start HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- The worktree remained intentionally dirty with prerequisite work and Phases 00–10. All existing changes were preserved; no reset, checkout, commit, push, force, or remote Git operation was performed.
- Actual solution: `SingNextOS.slnx`.

## Internal mapping and lifecycle composition

The integrated fixture uses the existing semantic owners without introducing another lifecycle registry:

- manifest admission and `CapabilityAwareServiceSupervisor` own service admission, dependency generation, health, drain, quarantine, and replacement;
- `ResourceBudgetAuthority` owns hierarchical charges and exact release;
- channel and region authorities own IPC queue and MOVE/borrow ownership;
- `ExternalOperationAuthority` owns effect, visibility, publication, closure, containment, and reclaim barriers;
- cancellation scopes own monotonic deadline/cancellation disposition;
- trace, telemetry, and inspector are detached observations only;
- checkpoint records own ordinary logical images only and restore through fresh component admission.

The normal integrated scenario exercises hard-dependency binding, managed start, MOVE IPC, staged external completion/visibility/publication, exact provider closure, trace/telemetry observation, an ordinary checkpoint path, clean replacement, stale old-generation telemetry rejection, and exact dependency rebind.

## Authority, evidence, and provider split

- Manifest, supervisor health, performance timings, trace, telemetry, inspector explanations, and checkpoint images do not authorize an OS/provider effect.
- Capability validation remains the local effect authority; budgets only constrain admission.
- Provider loss now emits the additive semantic `ExternalOperationFaulted` trace event only after the authoritative provider-loss transition succeeds. The event neither closes nor contains the effect.
- Ambiguous loss retains RegionUse pins and budget charge, suppresses publication, produces inspector/telemetry/trace evidence, quarantines the service, and blocks replacement until explicit containment.
- Deadline expiry after acceptance requests cancellation but keeps pins until exact provider closure; budget exhaustion does not alter ownership and exact release restores capacity.

## Hardening and abuse coverage

- bounded restart/dependency behavior is retained from supervisor regression coverage;
- trace flood uses explicit drop markers and a fixed buffer;
- telemetry flood uses a bounded subscription and exact dropped-snapshot count;
- oversized scatter/gather lists fail before transfer;
- checkpoint storage storm is stopped by the configured system budget;
- unauthorized cross-service inspection remains denied;
- production assemblies contain no provider fault-plan type;
- prior phase regressions cover concurrent reservations/stale release, cancellation races, replay non-authority, checkpoint integrity, and telemetry/trace stale generations.

## Diagnostic performance methodology and observations

Environment reported by the executed test:

- runtime: .NET `11.0.0` preview;
- OS: Microsoft Windows NT `10.0.26200.0`;
- logical processor count: 16;
- `Stopwatch.Frequency`: 10,000,000 ticks/second;
- Debug test binaries, single in-process model kernel, 64 sequential iterations unless stated otherwise;
- no warm-up subtraction, GC isolation, affinity, percentile sampling, hardware provider, or release-build optimization.

One diagnostic run reported:

| Path | Scope | Elapsed ticks |
|---|---:|---:|
| service start | one operation | 801 |
| service replacement | one operation | 29,966 |
| supervisor query | 64 operations | 484 |
| four-byte copy send/receive | 64 round trips | 29,012 |
| allocate + 64-byte MOVE + receive | 64 round trips | 26,415 |
| 64-byte borrow + receive + return | 64 round trips | 51,703 |
| budget reserve + release | 64 operations | 20,200 |
| tracing disabled record call | 64 operations | 442 |
| tracing enabled record call | 64 operations | 538 |
| telemetry projection | 64 projections | 130,528 |
| inspector snapshot | 8 captures | 14,800 |
| checkpoint | one 256-byte logical image | 2,919 |

Provider conformance timing from a separate detailed run:

- generic ExternalOperation model: 10 scenarios, each repeated twice, 2,964,032 ticks;
- executable-adapter model: 10 scenarios, each repeated twice, 456,449 ticks.

These values are executable diagnostic baselines only. They are not throughput/latency guarantees and are not suitable for hardware or production comparisons.

## Tests and qualification

Focused test command:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase11CrossCuttingIntegrationTests" --logger "console;verbosity=detailed" --nologo
```

Result: passed, 5/5.

Provider conformance timing command:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Phase10ProviderConformanceTests.NativeExternalOperationModelPassesReusableFaultMatrix|FullyQualifiedName~Phase10ProviderConformanceTests.HybridCpuExecutableAdapterBoundaryPassesSameFaultMatrix" --logger "console;verbosity=detailed" --nologo
```

Result: passed, 2/2.

Cross-phase regression command:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase11CrossCuttingIntegrationTests|FullyQualifiedName~Phase10ProviderConformanceTests|FullyQualifiedName~Phase09StructuredTelemetryTests|FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~Phase07IpcV2Tests|FullyQualifiedName~Phase06DeterministicTracingTests|FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~Phase04DeadlineCancellationTests|FullyQualifiedName~Phase03AuthorityInspectorTests|FullyQualifiedName~Phase02ServiceSupervisorTests" --nologo
```

Result: passed, 73/73.

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
- `SingPlus.Tests`: 1080 passed, 2 skipped, 0 failed;
- `git diff --check`: exit 0; only existing LF-to-CRLF worktree warnings were emitted.

## Public/SIP and provider boundaries

- No root supervisor authority, capability material, confidential payload, provider credential, or replay authority is projected into trace/telemetry/checkpoint data.
- No CXL topology/BDF/HDM/DPA/route/raw mapping/provider-private lease or secure backend detail was added to ordinary public/SIP contracts.
- HybridCPU remains confined to the provider-neutral executable-adapter boundary; core, ISA, compiler, scheduler, runtime legality, microarchitecture, and architecture were not changed.

## Changed files for this phase

- `contracts/SingPlus.Contracts/DeterministicTraceContracts.cs`
- `src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs`
- `tests/SingPlus.Tests/Runtime/Phase10ProviderConformanceTests.cs`
- `tests/SingPlus.Tests/Runtime/Phase11CrossCuttingIntegrationTests.cs`
- this phase document, roadmap index, and this evidence file.

## Remaining gaps / FutureGated

- Release-build BenchmarkDotNet-style distributions, warm-up/GC control, affinity, percentile reporting, and hardware/provider measurements remain FutureGated; the current numbers are intentionally diagnostic.
- CPU scheduling/allocation window enforcement has no production scheduler owner in this repository, so only existing budget admission/accounting paths are measured; no scheduler QoS claim is made.
- The checkpoint timing covers synchronous logical snapshot construction, not a real service quiescence pause under workload.
- Sustained multi-process transport load, physical device resets, cross-host effects, and production fault-control authorization remain FutureGated.
- No production, hardware, CXL, confidentiality, or performance claim is made.
