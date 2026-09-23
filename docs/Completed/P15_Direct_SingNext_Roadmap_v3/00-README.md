# P15 Direct SingNext Boot — Code-Grounded Implementation Roadmap v3

**Status:** implementation plan, not implementation evidence.

This package supersedes the prior P15 roadmap text for implementation planning. It is grounded in the current SingNextOS `master` observed for this update:

- SingNextOS master: `ebf16c0823c2c0c8a617778945c99ba34f3ca727` (`upd2 direct singnext`).
- HybridCPU-v2 current master observed: `794c4a53494f503855ac8cf209efab23fde083b2`.
- SingNextOS HybridCPU qualification pin still present in `tools/SingPlus.HybridCpuQualification/QualificationRecorder.cs` and `eng/qualify-hybridcpu-aot.sh`: `9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9`.
- `CompilerContract.Version` remains `6` on current HybridCPU-v2 master, but SHA drift means the old qualification cannot be promoted to the new baseline without rerunning qualification.

These SHAs are audit metadata only. Every implementation PR must revalidate the then-current `master` and must not silently rewrite external pins.

## Evidence doctrine

```text
documentation claim
    MUST be verified against
current source code + project graph + tests + qualification tooling.

"Completed" directory != production readiness.
ModelValidated != AdapterQualified != IseValidated != HardwareValidated.
```

## Corrected current-state facts that materially change sequencing

1. `HybridCpu.Boot.Contracts` already exists as `tools/HybridCpu_ExecutableAdapter/Boot.Contracts/HybridCpu.Boot.Contracts.csproj` and is already in `SingNextOS.slnx`. P15 must reuse/freeze it first; moving it is optional mechanical cleanup, not a prerequisite.
2. `HybridBootInfoCodec` exists in that project. `HybridBootInfoImporter`, `IFreshCxlBootDiscovery`, and `IFirmwareApertureRetirement` already exist in production source at `src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs`.
3. Concrete implementations of `IFreshCxlBootDiscovery` and `IFirmwareApertureRetirement` were found only in `tests/SingPlus.Tests/Runtime/HybridBootInfoImporterTests.cs`. Production wiring remains a real blocker.
4. `CxlAuthorityBridge`, `CxlType3MemoryAuthority`, `RegionAuthority`, `OwnedRegion`/`RegionUse`, and runtime backend reset handling already exist. P15 must integrate with these owners, not recreate them.
5. `ObservePlatformBackendReset()` exists in `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBackendReset.cs`; architectural CPU reset-to-ROM is a different domain.
6. `src/Kernel/Boot/SingPlus.Boot` is an existing **host-debug** executable with `ManagedGc` and direct references to kernel Host HAL and runtime. It is not the Direct SingNext capsule and must not be repurposed as the security boundary.
7. `RepositoryArchitecturePolicyTests` already has `BootContracts`; P15 must extend the current classification policy only for genuinely new production layers instead of adding a duplicate BootContracts concept.
8. Existing `HybridBootInfoCodec.Encode` is allocation-heavy and therefore cannot be assumed usable inside a no-heap capsule. P15 needs an allocation strategy and, if no-heap is selected, a bounded writer/Span path rather than copying tooling/runtime codec usage into the capsule.

## Non-negotiable invariants

```text
boot evidence != runtime authority
physical mapping != memory ownership
BootVolumeId != capability
BDF / DSN / HPA / DPA / route / decoder index != authority
same numerical HDM mapping != authority continuity
warm-reset physical survival != generation survival
compiler metadata != runtime authority
HybridCPU/provider receipt != SingNext capability
completion != visibility != publication != release

BootCapsuleGeneration
    != ImageGeneration
    != BootMappingGeneration
    != ProviderGeneration
    != RegionGeneration
```

No P15 code may mint `OwnedRegion`, `RegionUse`, capability authority, or a provider generation from BootInfo or physical identity alone.

## Target execution chain

```text
HybridCPU architectural reset
  -> immutable ROM
  -> signed local SingNext Boot Capsule
  -> SingNext.Boot.Capsule
  -> SingNext.Boot.Core
  -> SingPlus.Platform.HybridCpu.Boot
  -> bounded PCIe/CXL Type-3 discovery
  -> BootVolume selection
  -> temporary single-target HDM aperture
  -> verified copy to normal RAM
  -> HybridBootInfo evidence
  -> kernel entry
  -> existing HybridBootInfoImporter
  -> production fresh discovery + current provider revalidation
  -> existing runtime provider/authority owners
  -> aperture Released/Stale/Quarantined
  -> RegionAuthority / OwnedRegion / RegionUse only after normal admission
```

## Document map

The package is intentionally split by ownership and evidence level. `21-V3-DELTA.md` summarizes corrections from the prior roadmap revision.

## Roadmap-use rule

The roadmap is usable as an implementation plan. It is never implementation evidence. `P15-00` remains the first implementation slice and must regenerate the baseline artifact before code movement begins.
