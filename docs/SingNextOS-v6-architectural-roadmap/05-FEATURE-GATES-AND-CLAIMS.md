# v6 Feature Gates and Claim Discipline

## Global gates

```text
V6-MEMORY-SEMANTICS
V6-SHARED-ATOMIC-REGION
V6-DURABLE-OUTPUT
V6-TEMPORAL-LEASES
V6-GUARANTEED-DEADLINE
V6-DMA-TRANSLATION-BINDING
V6-FORMAL-REFINEMENT
V6-IFC
V6-LOCALITY-PLANNING
V6-PREEMPTION
V6-STATEFUL-RESUME
V6-DEVICE-ATTESTATION
V6-RAS-PARTIAL-FAILURE
V6-MULTIHOST-LEASES
V6-ENERGY-BUDGETS
V6-PROOF-CARRYING-LOWERING
```

Every gate is OFF at P00.

## Claim levels

| Level | Meaning |
|---|---|
| `ModelOnly` | type/model/spec exists; no runtime enforcement claim |
| `StaticAdmission` | admission/compiler/static verifier enforces a bounded property |
| `RuntimeEnforced` | live SingNext/runtime path enforces it |
| `ExecutableAdapter` | cross-project adapter path executes against a concrete provider/runtime |
| `EnforcedUpperBound` | measured/enforced maximum resource property |
| `GuaranteedReservation` | provider-specific minimum/reservation guarantee with enforcement |
| `HardwareValidated` | named hardware/firmware/IOMMU/CXL contour validated |
| `ProductionQualified` | security, fault, performance, supply-chain and operational gates closed |

## Promotion rule

A feature is promoted only for the exact qualified tuple. A stronger provider, newer package version, different firmware, different CXL topology or altered compiler proof schema requires requalification if it changes any relied-on semantic.

## Mandatory fail-closed examples

- coherent provider with no alias/atomicity proof -> staged path;
- persistence requested but only visibility proven -> no durable publication;
- deadline requested but provider only reports average latency -> no guarantee claim;
- stale IOMMU/PASID mapping generation -> no new DMA effect;
- proof certificate schema unknown -> revalidate without proof or reject if proof mandatory;
- attestation stale -> secure operation denied, not downgraded silently;
- RAS status ambiguous after a possible write -> quarantine/pin until closure/recovery.
