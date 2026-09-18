# Phase 03 Implementation Evidence

Date: 2026-09-18 (Europe/Moscow)

## Baseline

- Repository: `C:\Users\Yuriy Kurnosov\Desktop\SingNextOS`.
- Baseline HEAD: `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- Actual solution: `SingNextOS.slnx`.
- The worktree was already substantially dirty from prerequisite, build-system, runtime, test and HybridCPU adapter work plus completed Phases 00–02. All pre-existing and parallel changes were preserved.
- No reset, checkout, force, commit, push or remote Git operation was performed.

## Audited dependencies and reused types

- `ProcessRegistry` remains live/terminal process-generation truth.
- `CapabilityAuthority` remains capability validation/revocation truth. It now retains only the delegation-parent fact needed for provenance and supplies a detached diagnostic snapshot.
- `RegionAuthority` remains owner, borrow, backing, mapping-reservation and `RegionUse` truth. It supplies one detached composite diagnostic snapshot rather than a second registry.
- `ExternalOperationAuthority` remains effect lifecycle, closure and region-pin truth.
- `ServiceRegistry` remains service generation/provider truth. It minimally retains immutable N -> N+1 replacement lineage when its existing retire/register lifecycle advances a service.
- Existing endpoint-session diagnostics and process/platform reclaim diagnostics remain service-binding and reclaim-blocker truth.
- Existing VirtualDomain and SecureDomain registries remain domain lifecycle truth and expose only redacted diagnostic projections.
- Phase 02 supervisor/service replacement is consumed through the existing service registry and process teardown paths; the inspector does not participate in the IPC, compute or provider fast path.

## Implemented scope

- Versioned `AuthorityInspectionContract` with typed node, edge, blocker, consistency and visibility families.
- Node families for process/domain, service instance, capability, owned region, borrow, `RegionUse`, backing lease, platform mapping, device/DMA, ExternalOperation, virtual/secure domain, service binding, budget reservation and checkpoint pin.
- Typed `Owns`, `MintedBy`, `DelegatedTo`, `BorrowedBy`, `MappedInto`, `BackedBy`, `Pins`, `Requires`, `WaitingForClosure`, `AuthorizedBy`, `QuarantinedBy` and `ReplacedBy` edge families.
- Self-scoped inspection without ambient cross-service access and system inspection gated by an exact revocable `kernel:authority-inspector:v1` read capability.
- `InspectOwner`, `InspectDependents`, `InspectCapabilityProvenance`, `WhyMoveBlocked`, region/process `WhyReclaimBlocked`, `WhyServiceDrainBlocked` and `WhyExternalOperationPinned`.
- Exact typed explanations for borrow, `RegionUse`, backing, mapping, endpoint session, device/DMA, publication, provider closure, uncontained effect, stale generation and quarantine blockers.
- Point-in-time capture, bounded convergent strong capture and immutable historical service-replacement reference capture.
- Explicit stale node projection instead of merging an old generation into the current node.

## Authority, evidence and provider split

- Inspector snapshots, nodes, edges and explanations are detached observations; both snapshot and explanation expose `AuthorizesMutation == false`.
- DTOs contain `AuthorityNodeId`, a SHA-256 correlation value, instead of `CapabilityId`, `ProcessHandle`, `RegionHandle`, `ExternalOperationHandle` or provider lease/token types.
- Query inputs may use existing exact handles, but every query independently validates the caller process and, for system scope, the inspection capability. Snapshot output cannot be passed to an effect API.
- Capability revocation immediately makes an existing system inspector fail with `ProjectionDenied`.
- Provider effect closure, containment and reclaim are read from existing resource-specific authorities. The inspector cannot alter them.
- Platform diagnostic identifiers are hashed and reduced to semantic classes; provider identity, topology, physical mappings and recovery material are not returned.
- No mutable graph database or duplicate ownership/generation/closure registry was introduced.

## Consistency, stale, quarantine and reclaim guarantees

- `PointInTimeBestEffort` performs one detached on-demand projection.
- `AuthorityLockedSnapshot` performs bounded full-projection double capture and returns only after two exact fingerprints converge; three failed attempts return `SnapshotUnstable` rather than a falsely strong result.
- `HistoricalReference` currently returns only immutable service replacement lineage and never presents it as live authority.
- Stale region and service generations have separate stale nodes/reasons and are not conflated with current state.
- Active `RegionUse`, borrow, backing and platform mapping facts identify exact semantic blocker classes.
- Submitted ExternalOperations identify exact operation and region-use pins; provider loss/fault is reported as uncontained rather than closed.
- Process/service drain explanations reuse existing teardown and external-operation truth. Crash, timeout intent, health failure or caller disconnect cannot manufacture closure.
- Ambiguous/quarantined platform diagnostics remain blockers; inspection does not release local pins or enable replacement.

## Public/SIP and redaction boundary

- Reflection tests verify that inspection DTO properties contain no reusable capability/process/region/operation handle, provider lease/binding, CXL/BDF/HDM/DPA/Fabric Manager, recovery-token, HybridCPU opcode or replay-certificate type vocabulary.
- Ordinary self scope cannot inspect an unrelated process region. Cross-service projection requires the dedicated capability.
- Secure domain projection contains only opaque identity, generation and semantic lifecycle state; raw secure binding/evidence is not exposed.
- Node state/detail strings contain semantic states/reasons, not host topology, confidential payloads or provider credentials.

## Changed files

- `contracts/SingPlus.Contracts/AuthorityInspectionContracts.cs`.
- `contracts/SingPlus.Contracts/CapabilityResourceIds.cs`.
- `src/Runtime/SingPlus.Runtime/KernelResult.cs`.
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/Services/ServiceRegistry.cs`.
- `src/Runtime/SingPlus.Runtime/Virtualization/VirtualDomainAuthority.cs`.
- `src/Runtime/SingPlus.Runtime/Observability/AuthorityInspector.cs`.
- `tests/SingPlus.Tests/Runtime/Phase03AuthorityInspectorTests.cs`.
- `docs/post-cxl-operability-refactoring-roadmap/03-authority-inspector-and-provenance-graph.md`.
- `docs/post-cxl-operability-refactoring-roadmap/README.md`.
- This evidence file.

## Focused and regression qualification

1. Focused authority inspector positive/negative tests:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~Phase03AuthorityInspectorTests' --no-restore --nologo`

   Final result: passed, 11 passed, 0 failed, 0 skipped. Covered self/system scope, capability revocation, delegation provenance/redaction, borrow/use/backing/mapping blockers, ExternalOperation closure pins, process/service drain explanations, stale isolation, historical replacement lineage and DTO non-authority.

   An earlier focused run had one test expectation mismatch: the owning region authority correctly returned `StaleGeneration` before owner validation after MOVE. The test was corrected to the authoritative fail-closed ordering; runtime semantics were not weakened.

2. Owning authority/lifecycle regressions:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~CapabilityAuthorityTests|FullyQualifiedName~OwnershipTests|FullyQualifiedName~RegionUseTests|FullyQualifiedName~ServiceDiscoverySessionTests|FullyQualifiedName~Phase02ServiceSupervisorTests|FullyQualifiedName~ReclaimObservabilityTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~PlatformOwnedRegionMappingV2Tests|FullyQualifiedName~VirtualizationLocalModelTests|FullyQualifiedName~Phase7EvidenceSecureComputeTests' --no-restore --nologo`

   Result: passed, 117 passed, 0 failed, 0 skipped.

3. Solution build:

   `dotnet build SingNextOS.slnx --no-restore --nologo`

   Result: succeeded, 0 warnings, 0 errors. The installed preview `.NET 11.0.100-rc.1.26425.128` SDK emitted informational `NETSDK1057` messages.

4. Solution tests:

   `dotnet test SingNextOS.slnx --no-build --no-restore --nologo`

   Result: passed across all four assemblies:

   - `HybridCpu_ExecutableAdapter.Tests`: 12 passed, 0 failed, 0 skipped;
   - `HybridCPU_NeutralRuntime.Tests`: 58 passed, 0 failed, 0 skipped;
   - `SingPlus.Platform.HybridCpu.Tests`: 60 passed, 0 failed, 0 skipped;
   - `SingPlus.Tests`: 1030 passed, 0 failed, 2 skipped.

   The two skips are explicit opt-in suspended-child qualification probes.

5. `git diff --check` completed with exit code 0 before solution qualification. Git reported existing LF-to-CRLF conversion notices but no whitespace errors. A final check is run after this evidence file is written.

## Remaining gaps and FutureGated items

- Budget-reservation and checkpoint-pin node kinds are versioned now, but no instances are fabricated until Phase 05 and Phase 08 provide their single authoritative registries.
- Phase 06 owns trace sessions and causal history. `TraceCorrelationId` therefore remains unset and general historical reconstruction is not claimed.
- `AuthorityLockedSnapshot` is a bounded convergent cut for the current model runtime, not a production claim of a kernel-wide multi-writer read epoch. It fails closed with `SnapshotUnstable`; a future production concurrency pass may replace it with an explicit shared read epoch.
- Historical retention is bounded to service replacement lineage; capability/resource event history belongs to trace, not to a second inspector history store.
- No persistent graph store, mutation API, ambient host inspector, cross-host inspection or confidential backend projection was introduced.
- No production hardware, physical CXL, QEMU, FPGA or silicon qualification was performed or claimed.

## HybridCPU boundary confirmation

Phase 03 did not modify HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture or CPU architecture. It did not create a SingNextOS-to-HybridCPU implementation dependency. Existing adapter/neutral-runtime projects were only compiled and tested through solution qualification.
