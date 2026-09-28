# ТЗ P13 — Fault, liveness, quarantine and cold-restart reconciliation

    **Depends on:** P12  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P13-01 — Classify fault/trust models

- **Phase:** P13
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs; contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs`
- **Exact current symbols:** `QuarantineLease; ReconcileLease; RestoreOrdinaryCheckpoint; TraceReplayEngine.CorrelateExternalRuntimeEvidence; CancellationDisposition.ProviderClosurePending`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase08OrdinaryCheckpointTests.cs; fault-injection tests required
- **Current live behavior:** Current code handles stale/provider loss/quarantine but does not expose one co-design fault matrix.
- **Actual gap:** Claims differ for CrashStop/Recovery/Omission/Duplication/Reordering/Partition/CorruptEvidence/MaliciousProvider.
- **Required change:** Document supported trust classes and exact evidence acceptance; default local trusted-but-fallible for first contour. Remote/attested/byzantine stays unsupported unless concrete auth/attestation exists.
- **Authoritative owner:** Policy/spec
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Fault-class matrix drives tests; no synthetic malicious-provider security claim.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P13-02 — Define eventual settlement/containment/reclaim policy

- **Phase:** P13
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs; contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs`
- **Exact current symbols:** `QuarantineLease; ReconcileLease; RestoreOrdinaryCheckpoint; TraceReplayEngine.CorrelateExternalRuntimeEvidence; CancellationDisposition.ProviderClosurePending`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase08OrdinaryCheckpointTests.cs; fault-injection tests required
- **Current live behavior:** Budget quarantine/reconcile and ReleasePlan already prevent provider loss from proving reclaim.
- **Actual gap:** Timeout/reconciliation owner and terminal policy are not complete for new semantic binding.
- **Required change:** For each uncertain state state owner/held resources/forbidden actions/reconciliation/timeout/conservative settlement/reclaim precondition. Partition may remain quarantined until operator/policy proof.
- **Authoritative owner:** Existing authority owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Provider loss after possible submit never auto-refunds; late evidence exact-match only.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P13-03 — Bound retries only under explicit fault assumptions

- **Phase:** P13
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs; contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs`
- **Exact current symbols:** `QuarantineLease; ReconcileLease; RestoreOrdinaryCheckpoint; TraceReplayEngine.CorrelateExternalRuntimeEvidence; CancellationDisposition.ProviderClosurePending`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase08OrdinaryCheckpointTests.cs; fault-injection tests required
- **Current live behavior:** No generic bounded liveness proof exists.
- **Actual gap:** Fixed retry count can be mistaken for semantic closure.
- **Required change:** Retry transport/reconciliation under declared assumptions; terminal escalation is quarantine/manual reconciliation, not proof of no effect.
- **Authoritative owner:** Reconciliation coordinator
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Omission/reordering/partition schedules; liveness claim tagged with assumptions.
- **Formal obligation:** TLA+/PlusCal safety + conditional liveness.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P13-04 — Cold restart without authority resurrection

- **Phase:** P13
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs; contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs`
- **Exact current symbols:** `QuarantineLease; ReconcileLease; RestoreOrdinaryCheckpoint; TraceReplayEngine.CorrelateExternalRuntimeEvidence; CancellationDisposition.ProviderClosurePending`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase08OrdinaryCheckpointTests.cs; fault-injection tests required
- **Current live behavior:** Checkpoint restore requires compatible identity and a fresh process generation/fresh admission; trace replay is diagnostic.
- **Actual gap:** New execution binding/replay evidence must follow same rule.
- **Required change:** Persist durable identity/policy/evidence only; after restart create fresh generation-bound authority and fresh external admission. Never serialize ephemeral capability/binding as authority.
- **Authoritative owner:** Existing admission/identity owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Process/runtime/provider restart; stale binding/evidence rejected after fresh generation.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
