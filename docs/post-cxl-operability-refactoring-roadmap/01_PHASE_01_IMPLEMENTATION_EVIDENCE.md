# Phase 01 Implementation Evidence

Date: 2026-09-18 (Europe/Moscow)

## Baseline

- Repository: `C:\Users\Yuriy Kurnosov\Desktop\SingNextOS`.
- Baseline HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- Actual solution: `SingNextOS.slnx`.
- Worktree was already substantially dirty from prerequisite, build-system, runtime, test and HybridCPU adapter work plus completed Phase 00 changes. All pre-existing changes were preserved.
- No reset, checkout, force, commit, push or remote Git operation was performed.

## Audited dependencies and reused types

- `SingProcessManifestV1` remains the single process identity/generation, entry-point, capability-request and resource-limit declaration.
- Existing `ServiceId`, `ServiceGeneration`, `ServiceEndpointDescriptor`, `ServiceRegistry` and endpoint-session registries remain service discovery/binding truth.
- Existing `ComponentAdmissionPlan`, `ComponentAdmissionRecord` and `RuntimeKernel.AdmitComponent` remain the single component materialization and rollback path.
- `CapabilityAuthority` remains local authority truth; manifest capability declarations and decisions do not mint rights.
- `PlatformAuthorityBridge` and provider feature manifest remain platform admission/binding truth.
- Existing process/component teardown performs exact rollback when hard dependency binding fails after process/platform materialization.
- Phase 00 `OperabilityGeneration.Compare` remains the shared stateless equality-only stale check and is not a manifest authority.

## Implemented scope

- Versioned service identity contract and exact `ServiceInstanceHandle` correlation over service id/generation, process generation and domain.
- Versioned `ServiceManifestV1` schema id/version and deterministic canonical SHA-256 normalized digest.
- Typed hard and optional dependencies; legacy `requiredContracts` normalize to hard dependencies inside the same model.
- Mandatory/optional provider-neutral platform feature requests.
- Local capability and platform authority-domain requests reusing existing types.
- Declarative CPU/memory/IPC/external/device/guest/checkpoint/trace-telemetry budget dimensions.
- Restart, drain, ordinary-checkpoint and telemetry policy declarations.
- Runtime compatibility constraints.
- Typed `Requested`, `Granted`, `Denied`, `Unsupported` and `Degraded` decisions plus overall granted/degraded/denied admission disposition.
- Read-only `EvaluateComponentAdmission` preflight and integration into the existing real `AdmitComponent` consumer.

## Authority, evidence and provider split

- Manifest is configuration/request data only.
- `EvaluateComponentAdmission` is a point-in-time evidence projection. `MaterializesAuthority` is always false.
- Successful capability materialization changes only redacted decision evidence from `Requested` to `Granted`; no `CapabilityId` enters the admission result.
- Provider authority-domain materialization similarly omits provider-private binding identity.
- Normalized digest correlates configuration and never substitutes for `ProcessHandle`, `ServiceInstanceHandle`, capability validation or provider admission.
- Budget requests remain `Requested`; Phase 01 does not emulate the Phase 05 ledger or claim quota grants.

## Lifecycle, stale, degradation and rollback guarantees

- Unknown mandatory platform feature produces `Denied` and `KernelError.PlatformUnsupported` before process/provider authority creation.
- Unknown optional platform feature produces `Unsupported` and overall `Degraded` without aborting the component.
- Missing hard dependency is typed denied by preflight, then uses the existing authoritative admission/rollback path so the component records `Reclaimable` and its process handle is stale.
- Missing or unbindable optional dependency does not abort the component; the exact decision becomes unsupported/degraded and no session is retained.
- Service instances bind the exact service generation to the exact process generation. Phase 01 does not implement restart/rebinding; those are Phase 02 responsibilities.
- Manifest policies cannot infer provider closure, containment or reclaim and do not alter external-operation lifecycle semantics.

## Public/SIP and provider-neutral boundary

Reflection tests verify that manifest, admission-result and service-instance surfaces contain no CXL/BDF/HDM/DPA/Fabric Manager, provider lease, HybridCPU lane/opcode or replay-certificate vocabulary. No capability token, provider recovery token or confidential payload is serialized into the normalized manifest.

## Changed files

- `contracts/SingPlus.Contracts/Services.cs`.
- `contracts/SingPlus.Contracts/ComponentManifests.cs`.
- `contracts/SingPlus.Contracts/OperabilityManifestContracts.cs`.
- `contracts/SingPlus.Contracts/ServiceManifestCanonicalization.cs`.
- `src/Runtime/SingPlus.Runtime/Components/ComponentAdmission.cs`.
- `src/Runtime/SingPlus.Runtime/Components/RuntimeKernel.Components.cs`.
- `src/Runtime/SingPlus.Runtime/Components/RuntimeKernel.ManifestAdmission.cs`.
- `tests/SingPlus.Tests/Runtime/Phase01ManifestContractsTests.cs`.
- `docs/post-cxl-operability-refactoring-roadmap/01-foundation-contracts-and-declarative-manifests.md`.
- `docs/post-cxl-operability-refactoring-roadmap/README.md`.
- This evidence file.

## Focused and regression qualification

1. Focused manifest, component admission and lifecycle tests:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~Phase01ManifestContractsTests|FullyQualifiedName~ComponentAdmissionTests|FullyQualifiedName~ManifestLifecycleTests' --no-restore --nologo`

   Final result: passed, 28 passed, 0 failed, 0 skipped.

   An earlier regression run exposed premature preflight rejection of a missing hard dependency, which bypassed the existing observable rollback record. The implementation was corrected to retain typed denial evidence while preserving the authoritative component rollback lifecycle; the final focused run above verifies the correction.

2. Real component-consumer regressions:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~DriverComponentVerticalSliceTests|FullyQualifiedName~NativeSystemServiceVerticalSliceTests|FullyQualifiedName~ComputeServiceSessionMigrationTests|FullyQualifiedName~Phase9GuiOwnershipTests|FullyQualifiedName~ReclaimObservabilityTests' --no-restore --nologo`

   Result: passed, 38 passed, 0 failed, 0 skipped.

3. Solution build:

   `dotnet build SingNextOS.slnx --no-restore --nologo`

   Result: succeeded, 0 warnings, 0 errors. The installed preview `.NET 11.0.100-rc.1.26425.128` SDK emitted informational `NETSDK1057` messages.

4. Solution tests:

   `dotnet test SingNextOS.slnx --no-build --no-restore --nologo`

   Result: passed across all four assemblies:

   - `HybridCpu_ExecutableAdapter.Tests`: 12 passed, 0 failed, 0 skipped;
   - `HybridCPU_NeutralRuntime.Tests`: 58 passed, 0 failed, 0 skipped;
   - `SingPlus.Platform.HybridCpu.Tests`: 60 passed, 0 failed, 0 skipped;
   - `SingPlus.Tests`: 1007 passed, 0 failed, 2 skipped.

   The two skips are explicit opt-in suspended-child qualification probes.

5. `git diff --check` completed with exit code 0 before the solution build. Git reported existing LF-to-CRLF conversion notices but no whitespace errors. A final check is run after this evidence file is written.

## Remaining gaps and FutureGated items

- Dependency graph cycle detection, exact dependency-generation bindings, readiness and rebinding belong to Phase 02.
- Restart/drain policies are declarations; Phase 02 owns their state machine and enforcement.
- Drain timeout uses a declarative duration only; monotonic hierarchical deadlines and cancellation dispositions belong to Phase 04.
- Budget requests are not grants and remain requested until the single Phase 05 authoritative ledger exists.
- Checkpoint policy does not make resources checkpointable and does not restore authority; classification/execution belongs to Phase 08.
- Telemetry policy does not create a subscription or visibility right; Phase 09 owns that path.
- No general persistent service database, distributed scheduler, confidential checkpoint, cross-host migration, zero-copy ABI promise or live effect replay was introduced.
- No production hardware, physical CXL, QEMU, FPGA or silicon qualification was performed or claimed.

## HybridCPU boundary confirmation

Phase 01 did not modify HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture or CPU architecture. It did not create a SingNextOS-to-HybridCPU implementation dependency. Existing adapter/neutral-runtime projects were only compiled and tested through the solution qualification.
