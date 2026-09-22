# P15 Traceability Matrix

| Requirement | Authoritative owner | PR | Primary tests | Maximum pre-ISE claim |
|---|---|---|---|---|
| wire ABI / BootInfo | `HybridCpu.Boot.Contracts` | 01,10 | ABI golden vectors, malformed BootInfo | `AdapterQualified` |
| candidate selection / loader | `SingNext.Boot.Core` | 02,03 | oracle differential, bounds/property tests | `ModelValidated` |
| protected state / A-B / rollback | Boot.Core + protected-store port | 04 | torn write, split brain, rollback, trial exhaustion | `ModelValidated` |
| DMA isolation | HybridCPU boot adapter | 05 | pre-IOMMU DMA attempt | `AdapterQualified` |
| PCI/CXL discovery | HybridCPU boot adapter | 06 | capability loop, DVSEC, timeout, link loss | `AdapterQualified` |
| temporary HDM | Core state + platform executor | 07 | partial commit, readback mismatch, reset | `AdapterQualified` |
| capsule composition | Capsule | 08 | executable adapter E2E | `AdapterQualified` |
| static admission / AOT | Admission + Capsule | 09 | negative fixtures, AOT artifact | `AdapterQualified` |
| kernel import | kernel boot path | 10 | malformed/alias/range/version tests | `AdapterQualified` |
| fresh provider admission | existing runtime provider authority | 11 | stale generation, provider refusal | `AdapterQualified` |
| aperture retirement | existing platform/runtime teardown owner | 12 | ambiguous teardown, reclaim block | `AdapterQualified` |
| runtime reset epochs | existing PlatformAuthority owner | 12 | late old-epoch completion | `AdapterQualified` |
| ISE Direct Boot | qualification lane | 13 | full mandatory fault matrix | `IseValidated` |
| hardware Direct Boot | hardware qualification | 14 | exact platform protocol/reset/DMA/order tests | `HardwareValidated` with evidence |

Traceability is bidirectional:

- every security requirement maps to at least one test;
- every claim-bearing test identifies its environment/evidence lane;
- every external gate lists the affected PR and maximum claim;
- every removed model points to replacement production code + differential proof.
