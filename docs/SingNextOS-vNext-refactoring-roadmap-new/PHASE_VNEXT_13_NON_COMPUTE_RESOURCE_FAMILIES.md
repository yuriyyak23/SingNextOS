# P13 — NON COMPUTE RESOURCE FAMILIES

## Purpose

Extend the proven single-class model to throughput and occupancy resources one family at a time; never pretend all resources share one scalar algebra.

## Preconditions

- P12 closed for initial time resource.

## Architectural decisions

- Throughput: DMA/memory/fabric/network bytes per explicit window.
- Occupancy: device memory bytes, queue slots, in-flight operations, guest memory where existing owners permit.
- Every family has its own canonical unit, lifetime and settlement rules.
- Multi-resource operations acquire reversible reservations in canonical order, then cross one irreversible submit boundary.
- DMA/CXL resource capacity never substitutes for DMA effect capability, Region ownership or mapping/IOMMU authority.
- CXL physical topology remains provider-private.

## State / linearization model

Each family uses `ResourceBudgetAuthority` as quantitative owner but separate typed dimension semantics. No cross-family lease split/merge.

## Negative-space obligations

- deadlock acquiring multi-resource vector;
- partial reservation rollback;
- bytes/window arithmetic overflow;
- queue slot leak on provider loss;
- device memory occupancy released before external operation closure;
- CXL/provider topology laundering into public authority.
- time-to-throughput conversion without normative mapping.

## Required executable tests

- Typed dimension compile/runtime negatives.
- Multi-resource canonical-order deadlock stress.
- Partial reservation fault injection.
- Provider-loss occupancy quarantine.
- Separate gate/evidence tests per family.
- No blanket heterogeneous-budget claim test in docs/manifest.

## Expected code / contract owners

- budget dimensions/contracts
- existing DMA/network/CXL/provider effect owners
- Region/mapping owners unchanged

## Claim boundary

`ExecutableAdapter`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Every enabled family separately qualified.
- No cross-dimensional arithmetic accepted.

## Prerequisite for next phase

P14 must define persistence/reconciliation per resource family, not generically.

## Current disposition (2026-09-21)

Closed as a typed `ModelOnly`/FutureGated boundary recorded in `EVIDENCE_P13_RESOURCE_FAMILY_BOUNDARY.md` and `P13_QUALIFICATION_TUPLE.json`. The common ledger proves atomic canonical vectors but no live dimension mapping or provider adapter qualifies throughput/occupancy families. Every P13 family gate remains OFF; P14 applies only to the existing ComputeTime contour and cannot generalize persistence claims to these future families.
