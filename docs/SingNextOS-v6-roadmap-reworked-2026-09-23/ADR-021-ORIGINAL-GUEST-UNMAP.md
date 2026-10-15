# ADR-021 — Original guest Unmap producing continuity and cleanup

Accepted 2026-10-10. Actual guest Unmap repro closes native mapping, loses the
reply, and leaves Region Owned/charge8192 because no original producing response
can be consumed. Native lookup also lacks canonical original mapping identity.

Add optional V1 original Unmap request naming the same native Map admission
receipt and operation. Existing original mapping owner retains request/raw
outcome before publication; parent→child commit verifies original mapping,
child and parent, artifact release and backing users. Canonical pure lookup
never repeats Unmap and never derives closure from absence or Closed projection.
Exact no-effect is distinct from closure. Legacy semantics remain additive.

Adapter/provider/bridge retain original invocation before effect, raw before
callbacks, and consume only that invocation's own unchanged fault revision.
Independent mutation, source/history loss and malformed/foreign receipts pin.
Known original outcome permits cleanup without renewed execution permission;
Region/budget reclaim still requires composed child/root closure. No authority,
ledger, enum/V2/schema or HybridCPU-v2 ISA/architecture change. Gates OFF; bounded
executable dev only; B1 durable and general B2 all-fact-loss remain open.
