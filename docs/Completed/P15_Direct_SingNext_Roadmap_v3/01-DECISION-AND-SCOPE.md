# Decision and Scope

## Architectural decision

Direct SingNext Boot is a SingNextOS-owned path with four logical responsibilities:

1. `HybridCpu.Boot.Contracts` — versioned wire/ABI contracts only. Reuse the existing project first.
2. `SingNext.Boot.Core` — platform-neutral boot policy/state machines/parsing/verification.
3. `SingPlus.Platform.HybridCpu.Boot` — pre-kernel hardware adapter.
4. `SingNext.Boot.Capsule` — minimal executable composition root.

`SingNext.Boot.Capsule` depends on Core and the platform adapter. The platform adapter must never reference the capsule.

## Existing components that remain authoritative

P15 integrates with, rather than replaces:

- `src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs`;
- `CxlAuthorityBridge` and `CxlType3MemoryAuthority`;
- `RegionAuthority` / `OwnedRegion` / `RegionUse`;
- `PlatformAuthorityBridge` and runtime backend-epoch handling;
- `SingPlus.Admission` and existing security-profile infrastructure;
- `RepositoryArchitecturePolicyTests`;
- `PlatformExternalGateTable` for external platform capability state where its vocabulary already fits.

## In scope

- freeze/reuse existing Boot.Contracts;
- create Core/PlatformAdapter/Capsule production projects;
- boot selection, BootVolume parsing, manifest/hash/signature policy;
- separate capsule and OS image A/B state machines;
- rollback floor, trial nonce/attempts/confirmation;
- local bounded recovery;
- bounded PCIe/CXL Type-3 discovery;
- transactional temporary HDM mapping;
- verified copy to normal RAM;
- allocation-safe BootInfo construction;
- kernel/runtime handoff through the existing importer;
- concrete production fresh-discovery and aperture-retirement adapters;
- current-provider/generation/liveness revalidation;
- runtime admission through existing CXL/Region owners;
- static admission, NativeAOT evidence, ISE/hardware gates.

## Explicit non-goals

No CXL interleave, MLD, multi-host pooling, network recovery, CXL IDE, accelerator boot, confidential compute, or general runtime CXL redesign.

No HybridCPU-v2 source changes are part of P15.

## Single-owner rule

P15 must not create a second authoritative owner for capabilities, Region ownership, provider generations, rollback floor, boot durable state, or external-operation lifecycle.

## First supported profile

- one local signed capsule source;
- one selected BootVolume;
- one temporary single-target aperture;
- no interleave/MLD/multi-host;
- early DMA denied by default;
- ambiguous external state => fail closed.
