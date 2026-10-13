# ADR-020 — Original child Close outcome and own quarantine consumption

Accepted 2026-10-10 for actual executable child Close → root teardown →
RuntimeKernel.TerminateProcess. The original-child-close repro invokes native
Close, loses its reply, observes native Closed/OriginalClosure, then fails local
retry with Region Owned and charge8192. Existing guards prohibit effect repeat
but have no producing-owner resolution consumer.

Add optional V1 original child Close/request/lookup. Request names the same
original native Create receipt by reference, with existing operation identity.
Native parent→child source checks and operation reservation precede possible
CPU Close. Original request, captured backing and raw result survive callbacks;
canonical pure lookup returns only that original outcome. Independent live
mapping/I/O users refuse effect. Exact pre-effect no-effect is distinct from
terminal no-further-use; unknown/absent history cannot repeat Close. Legacy
callers preserve V1 semantics and cannot repeat an unknown original effect.

Adapter/provider/bridge retain their original invocation before effect. Optional
pure resolution consumes surviving native closure for cleanup, never execution
admission. Only this invocation's own fault quarantine can be consumed, using
same original sources and lifecycle revisions; independent mutation, parent
revoke, exhausted revisions and unknown history preserve pins. Known canonical
outcome before late source loss permits lawful retry without repeated effect or
perpetual re-pin after exact consumption. Root dependent inventory continues to
require exact child closure before original parent Close/local reclaim.

No new authority/ledger, enum/V2/schema or core ISA/architecture change. Actual
package adoption and affected/main terminal verification are required. Gates
OFF; bounded single-host executable dev claim. This is surviving-original
reconciliation, not general B2 all-fact-loss containment or B1 durable recovery.
