# P15 Traceability Matrix

| Requirement | Authoritative owner | PR | Primary evidence/tests | Maximum pre-ISE claim |
|---|---|---|---|---|
| BootInfo wire ABI | existing `HybridCpu.Boot.Contracts` | 01,09,10 | existing codec vectors + bounded-writer equivalence | `AdapterQualified` |
| project/dependency policy | `RepositoryArchitecturePolicyTests` | 01 | positive + forbidden-edge fixtures | `ContractOnly` |
| Core selector/loader | `SingNext.Boot.Core` | 02,03 | property + model differential | `ModelValidated` |
| protected state/A-B/rollback | Core + `IBootProtectedState` | 04 | torn write/split brain/rollback/trials | `ModelValidated` |
| early DMA isolation | BootPlatformAdapter | 05 | pre-IOMMU DMA negative path | `AdapterQualified` |
| PCI/CXL discovery | BootPlatformAdapter | 06 | loop/DVSEC/timeout/link/duplicate/permutation | `AdapterQualified` |
| temporary HDM | Core FSM + BootPlatformAdapter | 07 | partial commit/readback/reset/compensation | `AdapterQualified` |
| capsule admission/memory profile | `SingPlus.Admission` + Capsule | 08 | negative fixtures + closure proof | `AdapterQualified` for static contour |
| capsule composition | Capsule | 09 | deterministic adapter E2E | `AdapterQualified` |
| kernel/runtime BootInfo import | existing `HybridBootInfoImporter` | 10 | malformed/evidence classification/alias/range | `AdapterQualified` |
| fresh endpoint enumeration | production `IFreshCxlBootDiscovery` adapter + existing provider | 10,11 | endpoint disappear/stale/refusal | `AdapterQualified` |
| aperture retirement | production `IFirmwareApertureRetirement` adapter + existing teardown owner | 10,12 | ambiguous release/quarantine | `AdapterQualified` |
| runtime authority | existing `CxlAuthorityBridge`/`RegionAuthority` | 11 | no BootInfo->authority; current generation checks | `AdapterQualified` |
| runtime backend epoch | existing `ObservePlatformBackendReset` owner | 12 | late old-epoch completion/reclaim block | `AdapterQualified` |
| architectural reset | external ROM/reset contract | 13 | ISE reset trace | `IseValidated` only with gate |
| HybridCPU baseline/pin | existing qualification owner | 13 | clean exact SHA + reproducible artifact | evidence-dependent |
| hardware CXL/DMA/order/protected state | named hardware profile | 14 | direct traces/faults/power-loss tests | `HardwareValidated` only with evidence |

Every removed model must link to replacement production code and differential test IDs.
