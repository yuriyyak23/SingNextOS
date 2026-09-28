# ТЗ P00 — Freeze exact live baseline and evidence tuple

    **Depends on:** none  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P00-01 — Refresh exact SHAs/tree SHAs

- **Phase:** P00
- **Classification:** `STALE_OR_INCORRECT`
- **Repository:** SingNextOS
- **Exact current paths:** `global.json; Directory.Build.props; .packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg; HybridCPU_ExternalRuntime.Contracts/HybridCPU_ExternalRuntime.Contracts.csproj; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`
- **Exact current symbols:** `SingNext SDK 11.0.100-rc.1.26425.128; HybridCPU SDK 10.0.201; HybridCPU.ExternalRuntime.Contracts 1.14.0; ExternalOperationContract.Version 1.4.0`
- **Existing tests/evidence:** current executable result: NOT RUN in this audit environment; only test existence/static inspection is claimed
- **Current live behavior:** Current roadmap package freezes SingNextOS at 1890a8e… while live master is cb6a94c…. HybridCPU baseline remains 794c4a5….
- **Actual gap:** Baseline drift invalidates source-bound claims.
- **Required change:** Replace every old SingNext SHA in this package with the current freeze; retain historical SHA only in correction log.
- **Authoritative owner:** Roadmap/evidence owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Manifest consistency check: no normative file may claim the superseded SHA.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** none
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P00-02 — Recompute package/DLL hashes and lockfile identity at implementation start

- **Phase:** P00
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `global.json; Directory.Build.props; .packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg; HybridCPU_ExternalRuntime.Contracts/HybridCPU_ExternalRuntime.Contracts.csproj; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`
- **Exact current symbols:** `SingNext SDK 11.0.100-rc.1.26425.128; HybridCPU SDK 10.0.201; HybridCPU.ExternalRuntime.Contracts 1.14.0; ExternalOperationContract.Version 1.4.0`
- **Existing tests/evidence:** current executable result: NOT RUN in this audit environment; only test existence/static inspection is claimed
- **Current live behavior:** Tracked package and recorded SHA-256 exist; Git tree confirms Contracts nupkg blob 2614d43f… and Runtime nupkg blob d528fd…; current recorded SHA-256 values are source evidence, not recomputed here.
- **Actual gap:** Artifact bytes were not executable/materialized in this environment.
- **Required change:** At implementation/qualification, recompute SHA-256 from tracked nupkg/DLL and compare to lockfile/content hash; mismatch under same SemVer is a new artifact.
- **Authoritative owner:** Supply-chain/qualification owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Locked restore plus digest equality. Any mismatch fails qualification.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** none
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P00-03 — Inventory tests/contracts and record test-evidence level

- **Phase:** P00
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `global.json; Directory.Build.props; .packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg; HybridCPU_ExternalRuntime.Contracts/HybridCPU_ExternalRuntime.Contracts.csproj; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`
- **Exact current symbols:** `SingNext SDK 11.0.100-rc.1.26425.128; HybridCPU SDK 10.0.201; HybridCPU.ExternalRuntime.Contracts 1.14.0; ExternalOperationContract.Version 1.4.0`
- **Existing tests/evidence:** current executable result: NOT RUN in this audit environment; only test existence/static inspection is claimed
- **Current live behavior:** Relevant tests exist in both repositories; no current test execution was performed here.
- **Actual gap:** Existing documents blur test existence, recorded evidence and current executable result.
- **Required change:** Every acceptance row carries one of: test exists / statically inspected / recorded evidence / current executable result.
- **Authoritative owner:** Qualification owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Evidence schema validation and exact source tuple binding.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** none
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
