# P17 — Qualify one complete MatrixMultiply staged-output co-design contour

## 1. Goal
Qualify one complete MatrixMultiply staged-output co-design contour.

## 2. Rationale / live gap
- current ComputeOperationKind lacks MatrixMultiply.
- staged output is safest current path; direct coherent remains gated.

## 3. Preconditions / dependencies
- Dependency: **P16**.
- Previous phase must not be BLOCKED for this contour.
- Exact source/package tuple must match P00 baseline or P00 is rerun.

## 4. Authoritative owners touched / explicitly unchanged
Touched only as required by existing responsibilities. No task transfers truth between `CapabilityAuthority`, `ResourceBudgetAuthority`, `RegionAuthority`, session/invocation owner, `ExternalOperationAuthority`, provider, HybridCPU runtime legality, or publication owner. Planner/scheduler remain non-authoritative.

## 5. VERIFIED_EXISTING code/files/tests
- **VERIFIED_EXISTING:** `contracts/SingPlus.Contracts/ComputePlanning.cs`
- **VERIFIED_EXISTING:** `src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs`
- **VERIFIED_EXISTING:** `src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs`
- **VERIFIED_EXISTING:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdapterContracts.cs`
- **VERIFIED_EXISTING:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`
- **VERIFIED_EXISTING:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`

## 6. NEW_PROPOSED surfaces
- **NEW_PROPOSED:** `MatrixMultiply semantic effect`
- **NEW_PROPOSED:** `Matrix execution class`
- **NEW_PROPOSED:** `cross-repo guarantee provider`

## 7. Normative semantics / invariants
- Preserve VNX-001..028 and CD-001..024.
- Every new descriptor/evidence object is non-authoritative unless it is an explicit extension of an existing owner state machine.
- Unknown versions/generations/classes fail closed.

## 8. State transitions / generations / lifetime
All transitions are generation-exact. New immutable semantic objects are valid only for the exact operation/provider/contract generations captured at construction. They are not refreshable in place after drift; a fresh prepare/bind is required.

## 9. Linearization points
Use existing owner linearization points. Cross-owner orchestration linearizes only at the contour-specific commit (for submit, the existing single submit-start/ExternalOperation submission path); pure refinement/snapshot construction is not a commit.

## 10. Concurrency / race handling
Final revalidation immediately precedes irreversible commit. Stale generation, revoke, close, restart or conflicting owner mutation wins by causing reject/stale/quarantine according to whether submit may already have occurred.

## 11. Failure / liveness / quarantine
Pre-submit reversible failures compensate. Post-possible-submit ambiguity never implies refund/reclaim. Quarantine requires a declared reconciliation/containment/terminal policy and, when possible, a bound retry/escalation strategy.

## 12. API / contract delta / compatibility
Changes are additive and versioned. Existing Level-1 paths remain available behind gates until the new contour is qualified. Mandatory obligations must never be silently downgraded to fit an older provider.

## 13. HybridCPU impact
See task-level TZ. Default rule: use current ExternalRuntime/runtime-legality seams first; HybridCPU additions are additive provider-neutral contracts or evidence hooks only when the phase requires them.

## 14. ISA impact
**NONE.** An ISA change is a blocker/rejected branch, not an implementation shortcut.

## 15. Implementation tasks
- `CD-P17-01` — add MatrixMultiply semantic contract and Region operands A/B/C
- `CD-P17-02` — map obligations to HybridCPU guarantees and exact binding
- `CD-P17-03` — execute through runtime legality + staged publication path
- `CD-P17-04` — collect resource/visibility/publication/settlement evidence

## 16. Tests
Each task adds exact positive and negative tests. Minimum phase regression includes stale generation, duplicate/reordered evidence, forbidden authority conversion, and gate-OFF fallback. Existing relevant tests remain green.

## 17. Formal/model obligations
- model-specific end-to-end state exploration.

## 18. Adversarial cases
Select all applicable rows from `09_ADVERSARIAL_MATRIX.md`; no phase may claim closure while an applicable row lacks an executable or formal disposition.

## 19. Performance / manycore scalability budget
Record admission latency, allocations, lock hold/wait time and throughput delta for touched hot paths. Do not add a global lock to implement semantic refinement/binding. Establish baseline before optimization.

## 20. Migration / rollback
Gate new behavior OFF by default. Rollback stops new admissions under the new contract but preserves the contract version/lifecycle of already-submitted operations until terminal reconciliation.

## 21. Deliverables / evidence
- code/contracts/tests for all tasks;
- phase evidence file with exact source/package/runtime tuple;
- traceability rows requirement -> task -> symbol -> test -> evidence;
- explicit unsupported contours.

## 22. Exit criteria / verdict gate
`CLOSED` only when all tasks/tests/evidence are satisfied on the exact tuple. `CLOSED_WITH_CORRECTIONS` requires documented non-semantic corrections only. Any missing prerequisite or unenforceable mandatory guarantee => `BLOCKED` or `REDESIGN_REQUIRED`; duplicate-owner proposal => `REMOVE_OR_MERGE`.

## 23. Explicit non-goals / negative space
- no new capability/budget/Region/universal authority ledger;
- no provider receipt/evidence as authority;
- no planner/scheduler authorization;
- no SingNext handles, capabilities, lane/slot/NUMA/queue IDs in ISA/application authority API;
- no ISA change;
- no `Complete => Visible => Published` shortcut;
- no automatic refund/reclaim from provider loss or cancellation request.
