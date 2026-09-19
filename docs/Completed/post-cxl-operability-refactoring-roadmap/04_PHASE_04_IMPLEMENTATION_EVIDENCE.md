# Phase 04 Implementation Evidence

Date: 2026-09-18 (Europe/Moscow)

## Baseline

- Repository: `C:\Users\Yuriy Kurnosov\Desktop\SingNextOS`.
- Baseline HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- Actual solution: `SingNextOS.slnx`.
- The worktree was already substantially dirty from prerequisite/build-system/runtime work and completed Phases 00–03. All pre-existing and parallel changes were preserved.
- No reset, checkout, force, commit, push or remote Git operation was performed.

## Audited dependencies and reused types

- `ProcessRegistry` remains exact process-generation truth and validates every scope owner.
- `EndpointSessionInvocationRegistry` remains request/reply delivery, acceptance and terminal-publication truth; Phase 04 attaches an optional scope rather than replacing invocation state.
- `ChannelRegistry`/`RegionAuthority` remain MOVE/borrow/ownership truth. Timeout never performs an inverse transfer.
- `ExternalOperationAuthority` remains the sole operation stage, effect boundary, `RegionUse` pin and closure/release truth.
- `ReleasePlan` remains the only input proving provider resources closed or effect contained; `ProviderUnavailable`, timeout and cancellation request do not satisfy it.
- Phase 02 `CapabilityAwareServiceSupervisor` and `DrainComponent` remain service lifecycle and teardown truth. The new timeout policy selects an outcome only.
- One injected `TimeProvider` supplies the monotonic timestamp domain shared by sessions and cancellation scopes.

## Implemented scope

- Versioned `DeadlineCancellationContract` with `DeadlineClockClass`, `MonotonicDeadline`, `CancellationScopeId`, `CancellationScopeGeneration`, `CancellationScopeHandle`, `CancellationRequest`, `CancellationDisposition`, `TimeoutDisposition` and `CancellationObservation`.
- Required dispositions: `CancelledBeforeEffect`, `CancellationRequested`, `TooLateEffectMayExist`, `CompletedBeforeCancellation`, `ProviderClosurePending`, `ProviderEffectContained`, `Unsupported` and `Stale`.
- Exact process-generation scope ownership, hierarchical parents/children, child deadline capping, idempotent request and explicit downward propagation.
- Single leaf-consumer binding per scope generation. Reuse for a different external operation or typed IPC request fails stale.
- ExternalOperation admission checks deadline before acquiring any region use. Submitted cancellation remains `ProviderClosurePending`; only a successful owning release records `ProviderEffectContained`.
- Staged `DeviceComplete` cancellation suppresses publication but still requires provider closure. Cancellation after `Published` reports `TooLateEffectMayExist` and cannot unpublish.
- Typed IPC invocation permits caller waiting to stop with `CancellationPending` while exact invocation settlement continues. MOVE ownership remains at the admitted receiver.
- Supervisor drain timeout policies: continue draining, quarantine, fail replacement, or proceed only through existing proven containment/closure machinery.

## Authority, evidence and provider split

- `CancellationObservation` is coordination/evidence only and returns `AuthorizesEffect == false` and `AuthorizesReclaim == false` for every disposition, including `ProviderEffectContained`.
- A scope never mints capability, transfers ownership, releases `RegionUse`, closes a provider lease or changes publication by itself.
- Capability remains the only local authority for effects. Existing owner/capability checks still run on IPC, external-operation and supervisor paths.
- External/provider closure is copied into the temporal disposition only after the resource-specific authority has accepted exact release; it is not inferred from time or provider availability.
- No second mutable operation, ownership, closure or service-generation registry was introduced.

## Lifecycle, stale, cancellation, quarantine and reclaim guarantees

- Deadline expiry before external admission cancels the prepared operation locally and creates no region pins.
- After submission, cancellation transitions the temporal scope to `ProviderClosurePending`; active region uses remain pinned and release without exact closure/containment fails.
- Device completion is not publication. Staged cancellation may discard the result, but reclaim remains blocked until release.
- Published results cannot be cancelled or unpublished; late cancellation is typed as too late.
- Parent cancellation propagates only `CancellationRequested`; it does not claim child closure.
- Old/forged scope generations return typed `Stale` without mutating the live scope. A scope generation cannot be reused for a second leaf operation.
- IPC timeout stops only caller waiting. It does not duplicate or return an already admitted MOVE payload, and the service must still publish or cancel the exact invocation.
- Drain deadline quarantine does not invoke reclaim and leaves ambiguous submitted work and its pins intact.

## Public/SIP boundary and non-leak status

- Public contracts contain semantic process/scope/deadline identities only; no CXL topology, BDF, HDM, DPA, Fabric Manager identity, provider lease/token, secure backend data, HybridCPU opcode/compiler metadata or replay certificate is exposed.
- The typed IPC API is transport-neutral and does not claim zero-copy. Its MOVE test verifies semantics, not transport implementation.
- No replay, trace, health, manifest or timeout observation is accepted as OS authority.

## Changed files

- `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs`.
- `contracts/SingPlus.Contracts/ExternalOperations.cs`.
- `contracts/SingPlus.Contracts/ServiceSupervisorContracts.cs`.
- `src/Runtime/SingPlus.Runtime/Deadlines/CancellationScopeAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/Deadlines/RuntimeKernel.DeadlineCancellation.cs`.
- `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs`.
- `src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs`.
- `src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs`.
- `src/Runtime/SingPlus.Runtime/Services/CapabilityAwareServiceSupervisor.cs`.
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs`.
- `src/Runtime/SingPlus.Runtime/KernelResult.cs`.
- `tests/SingPlus.Tests/Runtime/Phase04DeadlineCancellationTests.cs`.
- `tests/SingPlus.Tests/Runtime/Phase02ServiceSupervisorTests.cs`.
- `docs/post-cxl-operability-refactoring-roadmap/04-typed-deadlines-cancellation-and-timeouts.md`.
- `docs/post-cxl-operability-refactoring-roadmap/README.md`.
- This evidence file.

## Focused and regression qualification

1. Focused Phase 04 positive/negative matrix:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase04DeadlineCancellationTests|FullyQualifiedName~DrainDeadlineWithAmbiguousExternalEffect"`

   Final result: passed, 9 passed, 0 failed, 0 skipped. Coverage includes hierarchy/deadline bounds, pre-effect cancellation, submitted pin retention and exact closure, staged pre-publication cancellation, late Published cancellation, stale and reused generations, IPC MOVE wait timeout, and supervisor quarantine.

2. Owning lifecycle regressions:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --filter "FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~EndpointSessionCancellationTests|FullyQualifiedName~ComputeServiceCallerCancellationTests|FullyQualifiedName~Phase02ServiceSupervisorTests|FullyQualifiedName~Phase03AuthorityInspectorTests"`

   Result: passed, 44 passed, 0 failed, 0 skipped.

3. Solution build:

   `dotnet build SingNextOS.slnx --no-restore`

   Result: succeeded, 0 warnings, 0 errors. The installed preview `.NET 11.0.100-rc.1.26425.128` SDK emitted informational `NETSDK1057` messages.

4. Solution tests:

   `dotnet test SingNextOS.slnx --no-build`

   Result after the Phase 04 implementation passed across all four assemblies:

   - `HybridCpu_ExecutableAdapter.Tests`: 12 passed, 0 failed, 0 skipped;
   - `HybridCPU_NeutralRuntime.Tests`: 58 passed, 0 failed, 0 skipped;
   - `SingPlus.Platform.HybridCpu.Tests`: 60 passed, 0 failed, 0 skipped;
   - `SingPlus.Tests`: 1039 passed, 0 failed, 2 skipped.

   The two skips are explicit opt-in suspended-child qualification probes.

5. `git diff --check` completed with exit code 0 before solution qualification. Git emitted existing LF-to-CRLF conversion notices but no whitespace errors. A final check is run after this evidence file is written.

## Remaining gaps and FutureGated items

- Device-family-specific cancellation commands remain behind their provider adapters; Phase 04 integrates the common contract at the generic `ExternalOperation` boundary and does not claim every hardware provider supports cooperative cancellation.
- `Unsupported` is a versioned truthful outcome for consumers that cannot cancel. Current shared ExternalOperation behavior prefers `ProviderClosurePending` after effect admission because closure is still required even when provider cancellation is unavailable.
- Phase 05 owns budget charging for cancellation scopes, pending IPC waits and external-operation reservations.
- Phase 06 owns trace correlation/events; Phase 09 owns deadline/cancellation metrics and telemetry projection.
- The runtime uses an injected monotonic `TimeProvider`; it does not introduce a separate timer scheduler. Callers arrange wakeup and may stop waiting through the typed IPC API, while the scope itself remains the correctness record.
- No production hardware, physical CXL, QEMU, FPGA or silicon qualification was performed or claimed.

## HybridCPU boundary confirmation

Phase 04 did not modify HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture or CPU architecture. It introduced no SingNextOS-to-HybridCPU implementation dependency. Existing adapter and neutral-runtime projects were only built and tested by solution qualification.
