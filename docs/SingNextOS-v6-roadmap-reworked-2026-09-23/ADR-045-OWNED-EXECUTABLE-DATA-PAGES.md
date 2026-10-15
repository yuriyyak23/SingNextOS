# ADR-045: Owned private executable data pages

Accepted for implementation, 2026-10-11. The actual external CpuBackedChildExecution materialization previously used sparse memory whose data pages were detached by dropping references. Artifact NoFurtherUse must retire its original private data owner after terminal execution, including retained references, rather than infer reclamation from absent metadata.

Use an external Processor.MainMemoryArea implementation in HybridCPU_ExternalRuntime. Its synchronized authoritative page dictionary owns allocated data bytes. A write checks all missing pages against its capacity before allocation or mutation; allocation failure retains any owned prefix for cleanup. Reads of unallocated live pages remain zero. Range, word endian and existing 768 MiB default extent remain compatible. This does not change ISE machine legality, opcode, register, memory model, pipeline or retire semantics.

The execution owner is retained in the original Bind fact before loader effects. Close waits for the same runner's terminal result; an unreceipted runner exception continues to refuse closure. Only then it retires the original memory, clears owned pages, denies subsequent reads/writes and drops references. Independent owners are untouched. Existing original artifact/child/guest/root history and release gates still govern whether this Close may be consumed. No separate lifecycle ledger or permission owner is introduced.

Runtime1.23.5 is adopted by actual SingNextOS package references and integrity checks. Contracts1.31.0, manifestContract1.3 and operationSchema1.4.0 are unchanged. Existing exact kernel nested Start/Terminate paths now observe the retained native memory: exact cleanup retires pages to zero with Region/attached logical charges; uncertain history/source paths retain data pages and charges.

This establishes private data inventory/retirement and the compatible default capacity, not service admission for a smaller quota. GuestMemoryBytes remains logical mapping cost. Next: additive producer quota carried from pre-effect ResourceBudgetAuthority reservation through native materialization and its exact release consumption. Dictionary/annotation/CPU/CLR overhead and physical host reclamation are outside the data-page measure. Trusted cold continuation, general unknown/all-fact-loss and independent users remain open. Gates OFF; ProductionQualified false; Java-only skipped; ISA NONE.

Evidence: [consumer record](../../artifacts/v6/iteration-20261011-private-data-retirement/report.md).
