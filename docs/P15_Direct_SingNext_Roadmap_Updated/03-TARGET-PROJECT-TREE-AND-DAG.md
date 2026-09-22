# Target Project Tree and Correct Dependency DAG

## Proposed project structure

```text
contracts/
  HybridCpu.Boot.Contracts/
    HybridCpu.Boot.Contracts.csproj

src/Boot/
  SingNext.Boot.Core/
    SingNext.Boot.Core.csproj
  SingNext.Boot.Capsule/
    SingNext.Boot.Capsule.csproj

src/Platform/
  SingPlus.Platform.HybridCpu.Boot/
    SingPlus.Platform.HybridCpu.Boot.csproj
```

If repository architecture policy requires boot projects under an existing `src/Kernel/Boot` grouping, `P15-00` may adjust physical paths, but ownership and dependency rules below are normative.

## Correct project-reference direction

Notation: `A -> B` means **A references B**.

```text
SingNext.Boot.Core
    -> HybridCpu.Boot.Contracts

SingPlus.Platform.HybridCpu.Boot
    -> SingNext.Boot.Core
    -> HybridCpu.Boot.Contracts

SingNext.Boot.Capsule
    -> SingNext.Boot.Core
    -> SingPlus.Platform.HybridCpu.Boot
    -> HybridCpu.Boot.Contracts

Kernel boot importer / entry adapter
    -> HybridCpu.Boot.Contracts
    -> existing kernel/runtime/platform abstractions already allowed by policy
```

The capsule is the composition root. Therefore the platform adapter MUST NOT reference the capsule.

## Wire contracts vs in-process ports

`HybridCpu.Boot.Contracts` contains versioned wire/ABI shapes only.

In-process boot service ports belong to `SingNext.Boot.Core` unless `P15-00` finds an existing authoritative abstraction to reuse:

- `IBootPciConfiguration`;
- `IBootCxlTransport`;
- `IBootTemporaryMapping`;
- `IBootProtectedState`;
- `IBootRecoverySource`;
- `IBootClock`;
- `IBootResetControl`;
- `IBootDmaIsolation`.

This prevents `Boot.Contracts` from becoming a general dependency bag.

## Forbidden dependency edges

```text
HybridCpu.Boot.Contracts -> SingPlus.Runtime
HybridCpu.Boot.Contracts -> RegionAuthority / CapabilityAuthority implementation
HybridCpu.Boot.Contracts -> HybridCPU implementation assemblies
HybridCpu.Boot.Contracts -> Host implementation
HybridCpu.Boot.Contracts -> HybridCpu_ExecutableAdapter

SingNext.Boot.Core -> HybridCpu_ExecutableAdapter
SingNext.Boot.Core -> Host implementation
SingNext.Boot.Core -> HybridCPU compiler/ISE internals
SingNext.Boot.Core -> runtime capability authority

SingNext.Boot.Capsule -> SingPlus.Runtime
SingNext.Boot.Capsule -> RegionAuthority / capability minting shortcut

kernel/runtime -> raw HybridCPU compiler/static-linker/ISE internals
SingPlus.Platform.HybridCpu.Boot -> SingNext.Boot.Capsule
production projects -> adapter *Model types
```

## RepositoryArchitecturePolicyTests changes

Add classifications for:

- `BootContracts`: contract-only;
- `BootCore`: platform-neutral restricted production;
- `BootCapsule`: executable restricted composition root;
- `BootPlatformAdapter`: pre-kernel hardware-specific adapter.

The policy must enforce:

- allowed project references;
- forbidden project references;
- forbidden package references;
- no adapter-model dependencies from production code;
- no direct HybridCPU implementation dependency.

## Package policy

`HybridCpu.Boot.Contracts` should normally have no external packages.

`SingNext.Boot.Core` must remain deterministic and dependency-minimal. Package additions require explicit architecture/security review and lock-file updates.

The capsule's transitive closure is subject to the BootCapsule static-admission profile.
