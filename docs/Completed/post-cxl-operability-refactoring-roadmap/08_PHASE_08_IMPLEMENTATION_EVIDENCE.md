# Phase 08 Implementation Evidence — Ordinary-domain Checkpoint and Restore

## Baseline

- Phase-start HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- The Git worktree was already intentionally dirty with prerequisite work and Phases 00–07. It was preserved: no reset, checkout, commit, push, force operation, or remote Git operation was performed.
- Actual solution used for qualification: `SingNextOS.slnx`.

## Audited dependencies and reused ownership

- `ComponentAdmissionRecord` and `RuntimeKernel` remain the source for the exact component/process generation and retained admission plan.
- `ProcessRegistry` remains the process-generation source; restore cannot reuse a retired process handle.
- `CapabilityAuthority` remains the only effect authority. Checkpoint mutation requires the exact `CheckpointAdministration`/`Configure` capability; an image, digest, snapshot, or receipt authorizes nothing.
- `RegionAuthority` remains the source for owned-memory state, borrow/use/mapping/backing pins, generation, and release.
- `ExternalOperationAuthority` remains the source for operation effect/closure truth. Any operation not exactly `Released` blocks the snapshot.
- `ResourceBudgetAuthority` remains the single reservation ledger. Checkpoint bytes are charged to the existing system `CheckpointStorageBytes` dimension and survive source-process retirement until explicit checkpoint deletion.
- Existing supervisor/component drain and fresh manifest admission are reused for planned replacement; no parallel service, ownership, closure, or budget registry was introduced.
- Existing deterministic tracing records only checkpoint lifecycle correlation; it is evidence, not authority.

## Implemented contract and lifecycle

- Added versioned checkpoint identities, handles, lifecycle states, resource classifications, image/region records, detached status snapshots, and restore receipts.
- Lifecycle is explicit: `Requested -> Quiescing -> Snapshotting -> Validating -> Committed`, or `Failed`; deleted committed images become `Deleted`. Partial/non-committed images are not restorable.
- Every known resource family is classified as `Checkpointable`, `RecreateOnRestore`, `RequiresDrain`, or `NonCheckpointable(reason)`. Unknown or unselected owned memory defaults to `NonCheckpointable`.
- Manifest, logical state, and explicitly selected quiescent byte buffers are checkpointable. Capabilities, budgets, cancellation scopes, trace/telemetry subscriptions, and dependency bindings require fresh admission/recreation.
- Live IPC channels/sessions require drain. Live/uncontained external operations, platform/device/DMA state, secure domains, virtual domains, and unknown memory fail closed.
- The selected region descriptors are checked before and after copying; a generation/state change fails the checkpoint. Operation state is checked again after copying.
- Image and region digests are validated. Returned images are cloned so callers cannot mutate the authoritative stored image through shared arrays.
- Restore requires a complete committed image, matching schema/component/version/image identity, the same logical process identity, and a strictly newer generation.
- Planned replacement drains the old generation to exact reclaimability, retires it, then executes fresh manifest/capability/dependency/budget admission. Old process, capability, and region handles remain stale.
- Failed fresh admission is explicit and never falls back to serialized authority.
- Read-only status projection is limited to the exact source process or a caller holding the exact checkpoint-administration capability.

## Authority, evidence, and provider split

- Checkpoint data is ordinary state only. `ContainsLiveAuthority`, `RestoresExternalAuthority`, `ReusedCapability`, and `ReusedExternalAuthority` are false by contract.
- Manifest/image digests, trace events, resource dispositions, and restore receipts are correlation/evidence only.
- No provider lease, recovery token, hardware evidence, CXL topology, BDF/HDM/DPA/route, secure-private state, or HybridCPU internal is serialized or exposed.
- Provider unavailability, timeout, cancellation, crash, or disconnect is not treated as closure. A live or ambiguous external effect blocks checkpoint and replacement until the owning lifecycle reports exact release/containment.

## Integration and tests

Real integration is the managed component planned-replacement path: an admitted service with owned memory is checkpointed, exactly drained, retired, freshly admitted at generation N+1, restored, traced, and later releases the checkpoint-storage reservation.

Focused and related regression command:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~Phase02ServiceSupervisorTests|FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~Phase06DeterministicTracingTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~ProcessTeardownLifecycleTests|FullyQualifiedName~ComponentAdmissionTests" --nologo
```

Result: passed, 64/64. The Phase 08 fixture contributes five positive/negative tests covering fresh-generation restore, stale capability rejection, exact external-operation closure, secure/unknown-resource blocking, unauthorized administration, partial/tampered/incompatible images, storage charge/release, and changed fresh-admission outcome.

The first full solution test exposed a public-surface guard failure because an otherwise-negative DTO property contained the provider-specific term `ProviderLease`. It was corrected to provider-neutral external-authority terminology. The focused Phase 08 plus public-boundary guard then passed 6/6, and the full qualification below was rerun successfully.

Final qualification:

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
- `SingPlus.Tests`: 1065 passed, 2 skipped, 0 failed;
- `git diff --check`: exit 0; only existing LF-to-CRLF worktree warnings were emitted.

## Changed files for this phase

- `contracts/SingPlus.Contracts/CheckpointContracts.cs`
- `contracts/SingPlus.Contracts/CapabilityResourceIds.cs`
- `src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs`
- `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Components/ComponentAdmission.cs`
- `src/Runtime/SingPlus.Runtime/KernelResult.cs`
- `tests/SingPlus.Tests/Runtime/Phase08OrdinaryCheckpointTests.cs`
- this phase document, roadmap index, and this evidence file.

## Remaining gaps / FutureGated

- Arbitrary typed object graphs and non-byte owned buffers are not serialized; services must provide bounded logical bytes and explicitly selected byte buffers.
- Quiescence is synchronous and fail-closed. Coordinated asynchronous stop-new-work, bounded drain, and user-requested abort of an in-progress snapshot remain FutureGated.
- Active IPC is not serialized; it must be closed/drained and rebound under the fresh generation.
- Secure/private state, live virtual domains, device/DMA/platform state, uncontained external effects, raw leases, cross-host migration, and opaque host evidence remain intentionally unsupported.
- A restore admission failure leaves the old generation retired after its proven reclaimable drain; higher-level rollback/restart policy remains supervisor policy, not checkpoint authority.
- No production performance, hardware, CXL, confidential-compute, or transparent-live-migration claim is made. Qualification used the repository's .NET 11 preview model/test environment only.
