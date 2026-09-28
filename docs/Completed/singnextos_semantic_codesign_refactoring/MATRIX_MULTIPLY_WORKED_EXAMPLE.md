# MatrixMultiply worked example — corrected staged contour

This is the first implementation vertical; it reuses existing HybridCPU MatrixTile/L7 execution support and changes no ISA.

1. **App/SIP** requests MatrixMultiply(A,B→C). SingNext adds a new semantic operation because current `ComputeOperationKind` has only Copy/Transform/Reduce.
2. **Capability/effect authority** independently validates semantic permission. OperationObligations does not grant it.
3. **RegionAuthority** acquires A/B read uses and C staged-write use, binding Region generation + MutationEpoch.
4. **Resource permission/budget** validates resource-use constraint and atomically reserves the required `BudgetAmount` vector in the existing `ResourceBudgetAuthority` ledger. Resource permission is not effect permission.
5. **EndpointSession/invocation** exact generation remains live.
6. **ComputePlanner** selects candidate/provider; it cannot authorize/refine mismatch.
7. **OperationObligationsV1** is composed from exact owner snapshots: Matrix semantic effect, A/B/C data use, resource envelopes, staged visibility/publication, replay/determinism/numeric requirements, cancellation/containment as required.
8. **Provider admission** returns exact provider/runtime generation and `ExecutionGuaranteesV1`. For baseline 1.14 compatibility, absent resource/preemption/containment guarantees remain Unsupported.
9. **Typed refinement** evaluates semantic partial orders. Mandatory Unsupported or mismatch rejects.
10. **SemanticExecutionBinding** freezes ExternalOperation generation, provider generation, contracts, obligation/guarantee ids, resource/measurement/visibility/publication/replay context.
11. **Final SingNext sentry** re-resolves capabilities, Region/session, exact live budget lease and provider generation.
12. **HybridCPU runtime legality** remains independent; `LegalityDecision` must allow current materialized execution. Compiler typed-slot facts are only ValidationOnly evidence.
13. **Execute** uses existing MatrixTile/L7 provider path. No new instruction or architectural register is introduced.
14. **Retire/usage evidence** remains CPU/provider evidence. Resource charging follows P09 per-class measurement, not “retired work == consumption”.
15. **ProviderComplete** does not imply visibility. Staged output advances only with exact completion then visibility evidence.
16. **SingNext publication owner** makes the publication decision. Adapter invokes exact provider staged commit/publication action; HybridCPU local publication gate validates its own staged preconditions but never owns OS policy.
17. **Published** is recorded by ExternalOperationAuthority only after exact provider receipt. C Region publication semantics are then satisfied.
18. **Settlement** accepts exact bound usage evidence; actual <= reserved. Duplicate exact evidence is idempotent; mismatched receipt is rejected/quarantined.
19. **Release/reclaim** follows existing Region/ExternalOperation/Budget owners and requires provider closure/containment where relevant.

## First-contour claim limits

- Staged output only.
- Numeric provider conformance must be explicit.
- DeviceMemory/DMA/ComputeTime are not promoted above the actual measured/enforced claim level.
- No generic bounded preemption or containment claim unless the selected provider runtime implements and tests it.
- No direct-coherent publication claim.
