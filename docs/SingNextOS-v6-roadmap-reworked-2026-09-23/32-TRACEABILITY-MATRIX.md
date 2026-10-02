# Corrected Traceability Matrix

| Requirement | Authoritative owner(s) | Phase | Gate | Primary executable evidence | Claim ceiling at baseline |
|---|---|---|---|---|---|
| capability/effect permission | CapabilityAuthority | cross-cutting | existing | authority tests/sentries | RuntimeEnforced existing |
| Region ownership/use | RegionAuthority | P01/P10 | V6-MEMORY-SEMANTICS | Region race/property tests | RuntimeEnforced existing + new contour gated |
| memory ordering/atomicity | provider + HybridCPU legality; Region use prerequisites | P01 | V6-MEMORY-SEMANTICS | litmus + trace refinement | ExecutableAdapter staged contour |
| publication truth | publication/response owner | P01/P02 | existing + phase gates | visibility/publication negative tests | RuntimeEnforced existing contour |
| translation mapping | platform/IOMMU provider | P04 | V6-DMA-TRANSLATION-BINDING | invalidation/reset/race suite | ExecutableAdapter model/provider |
| DMA permission composition | Region + process/address-space + device + provider + operation owners | P04 | V6-DMA-TRANSLATION-BINDING | generation-bound submit tests | RuntimeEnforced composition |
| refinement relation | spec/evaluator, no authority owner | P05 | V6-FORMAL-REFINEMENT | executable checker/model/differential trace | checker RuntimeEnforced; model ModelOnly |
| durability | storage/checkpoint + provider; publication separate | P02 | V6-DURABLE-OUTPUT | crash matrix | adapter-specific |
| quantitative resource accounting | ResourceBudgetAuthority | P03/P12 | phase gates | conservation/settlement | RuntimeEnforced existing |
| deadline/min service | provider enforcement + scheduler analysis, not authority | P03 | V6-GUARANTEED-DEADLINE | contention/schedulability | FutureGated |
| preemption/resume | provider/HybridCPU execution state + operation lifecycle | P08 | V6-PREEMPTION | safe-point/reset/race tests | per-provider adapter |
| labels/flow policy | existing authority authorizes effects; policy restricts | P06 | V6-IFC | propagation/declassify tests | ModelOnly/StaticAdmission |
| locality cost | provider evidence + planner policy | P07 | V6-LOCALITY-PLANNING | A/B performance + stale plan tests | advisory only |
| device trust evidence | provider/trust producer; existing owners keep authority | P09 | V6-DEVICE-ATTESTATION | replay/reset/measurement tests | StaticAdmission/adapter |
| failure/health consequences | provider evidence + Region/ExternalOperation consequence owners | P10 | V6-RAS-PARTIAL-FAILURE | poison/reset/quarantine | model/adapter |
| remote delegation | original logical owner | P11 | V6-MULTIHOST-LEASES | partition/lease model | FutureGated |
| energy budget | ResourceBudgetAuthority when committed | P12 | V6-ENERGY-BUDGETS | accounting/enforcement tests | measurement/upper bound |
| compiler lowering evidence | compiler producer + verifier, no authority | PCL | V6-PROOF-CARRYING-LOWERING | mutation/digest/fallback tests | StaticAdmission |

### C0 telemetry no-wrap requirements — 2026-10-02

C0-TEL-COUNT-001 capture ID cannot wrap or mutate on invalid/terminal refusal; C0-TEL-COUNT-002 last signed-backing capture ID allocated once; C0-TEL-COUNT-003 Dropped exhaustion refuses before queue/state mutation; C0-TEL-COUNT-004 final overflow increment retains V1 policy and existing incomplete batch consumer. Tests: InvalidOrExhaustedCaptureSequenceCannotMutateOrWrap, ConcurrentFinalCaptureSequenceIsAllocatedExactlyOnce, ExhaustedDropCounterRefusesBeforeQueueOrStateMutation, FinalDropIncrementPreservesV1OverflowPolicyAndIncompleteBatch, ConcurrentFinalDropIncrementCannotWrapOrMutateRefusedSample. Fault IDs C0-TEL-COUNT-F01..06: invalid signed capture, terminal capture, concurrent last capture, terminal Dropped, final Dropped per policy, concurrent last Dropped. Existing RuntimeKernel field/subscription record remain owners; no trace/time identity as permission. Evidence: artifacts/v6/iteration-20261002-c0-telemetry-counter-exhaustion. Managed guard RuntimeEnforced, gates OFF; physical/clock/coherent-snapshot completeness not qualified.
