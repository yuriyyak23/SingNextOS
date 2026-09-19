# SingNextOS post-SingCap-M-v1 — SipJob Refactoring Roadmap

**Status:** proposed post-v1 roadmap; no runtime claim is implied by this document set.  
**Scope:** modernization of SingNextOS after SingCap-M v1 to add a qualified fast intra-runtime SIP composition path (`SipJob`) while preserving the existing capability, session, Region, sealing, external-operation and publication authority owners.

## Baseline discipline

The SingCap-M roadmap and P13 evidence pin the qualified v1 work to SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681` and HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`. At plan-authoring time the repository had newer commits. **P14-0 MUST re-pin the exact SingNextOS implementation SHA, toolchain tuple, admission-policy digest and HybridCPU artifact/source identity before any P14 semantic code lands.** Newer repository state must never be silently treated as equivalent to the P13 qualification tuple.

## Architectural objective

`SipJob` is a **qualified execution-composition optimization** over existing SIP contracts. It may remove intermediate transport materialization when two or more ManagedCap SIP stages execute inside one qualified managed runtime, but it does not remove their security semantics.

```text
normal SIP edge
    = security transition
    + transport materialization
    + invocation correlation
    + publication boundary

fused SipJob edge
    = security transition
    + ownership/use transition where required
    + direct generated trusted dispatch

transport/publication/scheduler boundaries may be elided only when the
feature gate proves that their semantics are not externally required.
```

## Non-negotiable invariants

1. **No new authority universe.** `SipJob`, `SipJobPlan`, `SipJobHandle`, `JobRunFrame`, stage descriptors, plan digests, caches and completion evidence MUST NOT become capability/Region/session/seal/publication authority. Permission remains in `CapabilityAuthority`; subject incarnation in `ProcessRegistry`; session lifecycle in `EndpointSessionRegistry`; memory ownership/use in `RegionAuthority`; sealed-object identity/lifecycle in `SealedObjectAuthority`; external-effect lifecycle in `ExternalOperationAuthority`; publication in the existing response/invocation owners.
2. **No raw CLR references across ManagedCap SIP/Job boundaries.** A mutable raw CLR object reference, service implementation object, delegate, `IServiceProvider`, ambient service locator, arbitrary object graph or capability-bearing mutable static MUST NOT cross a ManagedCap trust boundary or Job edge. Job-internal trusted runtime references are TCB-private and are never projected into ManagedCap code.
3. **Same SIP contract semantics.** A fused stage uses the same generated contract metadata, exact authority requirements, ownership annotations, protocol transition and response/authority declarations as the normal SIP path. Fusion is an optimization, not a second ABI.
4. **Generated sentry remains the security transition.** Eliding queue/response machinery never means bypassing the operation-specific generated sentry.
5. **No authority by plan possession.** A plan or Job handle identifies a qualified plan/binding only. Execution still requires current exact subject/session/capability/Region/seal authority.
6. **No fake transactions.** Job execution is not ACID. Failure cannot claim rollback of arbitrary private managed state or irreversible provider effects.
7. **Completion is not publication.** Stage completion, provider completion, visibility, publication, ownership settlement and release remain distinct states.
8. **No provider/user code under authority locks.** P04 `AUTHORITY_COMPOSITION_PROTOCOL` remains normative for every fused composed effect.
9. **No physical HybridCPU topology in SIP/Job ABI.** No lane numbers, raw opcodes, VMCS/IOMMU/provider tokens or CXL topology become application Job metadata.
10. **Fallback must preserve semantics.** If a fusion gate is not satisfied, the runtime materializes the ordinary SIP boundary or rejects the plan; it never weakens checks to retain performance.

## Dependency graph

```text
P14-0 Architectural freeze + ADRs + baseline repin
   |
   +--> P14-1 Job plan metadata + graph verifier (linear MVP)
           |
           +--> P14-2 Generated intra-runtime sentry thunk
                   |
                   +--> P14-3 Region/ownership edge settlement
                           |
                           +--> P14-4 Multi-session prepare/pin/commit + segment admission
                                   |
                                   +--> P14-5 Fusion barriers, async/cancellation, read-only DAG gate
                                           |
                                           +--> P14-6 Dynamic selection of precompiled qualified stages
                                                   |
                                                   +--> P14-7 HybridCPU/provider-neutral scheduling integration
                                                           |
                                                           +--> P14-8 Security/performance/claim qualification
```

## Phase files

| Phase | File | Primary result |
|---|---|---|
| P14-0 | `PHASE_14_0_ARCHITECTURAL_FREEZE.md` | freeze Job semantics, baseline and non-authority rules |
| P14-1 | `PHASE_14_1_PLAN_AND_GRAPH_VERIFIER.md` | immutable plan/stage/edge metadata and linear graph verification |
| P14-2 | `PHASE_14_2_DIRECT_GENERATED_SENTRY_PATH.md` | trusted direct intra-runtime dispatch without bypassing sentry semantics |
| P14-3 | `PHASE_14_3_REGION_AND_OWNERSHIP_EDGES.md` | MOVE/BORROW settlement, terminal-path ownership proof |
| P14-4 | `PHASE_14_4_COMPOSED_ADMISSION_AND_SEGMENTS.md` | multi-owner prepare/pin/commit and admission segmentation |
| P14-5 | `PHASE_14_5_FUSION_BARRIERS_ASYNC_AND_DAG.md` | explicit materialization barriers; feature-gated read-only DAG |
| P14-6 | `PHASE_14_6_DYNAMIC_BINDING_AND_PLAN_CACHE.md` | runtime selection of precompiled qualified stages, no dynamic codegen |
| P14-7 | `PHASE_14_7_HYBRIDCPU_SCHEDULING_INTEGRATION.md` | semantic scheduler hints and provider-neutral Job execution classes |
| P14-8 | `PHASE_14_8_QUALIFICATION_PERFORMANCE_AND_CLAIMS.md` | adversarial, race, contention and performance closure |

Cross-cutting rules are in `SIPJOB_NORMATIVE_INVARIANTS.md`, feature rollout in `FEATURE_GATES.md`, and test/requirement ownership in `TRACEABILITY_AND_CI_GATES.md`.

## Rollout principle

The first production-shaped contour is intentionally narrow: **linear, same-runtime, generated ManagedCap SIP stages with no external provider inside the fused segment, bounded values plus qualified Region BORROW/MOVE, one final externally visible publication, and no independently observable intermediate invocation.** Read-only DAG fan-out, parallel branches, async provider stages, split-runtime plans and HybridCPU scheduling integration are separate feature gates and MUST remain disabled until their phase-specific evidence exists.
