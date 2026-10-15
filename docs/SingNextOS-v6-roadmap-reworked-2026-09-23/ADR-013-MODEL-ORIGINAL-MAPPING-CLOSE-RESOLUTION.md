# ADR-013 — Model original mapping Close resolution

Accepted for actual ModelOnly neutral mapping consumer, 2026-10-10; all gates OFF. Production unknown containment and durable reboot recovery remain open.

ADR-012 established original platform region request before Begin. Its actual HybridCPU producer calls neutral CloseOwnedRegionMapping. Losing that response after native closure leaves provider CloseUncertain; known-response resolution cannot discharge it. Current neutral mapping owner stores lifecycle only, which cannot correlate original invocation; Revoked/NotFound projection is not original closure.

Add optional INeutralOriginalMappingClosureProvider: immutable runtime incarnation, Close(original request) and Resolve(original request). Request binds invocation key, runtime incarnation and full mapping lease. Before effect the ModelOnly producer attaches a fact referencing same original mapping and parent owners; it retains exact result before returning. Resolve is observation only: exact same request/source/original owners and retained Closed/Revoked are required. Missing/foreign owner, changed source, empty key, unknown or malformed outcome return no closure. Never execute Close from Resolve or infer original closure from lifecycle.

The provider records neutral request before possible Close, retains raw resolution separately and revalidates source/owner after callbacks. Exact native closure can then reach ADR-012 completion and actual kernel Region/budget consumers. Unknown outcomes at incapable providers stay pinned/no-repeat. Source incarnation is correlation/fencing, not permission. Completed original closure does not require a new active parent permission.

Model facade must serialize lifecycle admission and mutation at its existing dependency owner: Close cannot race a late DMA bind that passed active validation. Public native operations are pure ModelOnly state operations, with no callbacks/sinks inside this gate. This provides a bounded model mapping/dependent lifetime guarantee; it does not establish general cross-owner/runtime atomicity or production DMA execution.

Required checks: actual native Close lost response and native resolution through HybridCPU/kernel, original/mismatched key/lease/incarnation, equal-tuple owner replacement, unknown original outcome, no repeat Close, late source/callback refusal, concurrent mapping Close/DMA admission, native model negative tests and affected main runtime broad. No ISA/package/enum/V2/persistent schema change. Optional contract uses actual local project references; no sibling NuGet claim.

B1 still lacks durable all-fact-loss producer/storage freshness and authenticated lifetime continuation. B2 remains external for providers without this exact optional contract and production execution. Model facts/query do not become durable authority or permit arbitrary reboot reclaim. Artifact release B3 stays independent.
