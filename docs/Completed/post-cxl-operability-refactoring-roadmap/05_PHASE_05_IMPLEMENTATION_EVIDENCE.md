# Phase 05 Implementation Evidence

Date: 2026-09-18 (Europe/Moscow)

## Baseline

- Repository: `C:\Users\Yuriy Kurnosov\Desktop\SingNextOS`.
- Baseline HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- Actual solution: `SingNextOS.slnx`.
- The worktree was already substantially dirty from prerequisite/build-system/runtime work and completed Phases 00–04. All pre-existing and parallel changes were preserved.
- No reset, checkout, force, commit, push or remote Git operation was performed.

## Audited dependencies and reused types

- `ServiceBudgetDimension` from the Phase 01 manifest vocabulary remains the single provider-neutral dimension vocabulary. It already covers CPU allocation, owned and mapped memory, RegionUse, IPC messages/bytes, external operations, device/DMA, guest memory, checkpoint storage and trace/telemetry buffers.
- `ComponentAdmissionAuthority` remains manifest/admission truth. Phase 05 materializes its requested limits into exact service and process budget accounts; a manifest remains a request, never a grant.
- `ProcessRegistry` remains exact process/domain generation truth. A process budget account is attached to one exact `ProcessHandle`; restart cannot inherit it.
- `RegionAuthority`, `ChannelRegistry`, `ExternalOperationAuthority` and platform mapping authorities remain ownership, transfer, effect, pin and closure truth. The budget ledger records capacity only.
- Existing capability validation remains effect authorization. `BudgetAdministration` is required to configure system capacity or attach an otherwise raw process to a budget hierarchy.
- Existing provider and release paths remain closure truth. No availability, timeout, crash or budget observation is treated as closure.

## Implemented scope

- Versioned provider-neutral `ResourceBudgetContract` with account/reservation identities and generations, hierarchy levels, dimensions, usages, pressure, reservation lifetime and advisory QoS.
- A single `ResourceBudgetAuthority` ledger implements System → Service → Process/Domain → OperationReservation under one bounded critical section. Atomic reservation charges every ancestor and never holds its lock across a provider call.
- Per-dimension child-limit validation and concurrent reservation admission prevent child or ancestor overcommit.
- Generation-bound exact release is idempotent for the live handle. A forged/stale generation returns typed `Stale` and cannot free current allocation.
- Managed component admission creates fresh service and process accounts before process/capability materialization; replacement receives new account and reservation generations.
- Owned-memory allocation, IPC queued message/byte admission, platform mapped-memory admission and generic ExternalOperation admission reserve before consumption.
- Pre-effect failures compensate the exact reservation. Successful receive, ownership release, mapping closure or ExternalOperation release frees it only after the owning semantic authority reports closure.
- IPC and external-operation APIs accept optional `AdmissionQosHint`; it is carried as observation only and never changes capability, hard-limit or provider decisions.
- Pressure projections distinguish `Normal`, `SoftLimit`, `HardLimit`, `ReclaimPending` and `PinnedByExternalEffect`; none authorizes reclaim.

## Authority, evidence and provider split

- `BudgetReservationHandle.AuthorizesEffect`, `.AuthorizesReclaim` and `.MaterializesAuthority` are always false. Account and snapshot DTOs also state that they do not materialize authority.
- Capability answers whether the caller may request an effect; budget answers whether capacity is admitted; the resource/provider authority answers whether the effect was accepted and later closed. All conditions remain independent.
- Budget release follows resource-specific closure; the ledger cannot close an operation, unmap memory, move ownership, release RegionUse or publish a result.
- QoS and pressure are scheduling/operability observations. They cannot bypass capability validation, a hard budget, or provider admission.
- There is one mutable ledger for budget accounting and no duplicate ownership, operation, provider-effect or generation registry.

## Lifecycle, stale, crash, quarantine and reclaim guarantees

- Reservation precedes managed-path consumption and is compensated exactly when admission fails before effect.
- A crash with a submitted ExternalOperation leaves the operation reservation and ancestor charge live. Process teardown reconciles it only after the owning operation authority reaches exact `Released` through provider closure/containment.
- Mapping charge survives local release until the platform mapping authority confirms its own release path.
- IPC message and byte dimensions are independent from memory dimensions; dequeue or exact channel teardown releases queued charges once.
- Owned-memory MOVE first reserves the destination generation, performs the exact ownership transition, then releases the source charge. Failed transfer compensates the destination and preserves the sender charge.
- Old reservation generations cannot release current allocations. Service replacement performs fresh manifest, capability, process and budget admission.
- Active reservations block account retirement. Pressure or a process crash does not authorize unsafe reclaim.

## Public/SIP boundary and non-leak status

- Public DTOs expose semantic account/reservation identities, dimensions, quantities, pressure and provider-neutral QoS only.
- No CXL topology, BDF, HDM, DPA, route, Fabric Manager identity, raw mapping, provider-private lease/token, secure backend fact, HybridCPU opcode/compiler metadata or replay certificate is exposed.
- The contracts do not promise a CPU scheduler implementation, zero-copy transport, hardware QoS, or provider-specific cost semantics.

## Changed files

- `contracts/SingPlus.Contracts/ResourceBudgetContracts.cs`.
- `contracts/SingPlus.Contracts/CapabilityResourceIds.cs`.
- `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/Budgets/RuntimeKernel.Budgets.cs`.
- `src/Runtime/SingPlus.Runtime/Components/ComponentAdmission.cs`.
- `src/Runtime/SingPlus.Runtime/Components/RuntimeKernel.Components.cs`.
- `src/Runtime/SingPlus.Runtime/Components/RuntimeKernel.ManifestAdmission.cs`.
- `src/Runtime/SingPlus.Runtime/Channels/RuntimeKernel.Channels.cs`.
- `src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs`.
- `src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs`.
- `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs`.
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.ProcessTeardown.cs`.
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs`.
- `src/Runtime/SingPlus.Runtime/KernelResult.cs`.
- `tests/SingPlus.Tests/Runtime/Phase01ManifestContractsTests.cs`.
- `tests/SingPlus.Tests/Runtime/Phase02ServiceSupervisorTests.cs`.
- `tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs`.
- `docs/post-cxl-operability-refactoring-roadmap/05-resource-budgets-quotas-and-admission-qos.md`.
- `docs/post-cxl-operability-refactoring-roadmap/README.md`.
- This evidence file.

## Focused and regression qualification

1. Focused Phase 05 positive/negative matrix and replacement integration:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~PlannedReplacementUsesFreshProcessCapabilityAndStableServiceLineage"`

   Final result: passed, 8 passed, 0 failed, 0 skipped. Coverage includes parent/child enforcement, concurrent no-overcommit, stale release, advisory QoS, owned-memory recovery, independent IPC dimensions, mapped-memory closure, crash-with-submitted-effect pinning and fresh replacement accounting.

2. Owning lifecycle regressions:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --filter "FullyQualifiedName~Phase01ManifestContractsTests|FullyQualifiedName~Phase02ServiceSupervisorTests|FullyQualifiedName~Phase04DeadlineCancellationTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~ProtocolRuntimeTests|FullyQualifiedName~PlatformOwnedRegionMappingV2Tests|FullyQualifiedName~OwnershipTests|FullyQualifiedName~ComponentAdmissionTests"`

   Result: passed, 106 passed, 0 failed, 0 skipped.

3. Solution build:

   `dotnet build SingNextOS.slnx --no-restore --nologo`

   Result: succeeded, 0 warnings, 0 errors. The installed preview `.NET 11.0.100-rc.1.26425.128` SDK emitted informational `NETSDK1057` messages.

4. Solution tests:

   `dotnet test SingNextOS.slnx --no-build --no-restore --nologo`

   Result passed across all four assemblies:

   - `HybridCpu_ExecutableAdapter.Tests`: 12 passed, 0 failed, 0 skipped;
   - `HybridCPU_NeutralRuntime.Tests`: 58 passed, 0 failed, 0 skipped;
   - `SingPlus.Platform.HybridCpu.Tests`: 60 passed, 0 failed, 0 skipped;
   - `SingPlus.Tests`: 1046 passed, 0 failed, 2 skipped.

   The two skips are explicit opt-in suspended-child qualification probes.

5. `git diff --check` is run after this evidence file is written; its final result is recorded below by successful phase closure.

## Remaining gaps and FutureGated items

- Managed components and explicitly admitted raw processes are budget-enforced. Pre-existing raw-process APIs remain backward-compatible and unbudgeted until attached through capability-gated `AdmitProcessBudget`; universal migration of every legacy caller is FutureGated hardening, not claimed here.
- CPU allocation is presently a provider-neutral quantity contract, not a rolling-window scheduler implementation. Scheduler policy remains outside Phase 05.
- Checkpoint-storage and trace/telemetry-buffer dimensions are integrated with their owning Phase 08/09 consumers and remain charged until exact delete/close. RegionUse, device/DMA and guest-memory dimensions are defined and hierarchically enforceable through exact reservations but do not yet have universal automatic charging on every legacy owning path; no such universal consumer enforcement is claimed.
- `ReclaimPending` is a defined observation state; current integrations principally emit hard-limit or external-effect-pinned pressure. Broader reclaim workflow projection belongs to later supervisor/telemetry integration.
- Phase 06 owns trace events for reservations; Phase 09 owns policy-scoped budget telemetry; Phase 11 owns cross-cutting performance and abuse measurements.
- No production hardware, physical CXL, QEMU, FPGA or silicon qualification was performed or claimed.

## HybridCPU boundary confirmation

Phase 05 did not modify HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture or CPU architecture. It introduced no SingNextOS-to-HybridCPU implementation dependency. Existing executable-adapter and neutral-runtime projects were only built and tested by solution qualification.
