# Phase 0 — Executable Baseline Audit and Readiness Gate

## Status

**Complete as a documentation and test-preparation gate; no CXL implementation
has been added.** This audit is scoped to the SingNextOS worktree and records
only mechanisms with current executable evidence. It is the required baseline
for Phase 1, not evidence of a CXL hardware/provider capability.

## Frozen decisions

1. `RegionAuthority` remains the sole root of memory ownership. CXL bindings,
   placement, fabric and security records are lower-layer provider facts or
   opaque platform bindings; none is a substitute capability.
2. Phase 1 starts with **whole-region conservative `RegionUse` compatibility**.
   Current `RegionAuthority` reserves a complete region for platform mapping or
   an external borrow-read grant. It has no verified sub-range ownership ledger.
   Disjoint concurrent use is future work and may be enabled only with a new
   range-safe authority model and tests.
3. A direct write through `OwnedBuffer<T>.Span` is not an observable authority
   transition. `MutationEpoch` must initially advance only on authority-mediated
   events (MOVE, borrow grant/return/revoke where incompatible, binding
   admission/release, backing replacement, reclaim/fault). A future claim that
   arbitrary managed writes invalidate an operation requires an explicit writer
   lease/guard; it must not be inferred from hardware coherence.
4. Existing DMA/DSC1 mapping-use exclusion is a bridge-private whole-mapping
   interlock, not `RegionUse`, CXL coherency, or a common operation lifecycle.
5. Existing completion, visibility, acquire and publication contracts are
   reusable inputs to Phase 2, but they are not one seven-state operation
   machine. Do not retrofit CXL fields into them during Phase 1.
6. The Phase-1 implementation remains provider-neutral. No CXL type, topology,
   raw address, IOMMU/PASID/HDM/DPA/route identity, or hardware claim may enter
   public contracts, SIP, diagnostics, or manifests.

## Code-grounded gap matrix

| Area | Classification | Current code and test evidence | Phase-1/next action |
|---|---|---|---|
| Region identity, generation, owner and state | Already exists | `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`; `tests/SingPlus.Tests/Ownership/OwnershipTests.cs` | Extend the record privately with mutation/use state; retain `RegionHandle` authority semantics. |
| `OwnedRegion` / `OwnedBuffer`, MOVE and borrow | Already exists | `RegionAuthority.Transfer`, `AcquireLoan`, `ReturnLoan`, `RevokeLoan`; `OwnershipTests`, `PlatformTwoDomainMoveTests`, `PlatformBorrowReadGrantTests` | Bump MutationEpoch only at specified authority transitions; do not create CXL buffer type. |
| Platform domain/mapping generations | Already exists | `PlatformAuthorityBridge.MappingV2.cs`, `RuntimeKernel.PlatformMove.cs`; `PlatformOwnedRegionMappingV2Tests`, `PlatformBackendResetEpochTests` | Include existing mapping/domain snapshots in a later operation admission snapshot. |
| Platform mapping / DMA / IOMMU-facing authority | Partially reusable | `PlatformAuthorityBridge.Dma*.cs`, `Device.cs`, `MappingUse.cs`; `PlatformDmaGrantTests`, `PlatformDmaCompletionTests`, `PlatformDmaDsc1MappingInterlockTests` | Generalize only after Phase 1 through provider-neutral RegionUse; CXL.mem CPU access must not be modelled as IOMMU. |
| DeviceLease, MMIO, IRQ and driver resource grouping | Already exists | `Components/DriverResourceSet.cs`, component admission; `DriverComponentVerticalSliceTests`, `PlatformDeviceLeaseTests`, `PlatformIrqBindingTests` | Reuse for CXL.io. Do not introduce a second CXL device authority. |
| Visibility, coherent access and cache maintenance | Partially reusable | `PlatformMemoryVisibilityContracts.cs`; host/HybridCPU provider adapters; `PlatformMemoryVisibilityTests`, `HybridCpuOwnedRegionMappingTests` | Phase 2/7 must keep coherent access, visibility and publication distinct. No CXL coherence claim is currently present. |
| Completion and reclaim | Partially reusable | DMA completion/post-completion and revocation bridge contours; `PlatformDmaPostCompletionLifecycleTests`, `PlatformRevocationLifecycleTests`, `ReclaimObservabilityTests` | Phase 2 introduces the provider-neutral seven-state lifecycle; do not equate completion with ownership return. |
| Compute service / accelerator path | Partially reusable | `RuntimeComputeServiceHost.cs`, `PlatformAuthorityBridge.Dsc1.cs`; `ComputeServiceDsc1CompositionTests`, `PlatformDsc1ComputeTests` | Phase 3 planner and Phase 6 Type-2 staged provider reuse ownership ingress, not provider identities or direct output semantics. |
| Virtualization / device memory sharing | Partially reusable | `VirtualDomainAuthority.cs`, child mapping/bounded-I/O paths; virtualization tests | No multi-host or dynamic fabric binding authority exists; keep advanced sharing gated. |
| Evidence / SecureCompute | Partially reusable | `PlatformEvidenceContracts.cs`, `RuntimeKernel.SecureCompute.cs`; `Phase7EvidenceSecureComputeTests` | CXL IDE/device facts are evidence only and cannot mint memory/device authority. |
| Provider feature taxonomy | Partially reusable | `PlatformFeatureContracts.cs`, `PlatformFeatureDiscoveryTests`, provider conformance matrix | Add CXL provider capability dimensions only after Phase 3; no `IsCxl` authority shortcut. |
| CXL discovery, Type-3 backing, Type-2 provider, fabric manager, IDE adapter, QEMU/hardware backend, multi-host protocol | Missing / ExternalBlocked where hardware proof is required | No current source/CXL public type or backend exists. | Begin no earlier than Phase 4, with fakes first and hardware claims only under Phases 8–10 external gates. |

## Current types to extend vs. types to introduce

Extend in Phase 1 or 2:

- private `RegionAuthority` record/descriptor path: mutation epoch and active
  use bookkeeping;
- RuntimeKernel ownership transitions: epoch advancement and use admission;
- provider-neutral platform operation/reclaim composition only after RegionUse;
- existing platform feature/evidence contracts only where a semantic CXL
  capability/evidence query is genuinely needed.

Introduce only when their phase is reached:

- Phase 1: provider-neutral `MutationEpoch`, `RegionUse` identity/mode/snapshot
  and opaque release token;
- Phase 2: provider-neutral external-operation state/identity/receipt types;
- Phase 3: compute planner, selection policy and dependency graph types;
- Phase 4+: narrow CXL provider interfaces and provider-private binding types.

Do not introduce `CxlRegion`, `CxlCapability`, `CxlFabricEpoch`, a monolithic
`ICxlProvider`, or public physical-topology identities.

## Documentation-only names at this baseline

The following roadmap names have no executable support today: `MutationEpoch`,
`RegionUse`, common `ExternalOperation` lifecycle, ComputePlanner/dependency
DAG, all CXL provider interfaces, Type-3 placement/backing, Type-2 service,
fabric binding generation, CXL discovery, HDM/decoder handling, Fabric Manager,
IDE evidence adapter, QEMU/physical backend, P2P and writable multi-host sharing.

## Phase-0 verification gate

Run these checks before beginning Phase 1 and attach their results to the Phase
1 change:

```powershell
dotnet restore SingNextOS.slnx --force --no-cache
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~OwnershipTests|FullyQualifiedName~PlatformMemoryVisibilityTests|FullyQualifiedName~PlatformDmaDsc1MappingInterlockTests|FullyQualifiedName~PlatformDmaPostCompletionLifecycleTests|FullyQualifiedName~DriverComponentVerticalSliceTests|FullyQualifiedName~Phase7EvidenceSecureComputeTests" --logger "console;verbosity=minimal"
dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
```

The focused group validates the pre-existing authority, mapping-use, visibility,
completion, device-resource and evidence boundaries. The full solution is the
final baseline qualification. Existing unrelated dirty-worktree failures must
be reported rather than repaired under this phase.

## Phase-1 entry criteria

- this audit and the original Phase-0 ADR decisions remain accepted;
- baseline focused tests and full qualification are green on a stable worktree;
- the implementation starts provider-neutral and whole-region conservative;
- public/dependency-surface checks reject CXL physical/provider identities;
- no HybridCPU-v2, ISA, compiler, replay or hardware-provider claim is added.
