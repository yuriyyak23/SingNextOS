# ADR-043: Original guest mapping quantitative charge

Accepted for implementation, 2026-10-11. Actual RuntimeKernel child-backed MapGuestRegion reserved PinnedMappedMemoryBytes but never GuestMemoryBytes, despite an attached process budget containing that dimension. A guest range could exceed its configured guest budget before native Map.

For the child-backed contour, the existing dedicated original parent mapping reservation now includes both PinnedMappedMemoryBytes and GuestMemoryBytes, each equal to checked range.ByteLength. Reserve precedes Region/provider effects. ResourceBudgetAuthority remains the quantitative owner; existing process/domain/system admission applies. No profile maximum is charged as actual use, no duplicate reservation or ledger is introduced, and BudgetReservationLifetime.PlatformMapping stays bound to its authoritative original mapping owner.

The reservation measures admitted logical mapped guest bytes, not physical native private sparse allocation or an enforced physical footprint bound. Its complete cost vector is released only by existing exact guest/no-effect→parent closure finalization. Lost reply, history/source loss and partial cleanup retain that same original reservation until permissible consumption; retries do not reserve again. Exact no-effect can consume through existing rollback without declaring provider closure.

Model-only mappings and native private executable footprint remain separate open consumers. No new provider API, package, ISA or architecture change. Evidence: [actual kernel record](../../artifacts/v6/iteration-20261011-guest-quantitative-charge/report.md). Gates OFF; no ProductionQualified; Java-only skipped; ISA NONE.
