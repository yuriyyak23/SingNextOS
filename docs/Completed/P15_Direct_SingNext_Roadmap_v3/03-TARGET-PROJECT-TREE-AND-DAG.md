# Target Project Tree and Dependency DAG

## Physical-path decision

Do not move the existing Boot.Contracts project merely to make the tree prettier. The logical module already exists at:

```text
tools/HybridCpu_ExecutableAdapter/Boot.Contracts/HybridCpu.Boot.Contracts.csproj
```

P15-01 freezes/reclassifies/reuses it in place. A later path-only relocation to `contracts/HybridCpu.Boot.Contracts/` is optional and must be a separate mechanical slice with no semantic change.

New projects should follow the repository's existing boot/platform grouping:

```text
src/Kernel/Boot/SingNext.Boot.Core/
  SingNext.Boot.Core.csproj

src/Platform/SingPlus.Platform.HybridCpu.Boot/
  SingPlus.Platform.HybridCpu.Boot.csproj

src/Kernel/Boot/SingNext.Boot.Capsule/
  SingNext.Boot.Capsule.csproj
```

The existing `src/Kernel/Boot/SingPlus.Boot` remains host-debug and is not renamed into the capsule.

## Correct reference DAG

Notation: `A -> B` means A has a project reference to B.

```text
SingNext.Boot.Core
  -> HybridCpu.Boot.Contracts

SingPlus.Platform.HybridCpu.Boot
  -> SingNext.Boot.Core
  -> HybridCpu.Boot.Contracts
  -> SingPlus.Platform.Abstractions only when required by an existing neutral contract

SingNext.Boot.Capsule
  -> SingNext.Boot.Core
  -> SingPlus.Platform.HybridCpu.Boot
  -> HybridCpu.Boot.Contracts

SingPlus.Runtime (existing importer)
  -> HybridCpu.Boot.Contracts
  -> existing platform/runtime contracts
```

No edge from Core or Capsule to `SingPlus.Runtime` is allowed.

## RepositoryArchitecturePolicyTests correction

The current policy already contains `Layer.BootContracts`. Do **not** add a duplicate BootContracts classification.

P15 must either:

1. add explicit `BootCore`, `BootCapsule`, and `BootPlatformAdapter` layers before the generic `src/Kernel`/`src/Platform` classification branches; or
2. prove that existing `PrivilegedMechanism`/`ProviderAdapter` rules can enforce all required forbidden edges without weakening unrelated projects.

Preferred: explicit layers, because Capsule dependency closure is materially stricter than ordinary `PrivilegedMechanism`.

## Forbidden edges

```text
BootContracts -> Runtime/RegionAuthority/CapabilityAuthority/Host/ExecutableAdapter
BootCore -> Runtime/Host/HybridCPU implementation/compiler/ISE/ExecutableAdapter models
BootCapsule -> Runtime/RegionAuthority/CapabilityAuthority/Host debug HAL
BootPlatformAdapter -> BootCapsule
kernel/runtime -> raw HybridCPU compiler/ISE internals
production -> adapter *Model types
```

## Package policy

- Boot.Contracts: no new external packages.
- Boot.Core: no package unless explicitly allow-listed and locked.
- BootPlatformAdapter: no HybridCPU implementation package; only stable external semantic contracts if existing architecture policy permits exact pins.
- Capsule: transitive closure must satisfy the capsule admission profile.
