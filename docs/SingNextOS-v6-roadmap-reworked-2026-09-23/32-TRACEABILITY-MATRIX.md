# Requirement-to-owner traceability

This table maps target properties to existing owners and evidence kinds; it is not a current qualification/status matrix. Actual bounded exits are in [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Apply only the selected consumer's requirements.

| Requirement | Authoritative owner(s) | Phase | Gate | Primary executable evidence | Property-specific evidence boundary |
|---|---|---|---|---|---|
| capability/effect permission | CapabilityAuthority | cross-cutting | existing | authority tests/sentries | Existing owner enforcement; exact contour evidence required |
| Region ownership/use | RegionAuthority | P01/P10 | V6-MEMORY-SEMANTICS | Region race/property tests | Existing owner enforcement; new contour qualification separate |
| memory ordering/atomicity | provider + HybridCPU legality; Region use prerequisites | P01 | V6-MEMORY-SEMANTICS | litmus + trace refinement | Concrete staged adapter evidence required |
| publication truth | publication/response owner | P01/P02 | existing + phase gates | visibility/publication negative tests | Existing owner enforcement; exact contour evidence required |
| translation mapping | platform/IOMMU provider | P04 | V6-DMA-TRANSLATION-BINDING | invalidation/reset/race suite | ModelOnly model / concrete adapter separately |
| DMA permission composition | Region + process/address-space + device + provider + operation owners | P04 | V6-DMA-TRANSLATION-BINDING | generation-bound submit tests | Exact composition consumer evidence required |
| refinement relation | spec/evaluator, no authority owner | P05 | V6-FORMAL-REFINEMENT | executable checker/model/differential trace | checker RuntimeEnforced; model ModelOnly |
| durability | storage/checkpoint + provider; publication separate | P02 | V6-DURABLE-OUTPUT | crash matrix | adapter-specific |
| quantitative resource accounting | ResourceBudgetAuthority | P03/P12 | phase gates | conservation/settlement | Existing owner enforcement; exact contour evidence required |
| deadline/min service | provider enforcement + scheduler analysis, not authority | P03 | V6-GUARANTEED-DEADLINE | contention/schedulability | FutureGated |
| preemption/resume | provider/HybridCPU execution state + operation lifecycle | P08 | V6-PREEMPTION | safe-point/reset/race tests | per-provider adapter |
| labels/flow policy | existing authority authorizes effects; policy restricts | P06 | V6-IFC | propagation/declassify tests | ModelOnly/StaticAdmission |
| locality cost | provider evidence + planner policy | P07 | V6-LOCALITY-PLANNING | A/B performance + stale plan tests | advisory only |
| device trust evidence | provider/trust producer; existing owners keep authority | P09 | V6-DEVICE-ATTESTATION | replay/reset/measurement tests | StaticAdmission/adapter |
| failure/health consequences | provider evidence + Region/ExternalOperation consequence owners | P10 | V6-RAS-PARTIAL-FAILURE | poison/reset/quarantine | model/adapter |
| remote delegation | original logical owner | P11 | V6-MULTIHOST-LEASES | partition/lease model | FutureGated |
| energy budget | ResourceBudgetAuthority when committed | P12 | V6-ENERGY-BUDGETS | accounting/enforcement tests | measurement/upper bound |
| compiler lowering evidence | compiler producer + verifier, no authority | PCL | V6-PROOF-CARRYING-LOWERING | mutation/digest/fallback tests | StaticAdmission |

## Existing telemetry counter guard

Capture/drop counters refuse terminal allocation before partial mutation and retain V1 overflow policy. Requirement/test/fault IDs and exact inputs remain in [original evidence](../../artifacts/v6/iteration-20261002-c0-telemetry-counter-exhaustion). This is a bounded owner guard, not clock/physical evidence or a current task.
