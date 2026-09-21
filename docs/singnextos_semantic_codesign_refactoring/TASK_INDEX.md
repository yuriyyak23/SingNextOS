# Task Index

| Task | Phase | Title | ТЗ |
|---|---|---|---|
| CD-P00-01 | P00 | Freeze exact SHAs and tree SHAs | `TZ_P00_LIVE_BASELINE_SOURCE_FREEZE.md` |
| CD-P00-02 | P00 | Recompute package/DLL hashes and lockfile identity | `TZ_P00_LIVE_BASELINE_SOURCE_FREEZE.md` |
| CD-P00-03 | P00 | Inventory relevant tests/contracts and record current pass/fail baseline | `TZ_P00_LIVE_BASELINE_SOURCE_FREEZE.md` |
| CD-P01-01 | P01 | Build owner/fact matrix from live code | `TZ_P01_OWNER_AND_MACHINE_RECONSTRUCTION.md` |
| CD-P01-02 | P01 | Trace submit/complete/visible/publish/settle transitions | `TZ_P01_OWNER_AND_MACHINE_RECONSTRUCTION.md` |
| CD-P01-03 | P01 | Add architecture tests that reject duplicate owners/forbidden authority imports | `TZ_P01_OWNER_AND_MACHINE_RECONSTRUCTION.md` |
| CD-P02-01 | P02 | Define dimension-specific partial orders | `TZ_P02_SEMANTIC_VOCABULARY_AND_REFINEMENT.md` |
| CD-P02-02 | P02 | Define mandatory/advisory/unsupported semantics | `TZ_P02_SEMANTIC_VOCABULARY_AND_REFINEMENT.md` |
| CD-P02-03 | P02 | Property-test refinement monotonicity and unknown-version failure | `TZ_P02_SEMANTIC_VOCABULARY_AND_REFINEMENT.md` |
| CD-P03-01 | P03 | Specify GlobalState and owner transitions | `TZ_P03_GLOBAL_OPERATIONAL_SEMANTICS.md` |
| CD-P03-02 | P03 | Define cross-owner commit points and compensation | `TZ_P03_GLOBAL_OPERATIONAL_SEMANTICS.md` |
| CD-P03-03 | P03 | TLA+/PlusCal model for revoke/reserve/session/Region/provider races | `TZ_P03_GLOBAL_OPERATIONAL_SEMANTICS.md` |
| CD-P04-01 | P04 | Add versioned obligation DTO vocabulary | `TZ_P04_OPERATION_OBLIGATIONS.md` |
| CD-P04-02 | P04 | Compose from exact current owner snapshots | `TZ_P04_OPERATION_OBLIGATIONS.md` |
| CD-P04-03 | P04 | Revalidate snapshot identity at final sentry | `TZ_P04_OPERATION_OBLIGATIONS.md` |
| CD-P04-04 | P04 | Negative tests prove descriptor cannot authorize by possession | `TZ_P04_OPERATION_OBLIGATIONS.md` |
| CD-P05-01 | P05 | Add provider-neutral guarantee descriptor | `TZ_P05_EXECUTION_GUARANTEES_AND_BINDING.md` |
| CD-P05-02 | P05 | Bind exact OS operation/provider generations/contracts | `TZ_P05_EXECUTION_GUARANTEES_AND_BINDING.md` |
| CD-P05-03 | P05 | Add conservative compatibility mapping for 1.14.0 | `TZ_P05_EXECUTION_GUARANTEES_AND_BINDING.md` |
| CD-P05-04 | P05 | Cross-repo API baseline/conformance tests | `TZ_P05_EXECUTION_GUARANTEES_AND_BINDING.md` |
| CD-P06-01 | P06 | Extend existing prepare/revalidate/commit path | `TZ_P06_GENERATION_EXACT_ADMISSION_SENTRY.md` |
| CD-P06-02 | P06 | Bind refinement decision to exact binding generation | `TZ_P06_GENERATION_EXACT_ADMISSION_SENTRY.md` |
| CD-P06-03 | P06 | Single submit-start winner + pre-submit compensation | `TZ_P06_GENERATION_EXACT_ADMISSION_SENTRY.md` |
| CD-P06-04 | P06 | race tests revoke/session-close/provider-drift | `TZ_P06_GENERATION_EXACT_ADMISSION_SENTRY.md` |
| CD-P07-01 | P07 | Define staged/reversible/observable/durable/compensatable axes | `TZ_P07_EFFECT_AND_PUBLICATION_ALGEBRA.md` |
| CD-P07-02 | P07 | Map current ExternalEffectPolicy conservatively | `TZ_P07_EFFECT_AND_PUBLICATION_ALGEBRA.md` |
| CD-P07-03 | P07 | Add exact staged publication decision and provider invocation seam | `TZ_P07_EFFECT_AND_PUBLICATION_ALGEBRA.md` |
| CD-P07-04 | P07 | effect-class lifecycle tests | `TZ_P07_EFFECT_AND_PUBLICATION_ALGEBRA.md` |
| CD-P08-01 | P08 | Define visibility classes and Region transition mapping | `TZ_P08_VISIBILITY_MEMORY_SHARING_LOCALITY.md` |
| CD-P08-02 | P08 | Keep direct-coherent contour gated until alias-exclusion evidence | `TZ_P08_VISIBILITY_MEMORY_SHARING_LOCALITY.md` |
| CD-P08-03 | P08 | Design shared-mutable/atomic mutation authority inside RegionAuthority | `TZ_P08_VISIBILITY_MEMORY_SHARING_LOCALITY.md` |
| CD-P08-04 | P08 | semantic locality model without core/lane/NUMA IDs in authority API | `TZ_P08_VISIBILITY_MEMORY_SHARING_LOCALITY.md` |
| CD-P09-01 | P09 | Define chargeability matrix per resource class | `TZ_P09_RESOURCE_MEASUREMENT_RETIRE_CHARGING.md` |
| CD-P09-02 | P09 | Extend exact usage evidence with measurement contract identity | `TZ_P09_RESOURCE_MEASUREMENT_RETIRE_CHARGING.md` |
| CD-P09-03 | P09 | Reconcile replay/squash/retry/SMT/blocked time | `TZ_P09_RESOURCE_MEASUREMENT_RETIRE_CHARGING.md` |
| CD-P09-04 | P09 | enforce over-envelope and duplicate evidence invariants | `TZ_P09_RESOURCE_MEASUREMENT_RETIRE_CHARGING.md` |
| CD-P10-01 | P10 | Add heterogeneous vector reservation to existing budget owner | `TZ_P10_MULTI_RESOURCE_QOS_SCHEDULING.md` |
| CD-P10-02 | P10 | Choose up-front atomic/escrow protocol; prohibit hold-and-wait | `TZ_P10_MULTI_RESOURCE_QOS_SCHEDULING.md` |
| CD-P10-03 | P10 | preserve priority/donation ceilings | `TZ_P10_MULTI_RESOURCE_QOS_SCHEDULING.md` |
| CD-P10-04 | P10 | scheduler consumes evidence but cannot authorize | `TZ_P10_MULTI_RESOURCE_QOS_SCHEDULING.md` |
| CD-P11-01 | P11 | Define cancellation/preemption classes and max latency semantics | `TZ_P11_PREEMPTION_CANCELLATION_CONTAINMENT.md` |
| CD-P11-02 | P11 | extend provider guarantee/refinement mapping | `TZ_P11_PREEMPTION_CANCELLATION_CONTAINMENT.md` |
| CD-P11-03 | P11 | implement provider-coordination EffectEpoch without ISA change | `TZ_P11_PREEMPTION_CANCELLATION_CONTAINMENT.md` |
| CD-P11-04 | P11 | close-vs-retire/replay/cancel fault tests | `TZ_P11_PREEMPTION_CANCELLATION_CONTAINMENT.md` |
| CD-P12-01 | P12 | define replay/determinism lattice | `TZ_P12_REPLAY_PROVIDER_REFINEMENT_ISOLATION.md` |
| CD-P12-02 | P12 | provider behavior refinement including numeric semantics | `TZ_P12_REPLAY_PROVIDER_REFINEMENT_ISOLATION.md` |
| CD-P12-03 | P12 | typed isolation subset checks | `TZ_P12_REPLAY_PROVIDER_REFINEMENT_ISOLATION.md` |
| CD-P12-04 | P12 | guarantee-laundering and provider-class-switch tests | `TZ_P12_REPLAY_PROVIDER_REFINEMENT_ISOLATION.md` |
| CD-P13-01 | P13 | classify crash/omission/duplication/reordering/partition/malicious evidence | `TZ_P13_LIVENESS_FAULT_QUARANTINE_RECOVERY.md` |
| CD-P13-02 | P13 | define eventual settlement/containment/reclaim policies | `TZ_P13_LIVENESS_FAULT_QUARANTINE_RECOVERY.md` |
| CD-P13-03 | P13 | add bounded retry/terminal escalation | `TZ_P13_LIVENESS_FAULT_QUARANTINE_RECOVERY.md` |
| CD-P13-04 | P13 | cold-restart reconciliation design without authority resurrection | `TZ_P13_LIVENESS_FAULT_QUARANTINE_RECOVERY.md` |
| CD-P14-01 | P14 | measure contention before redesign | `TZ_P14_SCALABLE_AUTHORITY_OWNERS.md` |
| CD-P14-02 | P14 | shard by stable owner/realm/domain where semantics permit | `TZ_P14_SCALABLE_AUTHORITY_OWNERS.md` |
| CD-P14-03 | P14 | budget escrow/local reservations with parent conservation | `TZ_P14_SCALABLE_AUTHORITY_OWNERS.md` |
| CD-P14-04 | P14 | prove no cross-shard double spend/ABA | `TZ_P14_SCALABLE_AUTHORITY_OWNERS.md` |
| CD-P15-01 | P15 | specify managed execution assumptions/evidence | `TZ_P15_MANAGED_ROOT_DURABLE_IFC.md` |
| CD-P15-02 | P15 | define boot/service root mint policy using existing CapabilityAuthority | `TZ_P15_MANAGED_ROOT_DURABLE_IFC.md` |
| CD-P15-03 | P15 | durable identity+policy+principal -> fresh ephemeral authority | `TZ_P15_MANAGED_ROOT_DURABLE_IFC.md` |
| CD-P15-04 | P15 | decide IFC: defer unless cross-domain confidentiality thesis requires labels | `TZ_P15_MANAGED_ROOT_DURABLE_IFC.md` |
| CD-P16-01 | P16 | define authority-visible trace vocabulary | `TZ_P16_SIPJOB_AND_DECLARATIVE_OPERATION_MODEL.md` |
| CD-P16-02 | P16 | differential/weak-bisimulation tests ordinary SIP vs fused path | `TZ_P16_SIPJOB_AND_DECLARATIVE_OPERATION_MODEL.md` |
| CD-P16-03 | P16 | generate sentry/test/quarantine skeletons from OperationContract | `TZ_P16_SIPJOB_AND_DECLARATIVE_OPERATION_MODEL.md` |
| CD-P16-04 | P16 | prove generated metadata cannot mint/mutate authority | `TZ_P16_SIPJOB_AND_DECLARATIVE_OPERATION_MODEL.md` |
| CD-P17-01 | P17 | add MatrixMultiply semantic contract and Region operands A/B/C | `TZ_P17_MATRIXMULTIPLY_HYBRIDCPU_VERTICAL.md` |
| CD-P17-02 | P17 | map obligations to HybridCPU guarantees and exact binding | `TZ_P17_MATRIXMULTIPLY_HYBRIDCPU_VERTICAL.md` |
| CD-P17-03 | P17 | execute through runtime legality + staged publication path | `TZ_P17_MATRIXMULTIPLY_HYBRIDCPU_VERTICAL.md` |
| CD-P17-04 | P17 | collect resource/visibility/publication/settlement evidence | `TZ_P17_MATRIXMULTIPLY_HYBRIDCPU_VERTICAL.md` |
| CD-P18-01 | P18 | run full adversarial matrix + model checking | `TZ_P18_QUALIFICATION_PROMOTION_CUTOVER.md` |
| CD-P18-02 | P18 | record exact SHA/package/runtime/gate/test tuple | `TZ_P18_QUALIFICATION_PROMOTION_CUTOVER.md` |
| CD-P18-03 | P18 | promote only exact Matrix/staged contour | `TZ_P18_QUALIFICATION_PROMOTION_CUTOVER.md` |
| CD-P18-04 | P18 | synchronize docs/traceability and retain rollback path | `TZ_P18_QUALIFICATION_PROMOTION_CUTOVER.md` |
