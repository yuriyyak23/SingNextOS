# Directions, feature gates and remaining obligations

This chapter describes **target semantics and bounded existing source surfaces**, not a second implementation backlog and not a claim that all numbered phases have been delivered. When a consumer needs a change, identify its current producer and source first.

## Architectural tracks

| Track | Intent | Current claim limit |
| --- | --- | --- |
| P01 — memory | Ownership, ordering, visibility, use and reclaim | Models/selected local paths do not prove general shared/unknown safety |
| P02 — durability | Persistence, crash and durable output | Local journal is not trusted cold continuity |
| P03 — temporal/resources | Upper bounds, reservations, settlement | Accounting does not imply hard real time or minimum service |
| P04 — translation/DMA | Mappings, grants, generations, visibility and effects | Software adapter does not prove physical isolation |
| P05 — refinement | Machine-checkable semantic compatibility | Proof is neither live permission nor CPU legality |
| P06 — information flow | Protected labels and flow | Requires concrete producer/consumer enforcement |
| P07 — locality | Placement, motion and topology cost | Advisory placement is not authority |
| P08 — preemption | Capture/resume/restart | Requires an executable staged/acquired predecessor |
| P09 — device trust | Identity, freshness and attestation | Model certificates do not establish ProductionSecure |
| P10 — RAS | Failure domains, fault classification, containment | Generic execution error does not prove machine fault type |
| P11 — multi-host | Remote lease, source continuity and closure | Physical remote no-further-use requires independent evidence |
| P12 — energy | Measurement and enforceable budgets | Measurement is not reservation/power authority |
| PCL — lowering | Compiler/lowering evidence and optimization | Neither new permission nor overriding legality |

Many supporting source surfaces are present under [V6](../../../src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs) and [VNext](../../../src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs), including local resource estimation, semantic trace reconciliation, remote-lease journaled submit and preemption models. Presence is not gate activation or production readiness.

## Feature-gate facts proven from current code

- [`V6FeatureGates.IsEnabled`](../../../src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs) returns `TryResolve(name, out _) && false`. No name currently becomes enabled through that method.
- [`VNextFeatureGates.IsEnabled`](../../../src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs) likewise returns `TryResolve(name, out _) && false`. No P00 enablement route is present in that method.
- This is about the inspected code paths, not a universal claim that no independent model class runs or that all source methods are inert.
- Gate promotion must be tied to an actual qualifying producer, consumer, input tuple and evidence.

## Corrected virtualization state

The earlier roadmap note that SIP `DestroyAsync` **still** calls direct `DestroyVirtualDomain` without invocation correlation is outdated. Current [host dispatch](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeVirtualizationServiceHost.cs) calls `DestroySessionVirtualDomain`. The [runtime core](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs) binds `SessionInvocations.BindVirtualTransition` before dependent drain. Remaining work, if any, must be scoped to verified failure paths; do not reopen a resolved architectural gap without a reproduction.

## Outstanding qualification constraints

1. **Trusted cold continuation:** authenticated local history does not by itself establish rollback-proof, monotonic original continuity after all-fact loss or reboot.
2. **Unknown provider histories:** malformed, copied, foreign, source-drifted or missing observations must not lead to replay or reclaim without exact closure.
3. **Independent users/backing:** artifact/guest/DMA metadata removal must not silently discharge other RegionUse or quantitative charges.
4. **Cross-owner failure:** models and bounded tests do not imply universal multi-owner crash atomicity.
5. **Physical boundary:** physical IOMMU, CXL reset, remote memory no-further-use and hardware confidentiality require deployment-specific evidence.
6. **Actual adoption:** an optional provider interface or a `V6*` helper is not proof that a real producer and consuming application use it.

## Dependency guidance

Memory ownership semantics precede claims about shared DMA correctness. Temporal resource contracts and applicable DMA staging precede generalized preemptible heterogeneous operations. Refinement checks are cross-cutting, not an independent permission source. Persistent memory and multi-host claims require their own storage/remote fault assumptions.

Keep compatibility and bounded implementation changes additive until a real breaking need is demonstrated. No assumptions are made about old deleted `docs/SingNextOS-v6-modernize` or `docs/SingNextOS-v6-roadmap-reworked-2026-09-23` paths; neither exists in the pinned current tree.
