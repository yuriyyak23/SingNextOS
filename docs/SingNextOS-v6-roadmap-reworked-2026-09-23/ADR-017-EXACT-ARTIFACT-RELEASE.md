# ADR-017 — Exact admitted artifact release

Accepted 2026-10-10 for the actual ExternalRuntime CPU-backed artifact → executable
adapter → platform provider/bridge → guest-unmap/process teardown consumer. Existing
ChildRecord/CpuBackedChildExecution retain the admitted artifact and backing runner;
Bind/Start receipts do not release this lifetime. Existing pre-unmap pins cannot be
discharged without a producing no-further-use guarantee.

An optional additive release contract carries the exact original artifact admission
and an immutable release invocation before effect. Under the existing child owner
lock, the producer checks original artifact/child/mapping/parent/source continuity,
retains intent and joins the bounded execution owner. It publishes exact no-further-
use evidence only when no runner can use the backing and future Start is refused.
No new machine instruction, legality rule, authority or ledger is introduced.
Unknown/exception/malformed outcomes retain pins and cannot repeat release. Pure
lookup addresses only a retained original outcome; absence proves nothing.

Adapter/provider/bridge retain the original request/raw response before callbacks,
validate the same canonical owner and sources for local consumption, and preserve
consumed facts independently of routing. Original child/mapping owners stay stable
across legitimate lifecycle mutations; equal-tuple copies do not inherit outcomes.
Callbacks and external joins run outside unrelated adapter/provider/kernel gates.
Completed exact release needs no fresh active execution permission; unfinished
cleanup still needs applicable original sources. Admission, execution completion,
artifact release, mapping closure and Region/budget reclaim remain distinct.

Only exact release permits the existing guest unmap and teardown consumers to move
forward. Incapable providers keep current pins. Artifact bind without original
outcome remains unresolved. Versioned sibling packages must be built, adopted and
verified on actual SingNextOS assets; sibling source alone is not adoption.

Gates OFF; ISA NONE; bounded single-host executable dev claim only. Durable routing,
all-fact-loss containment, stateful resume, physical and ProductionQualified claims
remain separate. V1 semantics preserved; optional additive surfaces only.

## Native original history extension — 2026-10-11

The existing child owner keeps a release issued marker and self-identifying fact,
with captured original Bind, artifact, child/mapping admissions and parent sources.
Intent/marker precede the bounded backing join. The joined raw outcome stays on
the original fact before final source validation; missing/copied history does not
permit another Release or Start. Pure lookup requires the same request object and
same producing factory sources, without fresh active execution permission.
Both native legacy and original guest Unmap consume only this exact release fact.
An equal fact copy is not no-further-use evidence. The actual adapter also checks
native receipt request identity before local consumption. Runtime1.16.3 adoption
is recorded in the native-release-history report; the extension does not establish
cold durability, all-fact-loss containment or independent backing-user closure.
