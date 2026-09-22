# P15.1 — Exact proposed project tree

## Target repository tree

```text
contracts/
└── HybridCpu.Boot.Contracts/
    ├── HybridCpu.Boot.Contracts.csproj
    ├── BootAbiV1.cs
    ├── BootContracts.cs
    ├── BootWire.cs
    ├── BootPolicyCodec.cs
    ├── BootVolumeHeaderCodec.cs
    ├── BootManifestCodec.cs
    ├── HybridBootInfoCodec.cs
    ├── BootEvidencePayloadCodec.cs
    ├── HybridPlatformDescriptorV1.cs
    └── KernelEntryAbiV1.cs

src/Kernel/Boot/
├── SingNext.Boot.Core/
│   ├── SingNext.Boot.Core.csproj
│   ├── Selection/
│   │   ├── BootCandidate.cs
│   │   ├── BootSelectionPolicy.cs
│   │   ├── BootSelector.cs
│   │   └── SplitBrainRules.cs
│   ├── Trust/
│   │   ├── BootTrustEvaluator.cs
│   │   ├── RollbackPolicy.cs
│   │   └── BootConfirmationRules.cs
│   ├── Volume/
│   │   ├── BootVolumeReader.cs
│   │   ├── BootLocatorParser.cs
│   │   └── ManifestReader.cs
│   ├── Loading/
│   │   ├── VerifiedComponentLoader.cs
│   │   ├── BootLoadPlan.cs
│   │   └── DestinationHashVerifier.cs
│   ├── Recovery/
│   │   ├── AbBootStateMachine.cs
│   │   └── RecoverySelector.cs
│   ├── Cxl/
│   │   ├── BootCxlDiscoveryRules.cs
│   │   ├── TemporaryApertureCoordinator.cs
│   │   └── BootCxlEvidenceBuilder.cs
│   └── Handoff/
│       ├── HybridBootInfoBuilder.cs
│       └── KernelEntryPlan.cs
│
├── SingNext.Boot.Capsule/
│   ├── SingNext.Boot.Capsule.csproj
│   ├── CapsuleEntryPoint.cs
│   ├── CapsuleComposition.cs
│   ├── CapsuleBootPipeline.cs
│   ├── CapsuleFailure.cs
│   ├── CapsuleScratch.cs
│   └── README.md
│
└── SingPlus.Boot/
    └── ... existing host-debug profile retained, then optionally renamed/moved

src/Platform/
├── SingPlus.Platform.Abstractions/
│   └── ... runtime provider contracts only
│
├── SingPlus.Platform.HybridCpu/
│   └── ... existing runtime platform authority provider
│
└── SingPlus.Platform.HybridCpu.Boot/
    ├── SingPlus.Platform.HybridCpu.Boot.csproj
    ├── HybridCpuBootPlatform.cs
    ├── HybridCpuPlatformDescriptorSource.cs
    ├── Reset/
    │   ├── HybridCpuResetReasonSource.cs
    │   └── HybridCpuBootEpochSource.cs
    ├── Pci/
    │   ├── HybridCpuBootPciConfig.cs
    │   └── BoundedPciEnumerator.cs
    ├── Cxl/
    │   ├── HybridCpuBootCxlTransport.cs
    │   ├── HybridCpuBootCxlDiscovery.cs
    │   ├── HybridCpuBootPersistentCapacity.cs
    │   └── HybridCpuBootTemporaryDecoder.cs
    ├── Trust/
    │   ├── HybridCpuBootSignatureVerifier.cs
    │   ├── HybridCpuProtectedBootStore.cs
    │   └── HybridCpuMonotonicCounter.cs
    ├── Recovery/
    │   ├── HybridCpuLocalCapsuleStore.cs
    │   └── HybridCpuLocalRecoverySource.cs
    ├── Isolation/
    │   ├── HybridCpuEarlyDmaPolicy.cs
    │   └── HybridCpuBootIommu.cs
    └── Diagnostics/
        └── HybridCpuBootDebugSink.cs

tests/
├── SingNext.Boot.Core.Tests/
├── SingNext.Boot.Capsule.Tests/
├── SingPlus.Platform.HybridCpu.Boot.Tests/
└── SingPlus.Tests/
    └── Runtime/
        └── HybridBootInfoImporterTests.cs

tools/
├── HybridCpu_ExecutableAdapter/
│   └── Boot/                    # reduced to test/model adapters during transition, then removed
├── SingPlus.HybridCpuQualification/
└── SingPlus.Boot.DebugHost/

eng/
├── qualify-direct-singnext-boot.ps1
├── qualify-direct-singnext-boot.sh
├── qualify-hybridcpu-aot.sh     # updated/replaced; no stale pinned revision
└── singcap-security-profiles-v1.json
```

## Project responsibilities

### `HybridCpu.Boot.Contracts`

Dependency-free wire/ABI package. No runtime, provider, model or tool dependency. Both SingNextOS and HybridCPU-side loaders may consume it.

### `SingNext.Boot.Core`

Pure deterministic boot semantics. No host IO and no direct platform access. It references only `HybridCpu.Boot.Contracts` plus tightly reviewed BCL primitives. All algorithmic decisions from current boot models move here.

### `SingNext.Boot.Capsule`

Executable composition root. References `SingNext.Boot.Core`, `HybridCpu.Boot.Contracts`, and `SingPlus.Platform.HybridCpu.Boot`. It is the artifact compiled into HybridCPU executable form.

### `SingPlus.Platform.HybridCpu.Boot`

Pre-kernel hardware/platform adapter. It is intentionally separate from existing `SingPlus.Platform.HybridCpu` runtime authority provider. Boot code exposes temporary mechanisms, not runtime authority leases.

## Dependency direction

```text
HybridCpu.Boot.Contracts
        ↑
SingNext.Boot.Core
        ↑
SingNext.Boot.Capsule  ->  SingPlus.Platform.HybridCpu.Boot

Kernel/Runtime -> HybridCpu.Boot.Contracts
Kernel/Runtime -> SingPlus.Platform.Abstractions
Runtime        -> SingPlus.Platform.HybridCpu   (runtime profile)
```

Forbidden edges:

```text
HybridCpu.Boot.Contracts -> anything
SingNext.Boot.Core -> SingPlus.Runtime
SingNext.Boot.Core -> SingPlus.Platform.HybridCpu.Boot
SingNext.Boot.Capsule -> SingPlus.Runtime
SingNext.Boot.Capsule -> HybridCpu_ExecutableAdapter
Kernel runtime -> SingPlus.Platform.HybridCpu.Boot
Boot adapter -> RegionAuthority / CapabilityAuthority
```
