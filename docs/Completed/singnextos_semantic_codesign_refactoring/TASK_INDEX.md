# Task index

| Task | Phase | Title | Classification | Repository | HybridCPU impact | ISA |
|---|---|---|---|---|---|---|
| CD-P00-01 | P00 | Refresh exact SHAs/tree SHAs | `STALE_OR_INCORRECT` | SingNextOS | `NONE` | `NONE` |
| CD-P00-02 | P00 | Recompute package/DLL hashes and lockfile identity at implementation start | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P00-03 | P00 | Inventory tests/contracts and record test-evidence level | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P01-01 | P01 | Build owner/fact matrix from live code | `VERIFIED_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P01-02 | P01 | Trace submit/complete/visible/publish/settle transitions | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P01-03 | P01 | Architecture tests reject duplicate owners/forbidden imports | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P02-01 | P02 | Define dimension-specific partial orders | `PARTIALLY_EXISTING` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P02-02 | P02 | Define Mandatory/Advisory/Unsupported semantics | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P02-03 | P02 | Property-test refinement monotonicity and version failure | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P03-01 | P03 | Specify GlobalState as product of owner states | `NEW_PROPOSED` | Docs/Formal | `NONE` | `NONE` |
| CD-P03-02 | P03 | Define cross-owner commit points and compensation | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P03-03 | P03 | Model revoke/reserve/session/Region/provider races | `NEW_PROPOSED` | Docs/Formal | `NONE` | `NONE` |
| CD-P04-01 | P04 | Add versioned OperationObligationsV1 vocabulary | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P04-02 | P04 | Compose obligations from exact live owner snapshots | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P04-03 | P04 | Revalidate obligation identity at final sentry | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P04-04 | P04 | Negative tests: descriptor possession never authorizes | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P05-01 | P05 | Add provider-neutral ExecutionGuaranteesV1 descriptor | `NEW_PROPOSED` | HybridCPU Contracts + SingNext adapter | `CONTRACT_ONLY` | `NONE` |
| CD-P05-02 | P05 | Bind exact OS operation/provider/runtime contract context | `NEW_PROPOSED` | SingNextOS | `CONTRACT_ONLY` | `NONE` |
| CD-P05-03 | P05 | Conservative compatibility mapping for Contracts 1.14.0 | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P05-04 | P05 | Cross-repo ABI/conformance tests | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P06-01 | P06 | Extend existing prepare/revalidate/commit path | `PARTIALLY_EXISTING` | SingNextOS | `CONTRACT_ONLY` | `NONE` |
| CD-P06-02 | P06 | Bind refinement result to exact binding generation | `NEW_PROPOSED` | SingNextOS | `CONTRACT_ONLY` | `NONE` |
| CD-P06-03 | P06 | Guarantee one submit-start winner + pre-submit compensation | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P06-04 | P06 | Race tests revoke/session-close/provider-drift | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P07-01 | P07 | Model orthogonal effect traits without parallel lifecycle | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P07-02 | P07 | Map current ExternalEffectPolicy conservatively | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P07-03 | P07 | Exact SingNext publication decision + provider action for staged contour | `PARTIALLY_EXISTING` | Both | `RUNTIME_ONLY` | `NONE` |
| CD-P07-04 | P07 | Effect-class lifecycle tests | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P08-01 | P08 | Define visibility classes and Region transition mapping | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P08-02 | P08 | Keep direct-coherent contour gated until alias-exclusion evidence | `VERIFIED_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P08-03 | P08 | Shared-mutable/atomic authority extension only if a real contour requires it | `DEFERRED` | SingNextOS | `NONE` | `NONE` |
| CD-P08-04 | P08 | Semantic locality model without hardware IDs in authority API | `NEW_PROPOSED` | SingNextOS | `CONTRACT_ONLY` | `NONE` |
| CD-P09-01 | P09 | Define chargeability matrix per ResourceClassV1 | `NEW_PROPOSED` | Both | `RUNTIME_ONLY` | `NONE` |
| CD-P09-02 | P09 | Bind usage evidence to measurement contract identity | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P09-03 | P09 | Reconcile replay/squash/retry/SMT/blocked time explicitly | `PARTIALLY_EXISTING` | Both | `RUNTIME_ONLY` | `NONE` |
| CD-P09-04 | P09 | Enforce envelope and duplicate evidence invariants | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P10-01 | P10 | Verify/reuse atomic multi-dimensional budget reservation and add only missing envelope mapping | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P10-02 | P10 | Remove standalone banker/escrow protocol from critical path | `REMOVE_OR_MERGE` | SingNextOS | `NONE` | `NONE` |
| CD-P10-03 | P10 | Preserve existing donation assurance/priority ceilings | `VERIFIED_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P10-04 | P10 | Scheduler uses evidence but cannot authorize/refine mismatch | `VERIFIED_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P11-01 | P11 | Define semantic preemption/cancellation obligations with conservative mapping | `PARTIALLY_EXISTING` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P11-02 | P11 | Extend guarantees only for executable cancellation/preemption classes | `NEW_PROPOSED` | HybridCPU-v2 | `RUNTIME_ONLY` | `NONE` |
| CD-P11-03 | P11 | Replace global EffectEpoch with contour-scoped containment closure hook | `NEW_PROPOSED` | Both | `PROVIDER_SPECIFIC` | `NONE` |
| CD-P11-04 | P11 | Cancellation/retire/replay/containment fault tests | `NEW_PROPOSED` | Both | `RUNTIME_ONLY` | `NONE` |
| CD-P12-01 | P12 | Define replay/determinism lattice by extending existing replay policy | `PARTIALLY_EXISTING` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P12-02 | P12 | ProviderBehavior refines semantic operation contract including numeric semantics | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P12-03 | P12 | Typed isolation subset checks without overclaim | `PARTIALLY_EXISTING` | HybridCPU-v2 | `RUNTIME_ONLY` | `NONE` |
| CD-P12-04 | P12 | Guarantee laundering/provider-class switch tests | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P13-01 | P13 | Classify fault/trust models | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P13-02 | P13 | Define eventual settlement/containment/reclaim policy | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P13-03 | P13 | Bound retries only under explicit fault assumptions | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P13-04 | P13 | Cold restart without authority resurrection | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P14-01 | P14 | Measure contention before redesign | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P14-02 | P14 | Shard only if measured gate fails target | `DEFERRED` | SingNextOS | `NONE` | `NONE` |
| CD-P14-03 | P14 | Escrow/local reservation only if sharding requires it | `DEFERRED` | SingNextOS | `NONE` | `NONE` |
| CD-P14-04 | P14 | Prove no cross-shard double-spend/ABA if optional branch lands | `BLOCKED` | SingNextOS | `NONE` | `NONE` |
| CD-P15-01 | P15 | Specify managed execution assumptions only from live AdmissionVerifier/evidence | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P15-02 | P15 | Root/service mint policy uses existing CapabilityAuthority | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P15-03 | P15 | Durable identity+policy+principal -> fresh ephemeral authority | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P15-04 | P15 | Keep IFC out of critical path | `DEFERRED` | SingNextOS | `NONE` | `NONE` |
| CD-P16-01 | P16 | Define authority-visible trace vocabulary | `PARTIALLY_EXISTING` | SingNextOS | `NONE` | `NONE` |
| CD-P16-02 | P16 | Differential/weak-bisimulation ordinary SIP vs fused path | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P16-03 | P16 | Defer declarative OperationContract code generator until protocols stabilize | `DEFERRED` | SingNextOS | `NONE` | `NONE` |
| CD-P16-04 | P16 | Generated metadata cannot authorize | `DEFERRED` | SingNextOS | `NONE` | `NONE` |
| CD-P17-01 | P17 | Add MatrixMultiply semantic operation and A/B/C Region operands in SingNext | `VERIFIED_GAP` | SingNextOS | `NONE` | `NONE` |
| CD-P17-02 | P17 | Map Matrix obligations to proven HybridCPU guarantees and exact binding | `NEW_PROPOSED` | Both | `CONTRACT_ONLY` | `NONE` |
| CD-P17-03 | P17 | Execute via current runtime legality and staged provider publication path | `PARTIALLY_EXISTING` | Both | `RUNTIME_ONLY` | `NONE` |
| CD-P17-04 | P17 | Collect bound usage/visibility/publication/settlement evidence | `NEW_PROPOSED` | Both | `RUNTIME_ONLY` | `NONE` |
| CD-P18-01 | P18 | Run full adversarial matrix + selected model checking | `NEW_PROPOSED` | Both | `RUNTIME_ONLY` | `NONE` |
| CD-P18-02 | P18 | Record exact SHA/package/runtime/gate/test tuple | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P18-03 | P18 | Promote only exact Matrix/staged contour | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
| CD-P18-04 | P18 | Synchronize docs/traceability and retain rollback path | `NEW_PROPOSED` | SingNextOS | `NONE` | `NONE` |
