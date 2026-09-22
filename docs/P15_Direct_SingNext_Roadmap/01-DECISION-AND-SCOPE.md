# P15.0 — Decision, scope and architectural invariants

## Decision

Adopt **Direct SingNext Boot** as the native HybridCPU boot implementation target.

The immutable ROM MUST remain small and MUST NOT contain a general CXL stack. The ROM verifies and enters a signed, updateable local `SingNext.Boot.Capsule`. The capsule is SingNextOS code and owns the pre-kernel PCIe/CXL boot path. The kernel then re-establishes runtime platform/CXL authority rather than inheriting boot mappings as capabilities.

## Required authority boundaries

### ROM boundary

ROM authority is limited to:

- reset/bootstrap state;
- root-key / trust anchor use;
- protected boot-state reads and atomic updates needed for capsule selection;
- capsule A/B/recovery selection;
- capsule signature/generation verification;
- copy to boot RAM;
- jump to capsule;
- watchdog/recovery dispatch.

ROM MUST NOT know CXL DVSEC, mailbox opcodes, LSA, HDM decoder layouts, switch routes, MLD or fabric pooling.

### Boot Core boundary

`SingNext.Boot.Core` owns policy and parsing, but no physical authority. It may evaluate descriptors and produce decisions; it MUST NOT directly perform MMIO, PCI config, decoder programming, DMA or protected-store access.

### Boot Capsule boundary

`SingNext.Boot.Capsule` is a minimal composition root. It binds Boot Core to narrow platform services supplied by `SingPlus.Platform.HybridCpu.Boot`. It may create temporary boot mappings, but MUST NOT mint `OwnedRegion`, `RegionUse`, runtime device leases or runtime provider generations.

### Kernel/runtime boundary

`HybridBootInfo` is evidence-only. Kernel/runtime performs fresh liveness/generation validation and creates new runtime authority. Boot physical identifiers and temporary mapping descriptors are never imported as rights.

## Non-negotiable invariants

1. `BootVolumeId` is logical boot identity, not memory authority.
2. BDF/DSN/route/HPA/DPA/decoder indices are physical evidence only.
3. `BootCapsuleGeneration`, `ImageGeneration`, boot mapping generation, provider generation and Region generation are distinct domains.
4. Executable bytes are copied to normal RAM and hashed there before entry.
5. Temporary boot aperture is `Temporary | MustNotUseAsRam` and is retired, invalidated or quarantined after handoff.
6. Warm reset may preserve physical state but invalidates prior software authority epochs.
7. Generic PCI bus mastering remains disabled before an explicit early DMA-isolation decision.
8. Protected rollback state is not stored only on writable CXL boot media.
9. Trial boot confirmation is performed by running SingNextOS after a health milestone; kernel entry alone is not confirmation.
10. Local signed recovery remains available independently of CXL.

## Scope of P15

P15 includes:

- project and dependency refactor;
- boot contracts extraction;
- reusable boot logic extraction from current models;
- a real SingNext Boot Capsule project;
- a native HybridCPU boot-platform adapter contract and ISE implementation;
- managed-to-HybridCPU executable artifact production for capsule and kernel;
- kernel physical entry ABI and `HybridBootInfo` wiring;
- executable CXL Type-3 single-target boot aperture;
- fresh runtime CXL takeover;
- protected boot state / A-B / anti-rollback / recovery;
- differential and end-to-end qualification.

P15 does not require CXL interleave, MLD, multi-host pooling, network recovery, CXL IDE, full fabric-manager boot, or accelerator boot.
