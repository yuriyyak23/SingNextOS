# Practical Implementation Order

This order is optimized for mergeability, evidence and rollback rather than document numbering.

## Wave 0 — Freeze and architecture guards

1. Refresh exact SHA/package/toolchain tuple in CI artifacts.
2. Add/strengthen architecture tests banning duplicate authority owners and forbidden authority fields in cross-project contracts.
3. Land claim-level/gate registry with all new gates OFF.
4. Add semantic extension canonicalization test harness.

## Wave 1 — Contract substrate

5. `OperationSemanticExtensionsV1` / `ExecutionGuaranteeExtensionsV1` / binding extension digest.
6. Unknown mandatory/optional behavior and canonical hashing.
7. `SemanticTraceEventV1` schema and offline trace recorder.
8. Minimal P05 refinement library skeleton.

## Wave 2 — Core correctness vertical

9. P01 staged/exclusive `MemorySemanticsV1`.
10. Memory litmus/reference tests and visibility/publication trace points.
11. P04 `DmaExecutionBindingV1` generation vector (only fields needed by selected provider).
12. Final dependency revalidation before external submit.
13. Provider invalidation/reset/containment evidence for selected adapter.
14. P05 memory+DMA refinement rules.
15. QV1 end-to-end adapter path.
16. QV1 negative/race/fault/differential suite.
17. QV1 qualification artifact and optional gate enablement for exact tuple.

## Wave 3 — Resource/time and failure closure

18. P03 temporal/resource vocabulary; no hard RT.
19. P10 provider-loss/quarantine/subrange failure consequences.
20. P08 basic preemption/restart semantics for one provider.
21. Stateful resume only after separate evidence.

## Wave 4 — Optional single-host contours

22. P02 staged durability on one named provider.
23. P09 trust evidence on one named secure-device producer.
24. P07 advisory locality/data-motion planning.
25. P12 energy measurement/accounting; enforcement only if provider supports it.
26. P06 protected-contour IFC if a concrete SecureCompute use case needs it.
27. PCL prototype only after profiling shows repeated validation cost worth optimizing.

## Wave 5 — Multi-host research/implementation

28. P11 formal lease/partition model.
29. Simulation/fault harness.
30. Narrow delegated resource pilot.
31. Ownership transfer only if a real use case requires it.
32. Real fabric/hardware qualification last.

## Stop conditions

Stop feature expansion and repair the core if any of the following appears:

- a new semantic requires a second authority ledger for an already-owned fact;
- QV1 cannot preserve independent SingNext/provider/refinement/HybridCPU gates;
- a stale snapshot is used as permission after mutable generation drift;
- an external effect can be forgotten on rollback/provider loss;
- a stronger claim has no executable evidence for the exact tuple;
- an ISA proposal is made without first exhausting runtime/sideband/provider solutions.
