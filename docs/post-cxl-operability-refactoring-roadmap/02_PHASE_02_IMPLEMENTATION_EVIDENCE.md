# Phase 02 Implementation Evidence

Date: 2026-09-18 (Europe/Moscow)

## Baseline

- Repository: `C:\Users\Yuriy Kurnosov\Desktop\SingNextOS`.
- Baseline HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- Actual solution: `SingNextOS.slnx`.
- The worktree was already substantially dirty from prerequisite, build-system, runtime, test and HybridCPU adapter work plus completed Phases 00 and 01. All pre-existing and parallel changes were preserved.
- No reset, checkout, force, commit, push or remote Git operation was performed.

## Audited dependencies and reused types

- `RuntimeKernel.AdmitComponent`, `ComponentAdmissionRecord` and its existing rollback path remain the single process/component materialization lifecycle.
- `CapabilityAuthority` remains the single local authority source; the supervisor holds and revalidates an explicit `kernel:service-supervisor:v1` control capability for every mutation.
- `ServiceRegistry`, `ServiceEndpointDescriptor` and exact service endpoint sessions remain discovery, generation and binding truth. The supervisor does not create a second service registry.
- Existing `TerminateProcess`, teardown participants, external-operation closure and reclaim diagnostics remain closure/containment/reclaim truth.
- `ServiceManifestV1` remains a request. Phase 01 manifest evaluation is rerun for each replacement generation and does not become authority.
- `OperabilityGeneration.Compare` remains the stateless equality-only stale check used by existing registries; exact supervisor handles additionally bind service, process and domain generations.
- `TimeProvider.GetTimestamp()` supplies monotonic restart-window/backoff accounting; wall-clock time does not decide generation or closure.

## Implemented scope

- Capability-gated privileged `CapabilityAwareServiceSupervisor` over existing kernel APIs.
- Deterministic acyclic dependency graph validation and hard-dependency startup ordering.
- Exact `ServiceInstanceHandle` to process generation/domain mapping and exact dependency-generation bindings.
- Optional dependency absence as explicit degradation and late arrival through explicit session rebind.
- Observation-only health snapshots and explicit readiness/failure-propagation policies.
- Graceful drain and planned replacement with fresh component, manifest, capability and dependency admission.
- Stable service lineage across replacement: a retired service name retains its `ServiceId`, while the authoritative `ServiceRegistry` advances `ServiceGeneration` and binds the fresh process generation.
- Bounded restart windows, monotonic backoff and terminal crash-loop state.
- Quarantine and replacement blocking when existing teardown/reclaim truth reports an ambiguous or uncontained external effect.
- Exact component retirement and dependency-session bookkeeping added minimally to the owning component lifecycle subsystem.

## Authority, evidence and provider split

- The supervisor is an orchestration/control-plane consumer. It does not mint authority from manifests, health, dependency names or receipts.
- Capability validation occurs at supervisor creation and on every public mutation/observation call; revocation causes fail-closed `SupervisorDenied` without touching the service.
- Health is typed observation and `ServiceHealthSnapshot.AuthorizesMutation` is always false.
- Dependency bindings identify exact service instances but do not replace endpoint-session capability checks.
- Provider closure and containment are decided by existing resource-specific teardown participants and external-operation state, not by supervisor policy.
- Restart/backoff decides only when orchestration may retry. It cannot make an effect closed, release a pin or authorize reclaim.
- No global supervisor lock is held across component admission, process teardown or dependency-session rebind calls.

## Lifecycle, stale, quarantine and reclaim guarantees

- Lifecycle is executable across declared, admitting, starting, ready/degraded, draining, stopped, failed, restart-backoff, crash-loop and quarantined states.
- Graph cycles and missing hard dependencies fail before dependent materialization.
- Failed partial admission uses exact existing component teardown compensation.
- Planned replacement drains the exact old instance before retiring it and admits N+1 from a replacement definition; no live capability, service session or provider lease is copied.
- Old process capabilities and old service/dependency generations are stale after replacement. Dependents receive a newly opened exact endpoint session.
- A submitted operation with provable closure drains and permits replacement only after closure/reclaim completes.
- Provider loss or another ambiguous post-effect result leaves pins authoritative, sets supervisor state to `Quarantined`, and blocks replacement.
- Process failure, cancellation intent, timeout policy, caller disconnect or health failure is never treated as proof of provider closure.
- Restart attempts are bounded per monotonic window and backoff; exceeding the declared maximum enters `CrashLoop` rather than immediate unbounded restart.

## Public/SIP and provider-neutral boundary

- Supervisor/health DTO reflection tests reject capability tokens, provider leases/bindings, CXL/BDF/HDM/DPA/Fabric Manager vocabulary, HybridCPU lane/opcode internals and replay certificates.
- No supervisor API was added to ordinary IPC or compute paths.
- No CXL topology, raw mapping, provider-private recovery token, secure backend internal or HybridCPU execution detail is exposed through the public supervisor contracts.

## Changed files

- `contracts/SingPlus.Contracts/CapabilityResourceIds.cs`.
- `contracts/SingPlus.Contracts/OperabilityManifestContracts.cs`.
- `contracts/SingPlus.Contracts/ServiceManifestCanonicalization.cs`.
- `contracts/SingPlus.Contracts/ServiceSupervisorContracts.cs`.
- `src/Runtime/SingPlus.Runtime/KernelResult.cs`.
- `src/Runtime/SingPlus.Runtime/Components/ComponentAdmission.cs`.
- `src/Runtime/SingPlus.Runtime/Components/RuntimeKernel.Components.cs`.
- `src/Runtime/SingPlus.Runtime/Services/ServiceRegistry.cs`.
- `src/Runtime/SingPlus.Runtime/Services/CapabilityAwareServiceSupervisor.cs`.
- `tests/SingPlus.Tests/Runtime/Phase02ServiceSupervisorTests.cs`.
- `docs/post-cxl-operability-refactoring-roadmap/02-capability-aware-service-supervisor.md`.
- `docs/post-cxl-operability-refactoring-roadmap/README.md`.
- This evidence file.

## Focused and regression qualification

1. Focused supervisor positive/negative tests:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~Phase02ServiceSupervisorTests' --no-restore --nologo`

   Result: passed, 12 passed, 0 failed, 0 skipped. Covered hard ordering, optional degradation/late rebind, cycle rejection, fresh planned replacement, exact dependency rebind, bounded restart/crash-loop, closable submitted work, ambiguous-effect quarantine, stale handles, revoked/ordinary capability denial and public DTO non-leak.

2. Related manifest/component/service/external-operation/reclaim regressions:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~Phase01ManifestContractsTests|FullyQualifiedName~ComponentAdmissionTests|FullyQualifiedName~ServiceDiscoverySessionTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~ReclaimObservabilityTests' --no-restore --nologo`

   Result: passed, 48 passed, 0 failed, 0 skipped.

3. Solution build:

   `dotnet build SingNextOS.slnx --no-restore --nologo`

   Result: succeeded, 0 warnings, 0 errors. The installed preview `.NET 11.0.100-rc.1.26425.128` SDK emitted informational `NETSDK1057` messages.

4. Solution tests:

   `dotnet test SingNextOS.slnx --no-build --no-restore --nologo`

   Result: passed across all four assemblies:

   - `HybridCpu_ExecutableAdapter.Tests`: 12 passed, 0 failed, 0 skipped;
   - `HybridCPU_NeutralRuntime.Tests`: 58 passed, 0 failed, 0 skipped;
   - `SingPlus.Platform.HybridCpu.Tests`: 60 passed, 0 failed, 0 skipped;
   - `SingPlus.Tests`: 1019 passed, 0 failed, 2 skipped.

   The two skips are explicit opt-in suspended-child qualification probes.

5. `git diff --check` completed with exit code 0 before solution qualification. Git reported existing LF-to-CRLF conversion notices but no whitespace errors. A final check is run after this evidence file is written.

## Remaining gaps and FutureGated items

- Phase 04 owns typed hierarchical monotonic deadlines and cancellation dispositions. Phase 02 backoff is monotonic, but drain timeout remains policy data and cannot imply closure.
- Phase 05 owns the single authoritative budget hierarchy/reservation ledger; restart limits are supervisor policy, not resource-budget authority.
- Phases 06 and 09 own causal trace and policy-scoped telemetry. Current snapshots are bounded point observations only.
- Phase 08 owns ordinary checkpoint classification and restore. Planned replacement recreates only the explicitly supplied definition and copies no checkpoint/provider state.
- A future explicit policy may select an isolated replacement resource while an exclusive old effect remains live; Phase 02 safely blocks instead of inventing this facility.
- Bootstrap dependency cycles, distributed supervision, cross-host replacement and production hardware failover are not implemented.
- No production hardware, physical CXL, QEMU, FPGA or silicon qualification was performed or claimed.

## HybridCPU boundary confirmation

Phase 02 did not modify HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture or CPU architecture. It did not create a SingNextOS-to-HybridCPU implementation dependency. Existing adapter/neutral-runtime projects were only compiled and tested through solution qualification.
