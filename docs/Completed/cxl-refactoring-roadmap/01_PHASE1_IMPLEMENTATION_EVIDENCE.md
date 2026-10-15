# Phase 1 — MutationEpoch and RegionUse Implementation Evidence

## Status and delta audit

**Complete for the provider-neutral, whole-region-conservative Phase 1
foundation.** Phase 0 assumptions remain valid: `RegionAuthority` is the only
memory ownership root; platform mapping-use is a separate bridge-private
interlock; and no common seven-state external-operation lifecycle or CXL
provider exists yet.

The delta audit found no pre-existing mutation epoch or region-use ledger.
Existing platform mapping, DMA/DSC1, visibility, completion, device-resource,
virtualization and evidence paths remain reusable but were not relabelled as
RegionUse, coherence, publication or CXL proof.

## Implemented contracts and authority route

The provider-neutral contracts are `MutationEpoch`, `RegionUseId`,
`RegionUseHandle`, `RegionUseRange`, `RegionUseMode`, `RegionUseState` and
`RegionUseDescriptor`. `RegionDescriptor` now reports its logical mutation
epoch. These contracts contain logical region, principal, range, mode and
generation facts only.

Owned-region admission follows:

```text
RuntimeKernel.AcquireRegionUse
  -> live ProcessHandle + effect admission
  -> RegionAuthority.Validate(region handle, owner, Owned)
  -> exact range validation
  -> whole-region compatibility + separate platform-mapping exclusion
  -> writable activation advances MutationEpoch
  -> opaque RegionUseHandle + immutable mutation/generation snapshot
```

Borrow admission follows the same ledger through
`AcquireBorrowRegionUse`/`AcquireBorrowUse`, but first validates the exact live
borrow generation, owner, borrower and lifetime. The existing borrow is
read-only; writable modes fail with `InsufficientRights`.

Providers must revalidate the opaque use through `ValidateRegionUse` before
submit and again before publication. Revalidation checks exact principal, use
state, region generation and mutation epoch. A stale use cannot be refreshed;
it must be released and reacquired. `InvalidateRegionUse` supplies an explicit
provider-neutral cancellation/fault hook. `ReleaseRegionUse` is idempotent and
does not transfer, return or recreate ownership.

## Compatibility and epoch decisions

- `ReadOnly` uses may coexist with other `ReadOnly` uses.
- `ExclusiveWrite`, `StagedOutput`, `DirectCoherentWrite` and `DevicePrivate`
  are writable and exclusive against every other current use.
- Compatibility is deliberately whole-region even when recorded ranges are
  disjoint. Current authority has no range-subdivision proof.
- `SharedReadMostly` remains `FutureGated` and admission reports unsupported.
- A separate active platform mapping and a RegionUse cannot coexist. Existing
  DMA/DSC1 mapping-use machinery was not rewritten in Phase 1.
- Ownership transfer, borrow grant/return/revoke, explicit region release and
  domain reclaim advance the epoch and invalidate earlier uses. A writable-use
  activation and its first release/invalidation also advance it.
- An active writable use blocks a new borrow grant. The current borrow contract
  exposes no mutable borrow mode; it must not be described as one.
- `OwnedBuffer<T>.Span`, `OwnedRegion<T>.Value` and local wrapper `Move()` do not
  traverse `RegionAuthority`. Their direct managed writes/moves are therefore
  not claimed as automatically observed mutations. Cross-process
  `RuntimeKernel.TransferRegion` is authority-mediated and does advance the
  epoch.
- A platform backend reset has no RegionUse/provider-binding correlation in
  Phase 1. Whether reset is observed or unavailable, it cannot refresh a use
  already invalidated by local authority. Narrow provider binding invalidation
  remains a Phase 4 responsibility.

## Positive and negative evidence

`RegionUseTests` proves deterministic provider-neutral behavior:

- read sharing and fake-provider submit/publication revalidation;
- writable activation/release epoch changes and retained ownership;
- direct managed `Span` writes do not falsely change the epoch;
- stale use rejection before submit and between submit and publish;
- whole-region writer exclusion even for disjoint ranges;
- writable use exclusion against borrow and separate mapping admission;
- read-only borrow rejection of writable use;
- invalidation on MOVE, borrow transition and reclaim;
- idempotent invalidation/release without authority resurrection;
- invalid range and future-gated mode rejection;
- public RegionUse/MutationEpoch surface rejection of raw CXL/PCIe/IOMMU
  identity vocabulary.

The fresh pre-change Phase 0 gate passed 94/94 focused tests. The pre-change
full solution passed 758 tests (628 SingPlus, 60 HybridCPU platform, 58 neutral
runtime and 12 executable-adapter tests).

Final Phase 1 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused authority/lifecycle/public-surface tests
  PASS — 171/171

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 769/769 total
    639 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Claims, gates and next-phase entry

Current claim: SingNextOS now has a provider-neutral logical MutationEpoch and
whole-region RegionUse reservation/revalidation foundation under the existing
authority root. This is local software authority evidence only.

`FutureGated`: range-safe subdivision, `SharedReadMostly`, direct coherent
final-region policy, common operation lifecycle, compute planning, narrow CXL
bindings, Type-3/Type-2 providers, replay/publication integration and dynamic
fabric handling.

`Unavailable` or `ExternalBlocked`: hardware CXL discovery/coherence, HDM
materialization, CXL IDE, Fabric Manager enforcement, QEMU/physical backend
qualification, zero-copy proof and writable multi-host sharing. No fake,
receipt, wrapper or documentation entry upgrades these claims.

Phase 2 may begin only when the final fresh restore, focused tests, full
solution qualification and dirty-worktree review are green. It must consume
RegionUse revalidation while introducing one provider-neutral seven-state
operation lifecycle that keeps submit, completion, visibility, publication and
release distinct; it must not introduce CXL providers or special SIP transport.
