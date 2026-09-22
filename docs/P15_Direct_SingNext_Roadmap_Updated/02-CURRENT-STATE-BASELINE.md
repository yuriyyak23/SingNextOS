# Current-State Baseline and P15-00 Revalidation

The completed audit established that roadmap text and implementation state had drifted in several areas. It also identified places where negative claims needed stronger source proof. Therefore `P15-00` is a mandatory repository-revalidation slice, not optional documentation work.

## Audit-grounded baseline

| Area | Audit-time status | P15 assumption | Required action |
|---|---|---|---|
| Boot.Contracts | existing adapter-side boot contracts are present | production wire contracts should become SingNext-owned | inventory definitions/consumers before move |
| Boot.Core candidate logic | semantics are split across boot code and adapter models | new platform-neutral Core is required | extract/rewrite only after source-level ownership map |
| Boot Capsule | separate production composition-root project not established | create minimal executable root | add only after policy/DAG gate |
| HybridPlatformDescriptor | roadmap-proposed; exact existing equivalent must be checked | evidence descriptor may be required | reuse/extend existing authoritative contract first |
| HybridCPU artifact generation | qualification/tooling exists, exact supported image path must be verified | capsule must become reset-loadable | external gate until behavior is proven |
| PCI/CXL discovery | model/executable-adapter evidence exists; production boot ownership incomplete | SingNext-owned pre-kernel adapter required | rewrite behind boot ports |
| HDM aperture | model semantics exist | executable transactional mapping required | split state machine from hardware executor |
| protected state | model semantics exist | durable protected-store backend required | port + gated implementation |
| A/B | policy/model material exists | separate capsule/image generation domains required | production state machine + torn-write tests |
| recovery | Stage0/Stage1/AB models exist | bounded local recovery required | retain models as oracle |
| BootInfo | definitions/codec/importer exist or are expected by current tree; exact ownership must be rechecked | evidence-only handoff | reuse authoritative codec/type; no duplicate contract |
| kernel entry | existing kernel entry path exists | add/extend versioned entry ABI | adapt existing owner |
| fresh discovery | runtime provider hooks exist or are close; exact API must be inventoried | mandatory revalidation | extend existing runtime discovery/provider path |
| aperture retirement | owner not sufficiently explicit in old roadmap | explicit retirement result required | locate owner; add narrow interface only if absent |
| runtime CXL | existing runtime providers/bridges are authoritative | boot path must not own runtime authority | integrate after handoff only |
| RegionAuthority | do **not** assume absent | existing authority owner must remain authoritative | locate definitions/consumers and reuse |
| reset epochs | do **not** assume absent | reuse current BackendEpoch/generation logic | locate `ObservePlatformBackendReset()` or equivalent |
| qualification | Admission and HybridCPU qualification tooling exist | P15-specific lane required | extend existing infrastructure |
| hardware gates | hardware evidence incomplete by definition until run | explicit gates required | block promotion above available evidence |

## P15-00 mandatory inventory

Inventory at minimum:

```text
contracts/
src/Kernel/
src/Kernel/Boot/
src/Kernel/Hal/
src/Runtime/
src/Platform/
src/Platform/SingPlus.Platform.Abstractions/
src/Platform/SingPlus.Platform.HybridCpu/
src/Platform/SingPlus.Platform.Host/
tools/HybridCpu_ExecutableAdapter/
tools/HybridCpu_ExecutableAdapter/Boot/
tools/HybridCpu_ExecutableAdapter/Boot.Contracts/
tools/SingPlus.Admission/
tools/SingPlus.HybridCpuQualification/
eng/
tests/
SingNextOS.slnx
Directory.Build.props
Directory.Build.targets
```

Find real definitions and consumers for:

- `HybridBootInfo`, `HybridBootInfoCodec`, `HybridBootInfoImporter`;
- `BootPolicy`, `BootVolumeHeader`, `BootManifest`;
- all adapter boot models;
- `ICxlDiscoveryProvider`, `ICxlMemoryProvider`, `ICxlFabricProvider`, `ICxlIoProvider`, `ICxlSecurityEvidenceProvider`;
- `CxlAuthorityBridge`, `CxlType3MemoryAuthority`, placement/teardown code;
- `PlatformAuthorityBridge`, backend reset handling;
- `RegionAuthority`, `OwnedRegion`, `RegionUse`;
- `SingPlus.Boot`, `KernelEntryPoint`;
- HybridCPU qualification tooling and NativeAOT/admission profiles.

For each, record exactly one status:

`Absent`, `DocsOnly`, `ContractOnly`, `ModelOnly`, `ExecutableTestOnly`, `RuntimeImplemented`, `AdapterQualified`, `ProductionPath`, `ExternalBlocked`, `FeatureGated`.

## Baseline artifact

`P15-00` emits `DirectSingNextBootBaselineV1.json` with:

- current master SHA;
- project graph;
- exact source paths/types/methods;
- status matrix;
- current external HybridCPU qualified baseline and pins;
- unresolved `RepositoryDrift` items;
- required architecture-policy changes.

Any later slice relying on an `Absent` or `ExternalBlocked` item must carry an explicit feature gate and fail closed.
