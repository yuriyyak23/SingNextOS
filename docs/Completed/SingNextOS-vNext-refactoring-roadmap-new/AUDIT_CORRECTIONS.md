# Audit corrections applied to the vNext roadmap

This file records how the prior roadmap was transformed rather than merely edited cosmetically.

| Area | Action | Corrected decision |
|---|---|---|
| Independent `TemporalResourceAuthority` | **REMOVE / MERGE** | no new owner; resource permission uses existing `CapabilityAuthority`; quantitative truth stays in `ResourceBudgetAuthority` |
| Accounting vs authority | **CHANGE** | accounting snapshots remain non-authoritative; resource-use permission is a distinct capability constraint/grant |
| Reservation/lease/settlement | **MERGE + HARDEN** | one owner and one linearizable state machine in `ResourceBudgetAuthority` |
| Conservation | **ADD** | atomic split/reserve/consume/refund invariants; no sibling-union amplification |
| Donation | **CHANGE** | EndpointSession binds a narrowed grant + budget lineage to one invocation; no raw ambient budget transfer |
| Provider loss | **CHANGE** | unknown after possible submit => quarantine/worst-case policy, never automatic refund |
| Compute planning | **CHANGE** | policy output is non-authoritative and every execution revalidates all owners |
| HybridCPU integration | **KEEP + NARROW** | provider-neutral additive contracts only when existing contracts are insufficient; no ISA change |
| Temporal guarantees | **SPLIT** | accounting, enforced upper bound, guaranteed reservation are separate claim classes |
| SipJob | **KEEP + CONSTRAIN** | optimization only; may remove transport but never authoritative checks/transitions |
| Non-compute resources | **SPLIT BY DIMENSION** | time, throughput, occupancy families; no cross-dimensional scalar algebra |
| Restart/checkpoint | **HARDEN** | executable authority is not restored from checkpoints; fresh authorization and reconciliation required |
| Observability | **KEEP + HARDEN** | evidence only; no mint/release/settle side effects |
| Qualification | **CHANGE** | exact SHA/package/provider/runtime/gate/test tuple required for every claim |
| Migration | **ADD** | mixed-version/dual-stack tests and proof of no live consumers before removing compatibility owner/path |
| CHERI/ISA | **KEEP NON-GOAL** | software monotonicity/provenance only; ISA-change category remains empty |

## Important correction to the prior audit wording

Provider loss after possible submission is **not** equivalent to safe refund. The corrected plan uses quarantine, exact reconciliation, trusted containment, or a declared worst-case charge policy. It never assumes that timeout/disconnect means no consumption.

## Commit boundaries

The roadmap no longer claims that every contour has exactly three global commits. Instead it names contour-specific boundaries:

1. local resource reservation/lease linearization;
2. local effect/admission commit where applicable;
3. provider admission/submission boundary;
4. CPU/runtime retire for contours where retire is meaningful;
5. provider completion;
6. visibility;
7. OS publication;
8. resource settlement/release and Region reclaim, each owned independently.

Some boundaries can coincide in a host-model contour, but qualification must prove coincidence rather than assume it.
