# Current-State Baseline and P15-00 Revalidation

Observed SingNextOS master before the follow-up audit changes: `1e767da4e649832fe018f4bc365da9be824d1951`.

## Code-grounded matrix

| Area | Current source evidence | Status | P15 action |
|---|---|---|---|
| Boot.Contracts | `tools/HybridCpu_ExecutableAdapter/Boot.Contracts/HybridCpu.Boot.Contracts.csproj`; `HybridBootInfoCodec.cs`, `BootPolicyCodec.cs` | `ContractOnly` / executable contract dependency | Reuse in place; freeze surface; do not duplicate |
| Boot.Core candidate logic | `src/Kernel/Boot/SingNext.Boot.Core`; retained `tools/HybridCpu_ExecutableAdapter/Boot/*Model.cs` oracle | `ModelValidated`; local production semantics tested | Keep oracle until broader differential proof and qualification exist |
| Boot Capsule | `src/Kernel/Boot/SingNext.Boot.Capsule`; existing `src/Kernel/Boot/SingPlus.Boot` remains host-debug `ManagedGc` | project present; real root not admitted; entry returns `Unsupported` | Do not report success until entry/bootstrap/admission gates are satisfied |
| HybridPlatformDescriptor | no verified authoritative production equivalent established | `Absent/Unverified` | Add only if P15-00 confirms no equivalent |
| HybridBootInfo codec | `tools/.../Boot.Contracts/HybridBootInfoCodec.cs` | `ContractOnly` | Reuse wire format; do not assume allocation profile is capsule-safe |
| HybridBootInfo importer | `src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs` | `RuntimeImplemented`, locally `AdapterQualified` by existing evidence | Reuse; wire into real handoff |
| Fresh discovery interface | interface in importer; `ProviderFreshCxlBootDiscovery` in `HybridBootProductionAdapters.cs` | local adapter implemented | Physical platform enumeration/composition remains external-gated |
| Aperture retirement interface | interface in importer; `PlatformFirmwareApertureRetirement` in `HybridBootProductionAdapters.cs` | local adapter implemented | Exact-owner physical retirement proof remains external-gated |
| Runtime CXL authority | `CxlAuthorityBridge.cs`, `CxlType3MemoryAuthority.cs` | `RuntimeImplemented` | Remains sole CXL authority path |
| Region ownership | `Regions/RegionAuthority.cs` | `RuntimeImplemented` | Remains sole ownership authority |
| Runtime reset epochs | `RuntimeKernel.PlatformBackendReset.cs::ObservePlatformBackendReset()` | `RuntimeImplemented` | Reuse for runtime backend lifecycle only |
| Kernel entry | `src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs` | `ProductionPath` for current host/kernel path | Extend via narrow entry adapter; preserve admission root semantics |
| Existing SingPlus.Boot | `src/Kernel/Boot/SingPlus.Boot/*.cs`, references Runtime + Host HAL, `ManagedGc` | `ProductionPath` host-debug only | Keep separate from capsule |
| PCI/CXL boot transport | `src/Platform/SingPlus.Platform.HybridCpu.Boot` plus retained models | local deterministic adapter semantics only | Real PCI/CXL transport remains `ExternalBlocked` |
| Temporary HDM | production state machine plus retained `TemporaryApertureModel.cs` | local deterministic semantics | Physical decoder/coherence proof remains external-gated |
| Protected state | Core durable protocol port plus retained model | protocol locally tested | Power-loss-safe store implementation remains `ExternalBlocked` |
| A/B and recovery | Core state/recovery policies plus retained models | local deterministic semantics | Retain oracles; no ISE/hardware promotion |
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
