# ADR-033 — Original artifact Start attempt on actual callers

Accepted 2026-10-11, local provider/adapter Start consumer. ADR-010 V1 NotInvoked,
ADR-017 artifact release and ADR-018 Bind cleanup remain independent contracts.

Before possible native Start, the existing artifact owner retains a self-owned
attempt with the exact request, original child/mapping/artifact sources and native
request object. Issued history and latest identity are checked, including latest
being the tail of the same owner history. A consumed exact NotInvoked remains a
historical fact; only fresh admission permits another attempt. Invoked unknown
history permanently refuses repeated Start. Missing response does not prove no
native effect or artifact closure.

The adapter invokes native Start and feature queries outside its sync lock while
pendingChildStarts protects parent/mapping/artifact teardown. Native raw outcome
is retained in the same attempt before post-call source query. Fresh source,
child revision and original factory checks precede receipt publication. Explicitly
observed source-generation change is retained independently, so an ABA projection
cannot repair it. Exceptions/fault transitions target the captured original child,
never a newly routed equal child. Pending guards balance on every return/throw.

Existing exact artifact release supplies its own no-further-use guarantee. Start
no-effect does not release backing. Actual kernel cleanup may consume release,
guest unmap and child closure, then release Region and both OwnedMemory and
PinnedMappedMemory accounting. Independent backing-source/history pins remain.
A completed release does not require a new active Start permission.

No public/native API, package, schema, authority or ledger is added here. Native
V3 Start still lacks its own pre-effect original factory/attempt and pure original
resolution: that is the next concrete producer consumer, not an implemented B2
claim. No durable all-fact-loss/reboot claim. Gates OFF; ProductionQualified false;
Java-only skipped; ISA NONE.
