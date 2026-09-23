# Current-State Baseline and P15-00 Revalidation

Observed SingNextOS master for this roadmap update: `ebf16c0823c2c0c8a617778945c99ba34f3ca727`.

## Code-grounded matrix

| Area | Current source evidence | Status | P15 action |
|---|---|---|---|
| Boot.Contracts | `tools/HybridCpu_ExecutableAdapter/Boot.Contracts/HybridCpu.Boot.Contracts.csproj`; `HybridBootInfoCodec.cs`, `BootPolicyCodec.cs` | `ContractOnly` / executable contract dependency | Reuse in place; freeze surface; do not duplicate |
| Boot.Core candidate logic | `tools/HybridCpu_ExecutableAdapter/Boot/*Model.cs` | `ModelOnly` | Rewrite deterministic semantics behind Core ports; retain oracles |
| Boot Capsule | no dedicated Direct-SingNext capsule project found; existing `src/Kernel/Boot/SingPlus.Boot` is host-debug `ManagedGc` | `Absent` for target capsule | Create new project; do not repurpose host-debug boot |
| HybridPlatformDescriptor | no verified authoritative production equivalent established | `Absent/Unverified` | Add only if P15-00 confirms no equivalent |
| HybridBootInfo codec | `tools/.../Boot.Contracts/HybridBootInfoCodec.cs` | `ContractOnly` | Reuse wire format; do not assume allocation profile is capsule-safe |
| HybridBootInfo importer | `src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs` | `RuntimeImplemented`, locally `AdapterQualified` by existing evidence | Reuse; wire into real handoff |
| Fresh discovery interface | same production file | `RuntimeImplemented` interface | Provide concrete production implementation; only tests currently implement it |
| Aperture retirement interface | same production file | `RuntimeImplemented` interface | Provide concrete production implementation; only tests currently implement it |
| Runtime CXL authority | `CxlAuthorityBridge.cs`, `CxlType3MemoryAuthority.cs` | `RuntimeImplemented` | Remains sole CXL authority path |
| Region ownership | `Regions/RegionAuthority.cs` | `RuntimeImplemented` | Remains sole ownership authority |
| Runtime reset epochs | `RuntimeKernel.PlatformBackendReset.cs::ObservePlatformBackendReset()` | `RuntimeImplemented` | Reuse for runtime backend lifecycle only |
| Kernel entry | `src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs` | `ProductionPath` for current host/kernel path | Extend via narrow entry adapter; preserve admission root semantics |
| Existing SingPlus.Boot | `src/Kernel/Boot/SingPlus.Boot/*.cs`, references Runtime + Host HAL, `ManagedGc` | `ProductionPath` host-debug only | Keep separate from capsule |
| PCI/CXL boot transport | adapter models and runtime providers exist; no SingNext-owned pre-kernel hardware project | `ModelOnly`/partial runtime | Create boot-specific platform adapter |
| Temporary HDM | `TemporaryApertureModel.cs` | `ModelOnly` | Rewrite state machine + hardware executor |
| Protected state | `TrustAndProtectedStateModel.cs` | `ModelOnly` | Core policy + protected-store port; hardware gated |
| A/B and recovery | `AbRecoveryModel.cs`, `Stage0RecoveryModel.cs`, `Stage1LoadModel.cs` | `ModelOnly` | Differential rewrite |
| Static admission | `SingPlus.Admission`, `KernelNoHeap`, `ManagedCap` | `Runtime/ToolingImplemented` | Extend existing verifier/profile mechanism; no second analyzer |
| Architecture policy | `RepositoryArchitecturePolicyTests` includes `BootContracts` layer | `Runtime/TestImplemented` | Reuse existing `BootContracts`; add only missing new production layers |
| External gate table | `PlatformFeatureContracts.cs::PlatformExternalGateTable` | `RuntimeImplemented` | Map P15 gates to existing requirements when possible |
| HybridCPU qualification | pin `9e001bf...`, contract version `6` | `RepositoryDrift` vs HybridCPU master `794c4a...` | Requalify; never just replace SHA |

## P15-00 mandatory outputs

`DirectSingNextBootBaselineV2.json` must contain:

- current SingNextOS SHA;
- current HybridCPU observed SHA and qualified SHA separately;
- compiler/runtime contract version;
- exact project graph and architecture classification;
- exact Boot.Contracts public surface hash;
- definitions and consumers for every boot/CXL/authority object;
- concrete implementation status for `IFreshCxlBootDiscovery` and `IFirmwareApertureRetirement`;
- current security-profile inventory entries;
- external gate mapping to `PlatformExternalGateTable`;
- repository drift list.

A later PR may not rely on an `Absent`, `ExternalBlocked`, or `FeatureGated` capability without a fail-closed gate.
