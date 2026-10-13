# ADR-038 — Original parent-device refusal and no-effect consumption

Accepted 2026-10-11 for the actual private backing client. A native readiness or
operation refusal could leave a native device loan/quantum reservation pinned
without an accepted backing intent. Missing receipt/query remains unknown.

The existing external root/device admission owner now records an original request
before possible allocation. Canonical root factory, history, known rights/version
and capacity must be valid. Readiness/in-flight/operation refusal records explicit
NoEffect and NoFurtherAdmission under the root gate, without allocating backing.
The original correlation stays latched after readiness recovery; neither a retry
nor equal copy can allocate that lifetime. Invalid source/history, unknown semantics
and inability to retain the latch remain unknown/pinned. Pure lookup returns the
same retained no-effect object, never an inference from absent backing.

SDK actual cleanup retains this optional raw before callbacks, checks same original
request/query identity and fresh generation/revision, and supplies scoped no-further
external use to native loan retirement. NoEffect remains distinct from containment;
native containment is not invoked for proven refusal. Original provider/bridge/kernel
consumption returns the private payload reservation through existing budget commit.
This completed fact requires no new operation identity/active permission; independent
child/root/event/users still need their own legal cleanup. Missing/malformed/query
loss preserves pins; pure retry cannot repeat Bind. V1 allocation/closure consumers
remain compatible; accepted backing still uses ADR-037 containment.

Actual adoption: Contracts1.29.0 / ExternalRuntime1.21.0; operationSchema1.4.0 and
manifestContract1.3 unchanged. Claim: in-process software original refusal and
quantitative cleanup, not cold/durable/all-fact-loss or hardware qualification.
Gates OFF; ProductionQualified false; Java-only skipped; ISA NONE.
