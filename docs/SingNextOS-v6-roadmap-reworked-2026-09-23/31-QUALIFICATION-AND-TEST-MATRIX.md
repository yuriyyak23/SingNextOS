# Corrected Qualification and Test Matrix

Legend: `R` required for phase exit, `H` required only for hardware/production promotion, `-` not normally required.

| Phase | Unit | Property | Negative | Race | Fault injection | Differential trace | Provider conformance | Model checking | Perf | Hardware | Supply-chain/artifact | Initial claim ceiling |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| P01 memory | R | R | R | R | R | R | R | R | R | H | R | ExecutableAdapter staged contour |
| P02 durability | R | R | R | R | R | R | R | R | R | H | R | ExecutableAdapter named provider |
| P03 temporal | R | R | R | R | R | R | R | R for stronger guarantees | R | H for hard RT | R | RuntimeEnforced accounting |
| P04 DMA | R | R | R | R | R | R | R | R | R | H | R | ExecutableAdapter; no physical isolation claim |
| P05 refinement | R | R | R | R | R | R | R | R | R | - | R | RuntimeEnforced checker + ModelOnly model |
| P06 IFC | R | R | R | R | R | R | R | R | R | H for production secure contour | R | ModelOnly/StaticAdmission initially |
| P07 locality | R | R | R | R | R | - | R | - | R | H for topology/perf production claim | R | advisory/performance only |
| P08 preemption | R | R | R | R | R | R | R | R | R | H for device safe-point claim | R | ExecutableAdapter per provider |
| P09 trust | R | R | R | R | R | R | R | R | R | H | R | StaticAdmission/ExecutableAdapter |
| P10 RAS | R | R | R | R | R | R | R | R | R | H | R | ModelOnly/ExecutableAdapter fault model |
| P11 multi-host | R | R | R | R | R | R | R | R | R | H | R | FutureGated |
| P12 energy | R | R | R | R | R | - | R | - | R | H for enforced/attributed claim | R | measurement/upper-bound only |
| PCL | R | R | R | R | R | R | - | property/exploration | R | - | R | StaticAdmission + optimization evidence |

## Mandatory promotion evidence

### ModelOnly -> StaticAdmission
- canonical parser/validator tests;
- schema/version/digest negative tests;
- unknown mandatory semantics fail closed.

### StaticAdmission -> RuntimeEnforced
- live owner/runtime transition code;
- race tests against generation changes;
- negative tests proving the runtime refuses invalid/stale state;
- no fake-provider-only proof.

### RuntimeEnforced -> ExecutableAdapter
- concrete adapter/provider executes the contour;
- provider generations and failure states are surfaced;
- end-to-end trace includes submit/effect/completion/visibility/publication as applicable;
- provider conformance suite passes.

### ExecutableAdapter -> EnforcedUpperBound / GuaranteedReservation
- independently observed enforcement under contention/fault/replay;
- resource conservation and settlement proof/tests;
- no inference from reservation or telemetry alone.

### Any level -> ProductionQualified
- named hardware/firmware/provider tuple;
- physical ordering/DMA/persistence/trust/RAS tests relevant to the claim;
- security review and fault campaign;
- performance characterization and limits;
- signed/reproducible artifacts, dependency/SBOM/package digests;
- operational recovery/rollback playbook.

## Required cross-cutting race set

Every phase that consumes mutable owner state must inject at least:

```text
capability revoke
session close
invocation close
Region generation/ABA
budget lease change
provider restart/generation drift
device reset/reconfiguration
translation invalidation
duplicate evidence/receipt
cancel vs retire/completion
visibility vs publication
settlement vs reclaim
```

The phase may mark a race N/A only with a written ownership reason.
