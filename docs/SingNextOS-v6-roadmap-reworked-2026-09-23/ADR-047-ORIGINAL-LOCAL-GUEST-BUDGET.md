# ADR-047: Original model-only guest quantitative lifetime

Accepted for implementation, 2026-10-11. Actual model-only MapGuestRegion also pins a Region and admits guest memory. Provider-backed mapping accounting does not cover this caller.

VDA owns one canonical local guest lifetime per original immutable mapping tuple, linked to its same original domain record. ResourceBudgetAuthority remains the only quantitative owner. Before Region reservation, an attached process reserves GuestMemoryBytes and PinnedMappedMemoryBytes equal to the guest range in one original GuestMemory reservation. Unbudgeted compatibility remains supported. Shortage refuses before Region effect. The lifetime retains the same reservation/charge owner before possible budget mutation, including exceptions; unknown admission cannot repeat.

Existing process/capability/source/lineage admission surrounds local effects and final publication. Publication additionally requires the original live budget attachment, charge owner and exact Region reservation. No provider, caller or notification callback runs under VDA/budget owner commit locks. Existing lock order is platform memory use → VDA → budget or Region; no new transaction manager.

Cleanup withdraws mapping publication before either irreversible local consumption. Exact Region release precedes original charge consumption; the original lifetime records each completed step. Failed charge consumption retains the mapping as cleanup-only and retains quantitative usage. Retry does not release the Region again. Original owner/charge/history loss and unknown invocation deny and stay pinned. Completed exact charge release does not require another active permission or reservation. Warm restoration of the same authoritative object is not cold recovery.

Explicit Close and process teardown consume this lifetime, then remove VDA mapping inventory. Another mapping/Region stays alive. A failed second reservation on an already reserved Region returns only its own admitted charge and cannot release the first mapping's reservation. Immutable mapping DTO equality is correlation, not reference continuity; canonical lifetime and quantitative record identity supply the original fact.

Internal implementation only; no package/API/schema/ISA delta. [Actual consumer evidence](../../artifacts/v6/iteration-20261011-local-guest-budget/report.md). Gates OFF; ProductionQualified false; Java-only skipped; ISA NONE. B1 trusted cold continuation, general B2/B3 history/independent users, and typed actual CPU trap refinement remain open.
