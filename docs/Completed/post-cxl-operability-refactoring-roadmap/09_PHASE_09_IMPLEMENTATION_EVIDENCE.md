# Phase 09 Implementation Evidence — Structured Telemetry and Evidence Projection

## Baseline

- Phase-start HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- The worktree remained intentionally dirty with prerequisite work and Phases 00–08. All existing changes were preserved; no reset, checkout, commit, push, force, or remote Git operation was performed.
- Actual solution: `SingNextOS.slnx`.

## Audited dependencies and reused semantic owners

- `ResourceBudgetAuthority` remains the only source for limits, use, pressure, and telemetry-buffer reservations.
- `RegionAuthority` remains the only source for owned bytes and borrow/use/mapping/backing pins.
- `ChannelRegistry` and component endpoint-session state remain the IPC queue/session sources.
- `ExternalOperationAuthority` remains the only operation lifecycle/effect truth source.
- `CancellationScopeAuthority` remains the only deadline/cancellation disposition source.
- checkpoint records remain the checkpoint lifecycle/image-size source; trace authority remains the trace buffer/drop source.
- `CapabilityAwareServiceSupervisor` remains the health, dependency, restart, quarantine, and replacement-state source.
- Existing `EvidenceRecord`, `ReadSecureDomainEvidence`, evidence capabilities, subject identity, producer identity, freshness, visibility, and hardware-rooted flag remain the separate security-evidence path.

No mutable metric ledger duplicates these facts. Snapshots are computed projections; the only new mutable objects are bounded subscription queues and their lifecycle metadata.

## Implemented projections and policy

- Added typed projection classes: self operational, service aggregate, privileged system diagnostics, and debug trace metadata.
- Added typed budget, memory/pin, IPC channel/queue/session, ExternalOperation stage, deadline/cancellation, checkpoint, trace-buffer, and supervisor health/restart metrics.
- Self projection requires the exact live process generation and a manifest telemetry policy admitting `Self`.
- Service-aggregate self projection requires the stronger manifest policy. Cross-service and privileged projection requires the exact `TelemetryInspection` read capability.
- Supervisor is the real existing consumer for service-aggregate health/restart projection.
- Ordinary projections contain no CXL topology, BDF/HDM/DPA/route, provider recovery token, secure backend diagnostic, accelerator queue identity, capability secret, or confidential payload.
- DTOs explicitly do not authorize effects and cannot satisfy security evidence.

## Subscription lifecycle, stale, and budget guarantees

- Subscription identity is generation-bearing and binds exact owner/subject process generations, projection class, capacity, overflow policy, and state.
- Capacity is bounded by both the contract maximum and manifest `MaximumBufferedBytes`.
- Buffer capacity is reserved in the existing `TraceTelemetryBufferBytes` budget dimension before admission and released on explicit close or process teardown.
- Overflow behavior is explicit: drop-oldest with an incomplete marker count, reject/backpressure, or stop.
- Restart does not rebind a subscription. The retired owner handle is stale; the fresh generation cannot read the old subscription and must create a new one.
- Teardown closes subscriptions before budget hierarchy retirement. A crash or restart does not turn telemetry into authority or infer closure of external effects.

## Telemetry versus security evidence

- `StructuredTelemetrySnapshot` and `SecurityEvidenceProjection` are distinct types. Telemetry exposes `SatisfiesSecurityEvidence == false`.
- The runtime does not accept telemetry as evidence and does not mint an evidence record from telemetry.
- Security evidence continues through the pre-existing typed evidence capability/provider APIs; the projection preserves `EvidenceFreshness` and `HardwareRooted` without changing their meaning.
- Neither telemetry nor evidence is a capability, provider lease, closure receipt, or reclaim authorization.

## Integration and tests

The real integration is `CapabilityAwareServiceSupervisor.ProjectTelemetry`, which projects the exact managed `ServiceInstanceHandle` state. IPC queue depth is also read from the existing live channel registry, and checkpoint/trace/budget values are checked against their owning subsystems.

Focused and related regression command:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase09StructuredTelemetryTests|FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~Phase06DeterministicTracingTests|FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~Phase03AuthorityInspectorTests|FullyQualifiedName~Phase02ServiceSupervisorTests|FullyQualifiedName~SecureCompute|FullyQualifiedName~Evidence|FullyQualifiedName~Phase9GuiOwnershipTests.PublicSurfaceAndDependenciesContainNoHardwareProviderOrExternalRuntimeLeakage" --nologo
```

Result: passed, 118/118. The Phase 09 fixture contributes seven tests covering source-consistent self metrics, cross-service denial and exact capability admission, public vocabulary non-leak, bounded overflow, stale generation/restart behavior, manifest/budget limits and release, evidence/telemetry type separation, IPC queue depth, and supervisor health integration.

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
- `SingPlus.Tests`: 1072 passed, 2 skipped, 0 failed;
- `git diff --check`: exit 0; only existing LF-to-CRLF worktree warnings were emitted.

## Changed files for this phase

- `contracts/SingPlus.Contracts/StructuredTelemetryContracts.cs`
- `contracts/SingPlus.Contracts/CapabilityResourceIds.cs`
- `src/Runtime/SingPlus.Runtime/Observability/RuntimeKernel.StructuredTelemetry.cs`
- `src/Runtime/SingPlus.Runtime/Services/CapabilityAwareServiceSupervisor.cs`
- `src/Runtime/SingPlus.Runtime/Deadlines/CancellationScopeAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Tracing/DeterministicTraceAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs`
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs`
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.ProcessTeardown.cs`
- `src/Runtime/SingPlus.Runtime/KernelResult.cs`
- `tests/SingPlus.Tests/Runtime/Phase09StructuredTelemetryTests.cs`
- this phase document, roadmap index, and this evidence file.

## Remaining gaps / FutureGated

- CPU runtime sampling, IPC latency/throughput histograms, detailed provider-neutral reset/fault taxonomy, and checkpoint duration histograms require stable owning counters and remain FutureGated; they are not fabricated from trace records.
- Privileged diagnostics currently projects one explicitly selected subject, not an unbounded system-wide dump. Bounded multi-subject queries remain FutureGated.
- Subscriptions are explicit pull-sampled queues rather than a background timer or push transport. No delivery/QoS guarantee is claimed.
- Projection consistency is point-in-time/best-effort across multiple existing authorities; strongly consistent cross-authority freezing is intentionally not claimed.
- Security evidence capabilities and freshness are covered by the prerequisite secure-compute implementation and its regression tests; this phase did not duplicate that provider path.
- No production, hardware, CXL, confidentiality, or performance claim is made. Qualification used repository model providers and the .NET 11 preview test environment.
