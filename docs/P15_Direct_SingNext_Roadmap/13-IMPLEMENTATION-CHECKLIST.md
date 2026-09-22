# P15.12 — Concrete implementation checklist

## Contracts and repository structure

- [ ] Create `contracts/HybridCpu.Boot.Contracts/`.
- [ ] Move all current Boot.Contracts sources without wire changes.
- [ ] Add golden before/after codec vectors.
- [ ] Update `SingNextOS.slnx`.
- [ ] Update `RepositoryArchitecturePolicyTests` layer classification.
- [ ] Update all project references/lock files.
- [ ] Add `HybridPlatformDescriptorV1`.
- [ ] Move/freeze `KernelEntryAbiV1`.
- [ ] Add capsule image/manifest generation records.

## Boot Core

- [ ] Create `src/Kernel/Boot/SingNext.Boot.Core`.
- [ ] Promote `CxlBootSelectionModel` semantics.
- [ ] Promote trust/rollback decision logic.
- [ ] Promote A/B/recovery state machine.
- [ ] Promote verified component loader.
- [ ] Promote aperture validation/transaction semantics.
- [ ] Split protocol parsing from deterministic fake transport.
- [ ] Add property/differential tests.

## Capsule

- [ ] Create `src/Kernel/Boot/SingNext.Boot.Capsule`.
- [ ] Add `BootCapsule` static admission profile.
- [ ] Define no-heap/bounded-heap policy.
- [ ] Add `CapsuleEntryPoint`.
- [ ] Add deterministic staged pipeline.
- [ ] Add bounded scratch allocator/buffers.
- [ ] Add failure/recovery dispatch.
- [ ] Ensure no runtime authority references.

## HybridCPU boot platform adapter

- [ ] Create `src/Platform/SingPlus.Platform.HybridCpu.Boot`.
- [ ] Implement platform descriptor source.
- [ ] Implement reset sequence/reason source.
- [ ] Implement boot debug sink.
- [ ] Implement local capsule/recovery reads.
- [ ] Implement bounded PCI config access.
- [ ] Implement CXL discovery/mailbox transport.
- [ ] Implement persistent capacity streaming read.
- [ ] Implement temporary HDM create/destroy/quarantine.
- [ ] Implement protected state backend profile.
- [ ] Implement asymmetric signature verifier.
- [ ] Implement bus-master-off / early IOMMU policy.

## HybridCPU toolchain

- [ ] Reconcile current HybridCPU master revision with qualification tooling.
- [ ] Replace `ExternalBlocked` AOT stage with actual artifact generation for explicit profile.
- [ ] Emit capsule image.
- [ ] Emit kernel image.
- [ ] Emit managed bootstrap metadata.
- [ ] Prove deterministic image digest.
- [ ] Prove ISE loader acceptance.

## Kernel handoff

- [ ] Add physical/fixed-width kernel entry ABI.
- [ ] Validate and copy BootInfo early.
- [ ] Wire `HybridBootInfoImporter` into actual entry path.
- [ ] Implement production `IFreshCxlBootDiscovery`.
- [ ] Implement production `IFirmwareApertureRetirement`.
- [ ] Ensure failure never adopts boot mapping.

## A/B, rollback and recovery

- [ ] Capsule A/B state distinct from OS A/B state.
- [ ] Trial nonce and attempt budget.
- [ ] Protected rollback floor.
- [ ] Explicit `ConfirmBoot` control path.
- [ ] Power-loss barrier tests.
- [ ] Local signed recovery with CXL absent.

## Qualification

- [ ] Contract corpus.
- [ ] Boot Core property corpus.
- [ ] Differential old-model/new-core corpus.
- [ ] ISE positive path.
- [ ] ISE negative matrix.
- [ ] Optional QEMU CXL lane.
- [ ] Hardware-gated reset/ROM/CXL/HDM/store/IOMMU lane.
- [ ] Canonical qualification report and claim matrix.
