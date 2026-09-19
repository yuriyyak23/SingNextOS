# Phase 00 Implementation Evidence

Date: 2026-09-18 (Europe/Moscow)

## Baseline

- Repository: `C:\Users\Yuriy Kurnosov\Desktop\SingNextOS`.
- Baseline HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- Actual solution: `SingNextOS.slnx`.
- The baseline worktree was already substantially dirty. It contained modified and untracked work across build configuration, contracts, analyzers/generators, kernel/runtime/platform, prerequisite roadmap evidence, HybridCPU executable-adapter and neutral-runtime integration, tests and qualification tooling.
- Those pre-existing changes were preserved. In particular, the already modified `contracts/SingPlus.Contracts/SingPlus.Contracts.csproj` and all HybridCPU/prerequisite files were not edited by Phase 00.
- No reset, checkout, force, commit, push or remote Git operation was performed.

## Mandatory documents read

- `README.md` and `00-TECHNICAL-SPECIFICATION.md` in this roadmap.
- `12-pr-slicing-validation-and-exit-criteria.md`.
- `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md`.
- Directly related service, process, capability, region, external-operation, platform closure, manifest and test contracts.

## Audited dependencies and reused authoritative types

- `ProcessHandle`, `ProcessId`, domain handles and the kernel process/domain tables remain process/domain generation truth.
- `ServiceId`, `ServiceGeneration`, `ServiceEndpointDescriptor` and `ServiceRegistry` remain service endpoint generation truth.
- `CapabilityAuthority` remains local OS effect-authority truth.
- `RegionAuthority`, `OwnedRegion`, borrow leases and `RegionUse` remain ownership/use/reclaim truth.
- `ExternalOperationAuthority` remains external-operation admission, submission, completion, visibility, publication and release truth.
- `PlatformAuthorityBridge` and resource-specific provider receipts remain provider generation, closure, containment/quarantine and compensation truth.
- `SingProcessManifestV1` and `ServiceManifestV1` were reused as request/evidence models; neither is treated as a grant.
- Existing endpoint sessions and IPC/region machinery remain transfer and session-lifetime truth.

The detailed mapping for future manifest, supervisor, dependency, deadline/cancellation, budget, trace, IPC, checkpoint, telemetry and conformance facts is recorded in `00-current-state-mapping-and-operability-rules.md`.

## Additive contract-first implementation

`OperabilityGeneration.Compare` is the only new shared executable primitive. It provides equality-only comparison of non-zero generation values:

- exact values return `GenerationMatch.Exact`;
- zero on either side returns `GenerationMatch.Invalid`;
- every unequal value, including a fabricated larger value, returns `GenerationMatch.Stale`.

The comparator holds no state and mints no authority. Its caller must read the authoritative generation from the existing owning registry. `ServiceRegistry.Resolve` is the first real consumer and still owns the service generation and performs all identity, contract and availability checks under its existing lock.

No universal authority type, mutable lifecycle registry, generic closure state or second source of semantic truth was introduced.

## Authority, evidence and provider split

- Capability is still the only local authorization for a concrete OS resource effect.
- Generation comparison is validation evidence only.
- Manifest, health, trace, telemetry, inspector, replay and checkpoint concepts remain non-authoritative.
- Provider completion/visibility/closure evidence does not replace OS authority or ownership checks.
- `DeviceComplete`, `Visible`, `Published`, provider `Closed`/`Contained` and local `Released` remain distinct.
- `Unavailable`, timeout, cancellation, crash, restart, disconnect and malformed/unknown results do not prove closure or containment.

## Lifecycle, stale, quarantine and reclaim guarantees

- Generation equality is checked against the owning registry; no ordering or "future generation" trust exists.
- Restart/replacement terminology requires fresh service/process generation and fresh admission. Old capabilities, sessions, leases, cancellation scopes, subscriptions and receipts are stale.
- Ambiguous post-effect outcomes retain pins/accounting and use the owning subsystem's draining/quarantine path.
- Reclaim remains blocked until exact resource-specific closure or permitted containment is proven.
- External provider calls remain outside global/shared locks; state and generation must be revalidated after the call before committing its result.

## Public/SIP boundary

The new contract contains only `GenerationMatch` and a numeric comparison helper. It exposes no capability token, process/provider handle, CXL topology/BDF/HDM/DPA/HPA/fabric identity, raw mapping, provider-private lease/recovery token, secure backend fact, HybridCPU ISA/compiler metadata or replay certificate.

## Changed files

- `contracts/SingPlus.Contracts/OperabilityGeneration.cs` — shared stateless exact/stale/invalid comparison.
- `src/Runtime/SingPlus.Runtime/Services/ServiceRegistry.cs` — one real consumer using the shared comparison while retaining registry authority.
- `tests/SingPlus.Tests/Contracts/OperabilityGenerationTests.cs` — positive and fail-closed negative contract tests.
- `docs/post-cxl-operability-refactoring-roadmap/00-current-state-mapping-and-operability-rules.md` — ownership, terminology, lifecycle, boundary and FutureGated mapping.
- `docs/post-cxl-operability-refactoring-roadmap/README.md` — Phase 00 implementation-document link.
- This evidence file.

## Qualification commands and actual results

1. Focused contract and real-consumer tests:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~OperabilityGenerationTests|FullyQualifiedName~ServiceDiscoverySessionTests' --no-restore --nologo`

   Result: passed, 15 passed, 0 failed, 0 skipped.

2. Related manifest and external-operation lifecycle regressions:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~ManifestLifecycleTests|FullyQualifiedName~ExternalOperationLifecycleTests' --no-restore --nologo`

   Result: passed, 17 passed, 0 failed, 0 skipped.

3. Solution build:

   `dotnet build SingNextOS.slnx --no-restore --nologo`

   Result: succeeded, 0 warnings, 0 errors. The installed SDK identified itself as preview `.NET 11.0.100-rc.1.26425.128` via informational `NETSDK1057` messages.

4. Solution tests:

   `dotnet test SingNextOS.slnx --no-build --no-restore --nologo`

   Result: passed across all four test assemblies:

   - `HybridCpu_ExecutableAdapter.Tests`: 12 passed, 0 failed, 0 skipped;
   - `HybridCPU_NeutralRuntime.Tests`: 58 passed, 0 failed, 0 skipped;
   - `SingPlus.Platform.HybridCpu.Tests`: 60 passed, 0 failed, 0 skipped;
   - `SingPlus.Tests`: 1000 passed, 0 failed, 2 skipped.

   The two skips are opt-in suspended-child qualification probes and were reported explicitly by the test runner.

5. Diff validation:

   `git diff --check`

   Result: exit code 0. Git emitted existing LF-to-CRLF conversion warnings but no whitespace errors.

## Remaining gaps and FutureGated items

- Phase 01 must finish the versioned declarative service manifest and deterministic requested/granted/denied/degraded admission model; existing component manifests are prerequisites, not a claim that Phase 01 is complete.
- Service-instance/process-generation binding and dependency binding belong to Phase 02.
- Generation-bound monotonic cancellation scopes, budget ledger, trace sessions, checkpoint classification, telemetry projections and fault-plan sessions remain owned by their later phases.
- A common immutable closure/containment projection is FutureGated until multiple owning subsystems can project it without losing resource-specific semantics. No generic mutable closure registry is planned.
- No persistent authority reconstruction, cross-host/confidential migration, live hardware re-effect replay, mandatory zero-copy ABI or unprivileged production fault injection is claimed.
- No production hardware, physical CXL, QEMU, FPGA or silicon qualification was executed or claimed.

## HybridCPU boundary confirmation

Phase 00 did not change HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture or CPU architecture. It did not create a SingNextOS-to-HybridCPU implementation dependency. The executable adapter and neutral-runtime projects were only exercised by the existing solution build/tests; no files under those projects were modified by this phase.
