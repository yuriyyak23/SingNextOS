# ADR-010 — Explicit child Start pre-invocation refusal

Accepted for the bounded managed Start capacity consumer, 2026-10-06.

Original defect motivating this implemented decision: the executable adapter refuses exhausted operation capacity before trace,
pending mutation and provider invocation, but neutral Denied maps to platform Denied.
PlatformAuthorityBridge conservatively fault-pins child/guest/parent dependencies for
every failure except NotAccepted. Local refusal therefore unnecessarily damages usable
dependencies. Platform cannot depend on the concrete executable adapter under the
existing DAG; neither type identity nor message text is an admission/closure contract.

Append NeutralVirtualizationStatus.NotInvoked=10, preserving V1 values0–9, result
signatures and contract versions. It asserts only that this invocation was refused
before any provider callback/possible external effect. In the initial slice, only the exact Start capacity
guard emits it in this slice. Translate it to the existing platform NotAccepted;
arbitrary Denied, unknown status, provider denial after invocation, lost/malformed
receipt and exceptions retain conservative ambiguity. No blanket V2 or dependency edge.

This is an additive invocation disposition, not permission, containment, closure,
artifact release or reclaim evidence. Existing known receipts/dependency pins survive;
an earlier effect cannot be closed by a refused fresh invocation. Consumers that do
not recognize the new failure continue to fail closed. No fallback/downgrade bypasses
fresh owner admission or current provider/refinement/HybridCPU legality.

Required checks: actual adapter→platform→bridge zero/max refusal, balanced interlocks,
unchanged usable parent mapping/child and retained artifact pin; last generation,
accepted receipt/no-repeat, provider-denied/throw/lost receipt quarantine; generic
Denied and unknown neutral result remain conservative. Existing V1 paths plus focused
and stable shared broad. No serializer/security digest representation changed.

Gates OFF, bounded RuntimeEnforced only; no ProductionQualified. Java excluded,
ISA NONE. Artifact release and executable I/O closure remain separate external gaps.

Bounded cleanup extension, 2026-10-06: the exact child-close operation-capacity
guard also emits NotInvoked before Op/pending/provider callback. The existing
platform translation preserves it as NotAccepted. The bridge queries the live
source outside its lifecycle lock, then validates the exact original child/root,
ancestors and backend epoch before retaining Draining. Refusal produces no closure
receipt, releases no root pin and does not authorize reclaim. Reset/reentry must
not restore a changed owner; generic failure after invocation remains quarantined.
Known closure receipts still bypass invocation and use the existing publication
retry path. Transition and guest-unmap extensions are specified below.

Transition/guest-unmap extension, 2026-10-06: their exact capacity guards now emit
the same pre-invocation disposition. Transition captures the admitted state and
checks it before invocation and after fresh source observation on refusal. Unmap
rechecks dependent effects/artifacts after the source callback, before staging and
invocation. A refused unmap restores only its local Active→Draining stage after
fresh original owner/source checks; known receipts, faults and changed owners
cannot be rolled back. Source loss keeps the staged pin; it is not a closure fact
or permission to restore Active. No new API/schema/version/ledger. Generic failed
responses after invocation retain quarantine. Required checks include source query
throw/change/reset/reentry, last generation, original facts/pins and no repeated
effect, plus final focused and shared broad on stable inputs.

Artifact-bind extension, 2026-10-06: exact local identity and operation capacity
guards emit NotInvoked before allocation/pending/provider callback. Existing bind
NotAccepted consumer preserves child/guest facts and balances its interlocks;
refusal publishes no artifact. Earlier mappings/receipts/pins are not released.
Final usable identity/operation is reserved before callback; generic failed outcomes
after actual bind remain quarantined. Artifact release remains an independent gap.
No public API/schema/package/reference change; create/map extensions are specified below.

Create/map extension, 2026-10-06: their exact identity/operation capacity guards
also return NotInvoked. Create checks capacity before lazy root BindDomain, reserving
both bind/create operations before the first callback (one operation for an existing
root). Refusal neither discards that existing root fact nor alleges its closure.
Guest-map refusal leaves original child/parent backing intact; no mapping is published.
Existing NotAccepted consumers balance pending interlocks and retain prior facts.
Actual root/child/map invocation failure, missing or malformed receipts and generic
Denied/Unsupported/unknown responses still follow quarantine, independent of any
returned-object compensation. No API/schema/package/reference/gate/ISA change.

The adapter's exact pendingParentBinds interlock also returns NotInvoked for the
refused current invocation before any callback. The original pending root-bind
invocation remains tracked, not closed or permitted by this outcome. This prevents
a reentrant refusal from falsely quarantining the original bridge root. Existing
parallel root-create receipt/reset semantics are preserved; no blanket bridge
serialization or generic pending/Denied reinterpretation is introduced.
