# Implementation Checklist

## P15-00 baseline

- [ ] Inventory all requested projects/paths on current `master`.
- [ ] Locate exact definitions/consumers for all boot/CXL/authority objects.
- [ ] Verify `RegionAuthority`, `OwnedRegion`, `RegionUse` ownership.
- [ ] Verify runtime reset-epoch owner and `ObservePlatformBackendReset()` or equivalent.
- [ ] Verify `SingPlus.Boot` responsibilities before moving any logic.
- [ ] Verify HybridCPU current compiler/bootstrap/linker/loader/reset/ISE behaviors.
- [ ] Record current qualification pins and stale-pin drift.
- [ ] Emit `DirectSingNextBootBaselineV1.json`.

## Structure/contracts

- [ ] Boot.Contracts is wire/ABI only.
- [ ] Boot.Core owns in-process boot ports and pure policy.
- [ ] Platform adapter implements ports and does not reference Capsule.
- [ ] Capsule is the composition root.
- [ ] Capsule has no `SingPlus.Runtime`/authority shortcut.
- [ ] Architecture policy rejects forbidden edges.

## Core semantics

- [ ] bounded parsers/range arithmetic;
- [ ] deterministic candidate selection;
- [ ] manifest/signature/hash policy;
- [ ] verified destination hash after copy;
- [ ] separate capsule/image A/B generations;
- [ ] rollback floor;
- [ ] trial nonce/attempts/confirmation;
- [ ] bounded local recovery;
- [ ] temporary mapping state machine.

## Platform backend

- [ ] DMA denied before device enable;
- [ ] bounded PCI enumeration/capability walk;
- [ ] DVSEC/Type-3 validation;
- [ ] bounded CCI/mailbox;
- [ ] timeout/link-loss/reset invalidation;
- [ ] transactional HDM commit/readback/compensation;
- [ ] cache/order policy documented;
- [ ] persistent read path qualified;
- [ ] destination copy hash verified.

## Handoff/runtime

- [ ] BootInfo bounded/versioned/evidence-only;
- [ ] kernel copies and owns BootInfo;
- [ ] fresh liveness/generation validation;
- [ ] new provider generation;
- [ ] aperture result is Released/Stale/Quarantined;
- [ ] ambiguous state => Quarantined/ReclaimBlocked;
- [ ] RegionAuthority acts only after normal runtime admission.

## Qualification

- [ ] all mandatory fault scenarios mapped to test IDs;
- [ ] model retirement only after differential proof;
- [ ] external pin changes trigger qualification rerun;
- [ ] ISE claim has direct ISE evidence;
- [ ] hardware claim has direct hardware evidence;
- [ ] `P15-15` deletes only proven-superseded model code.
