# P15.10 — Exit criteria

## P15 program-level exit criteria

P15 is complete only when all mandatory criteria below are met for the selected profile.

### Architecture/dependency

- `HybridCpu.Boot.Contracts` is independent and no longer nested under executable-adapter implementation.
- `SingNext.Boot.Core` has no platform/runtime authority dependency.
- `SingNext.Boot.Capsule` has no `SingPlus.Runtime` or host HAL dependency.
- `SingPlus.Platform.HybridCpu.Boot` has no Region/Capability/SIP/runtime authority dependency.
- architecture-policy tests encode these edges.

### Artifact production

- capsule and kernel are reproducibly emitted as HybridCPU executable artifacts;
- two clean builds are byte-identical or produce an explicitly documented deterministic-equivalent format;
- exact compiler/ABI/platform digests are recorded;
- stale `ManagedAssemblyToHybridCpuAot = ExternalBlocked` is removed for the qualified profile.

### Boot path

- ROM/ISE profile verifies and enters local capsule;
- capsule discovers one Type-3 endpoint using bounded PCI/CXL logic;
- capsule locates a logical BootVolume independent of enumeration order;
- capsule programs one temporary non-interleaved HDM aperture;
- kernel/services are copied to normal RAM and destination-hashed;
- kernel is entered using frozen `KernelEntryAbiV1`;
- BootInfo contains evidence only.

### Runtime takeover

- kernel fresh-admits/revalidates CXL;
- firmware/boot mapping is released, invalidated, stale, or quarantined;
- no BootInfo field can mint device/Region authority;
- new runtime generations are nonzero and independent of boot generations;
- normal CXL Type-3 use requires the existing Region/CXL authority path.

### Update/recovery

- signed capsule A/B or equivalent rollback-safe local update exists;
- OS image trial/confirm path exists;
- rollback floor advances only after explicit confirmation;
- reset/power-loss tests at every durable barrier leave a confirmed or recovery route;
- local recovery works with CXL unavailable.

### Security

- capsule admission profile rejects forbidden APIs and ambient dependencies;
- production signature verifier passes known-answer and malformed-input tests;
- protected state cannot be replaced by writable CXL metadata;
- generic bus mastering is disabled until an explicit DMA-isolation profile admits it.

### Qualification

- complete ISE positive scenario passes;
- mandatory negative matrix passes;
- evidence report is generated from actual commands, not manually asserted status;
- hardware-only claims remain unclaimed until real hardware lane passes.

## Criteria for deleting old model code

A current `HybridCpu_ExecutableAdapter/Boot/*Model.cs` file may be deleted only when:

1. its semantic rules live in Boot Core or contracts;
2. old tests were migrated or replaced;
3. a differential test demonstrated equivalence for unchanged semantics;
4. remaining model-only backend behavior has a new explicit test fixture location;
5. documentation and traceability no longer cite the old file as production evidence.
