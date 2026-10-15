# ADR-018 — Original artifact Bind cleanup

Accepted 2026-10-10 for the actual ExternalRuntime artifact Bind → adapter →
provider/bridge guest unmap consumer. A lost Bind reply can leave an admitted CPU
artifact without its cleanup identity. Existing invocation pins cannot infer
absence or release from a missing receipt.

An optional additive pure resolution contract identifies the original request,
operation, captured package digest and producing child/mapping/parent owners.
The native child owner retains intent before possible backing creation and the
original receipt before return. Query returns only this surviving original fact;
equal tuples/copies, mutable request drift and foreign owners are refused.
Absence is unknown, never no-effect or containment. No repeat Bind is allowed for
an unresolved invocation. Current parent and mapping lifetime checks are atomic
with admission under existing parent→child locks.

Adapter/provider/bridge retain invocation and raw outcome on existing owners.
Resolution is cleanup-only: it cannot publish a refused admission or permit
Start. The original admitted artifact must pass ADR-017 exact no-further-use
release before guest closure and Region/budget reclaim. Independent source faults
and other unknown invocations retain pins. Callbacks/joins remain outside local
owner gates; final consumption checks same canonical owners and source generation.

No new authority/ledger, machine/ISA change, V2 schema or production admission.
All gates OFF. This is surviving-original reconciliation, not general B2
all-fact-loss containment or B1 durable recovery. Sibling packages require actual
SingNextOS adoption and consumer verification before an implemented exit.

The native Bind lookup must also retain and compare the exact child and mapping
admission objects captured by its producing fact. Equal admission copies do not
inherit a completed receipt. Original owner self identities and canonical routes
are mandatory, including after lawful closure; completed lookup does not renew
active permission. Runtime1.16.4 adopts these source checks. This completion fix
does not make parser refusal or absence an exact no-effect disposition.

## Original refusal disposition

The optional native disposition query carries the original request instance,
captured package digest, source version/generation and raw result. The existing
child owner retains self/issued invocation history before materialization. Only
inspection refusal before native kernel/memory creation proves NoEffect; loader
failure, exceptions, missing/copy history and generic Denied remain unknown/pinned.
The SDK retains raw disposition before callbacks and consumes it only after fresh
source/owner/revision checks, restoring only its own Bind quarantine. Provider and
bridge propagate this same-invocation no-effect cleanup without artifact publication,
Release invocation or resurrection of permission. Independent source/history users
stay pinned. Native history prevents effect replay while permitting a distinct
fresh admission after a proven refusal. Completed consumption never repins a lifetime.
Contracts1.25.0/Runtime1.17.2 adoption and bounded consequences belong to the native
Bind refusal report. Gates remain OFF; no general containment/durability claim.

## Failed private materialization backing

The optional disposition may also retain an exact CleanupArtifact for native
Bind that created private kernel/memory backing but failed before execution
publication. The existing Bind owner retains that backing before loader effect.
This artifact is cleanup-only: Raw remains a failed Bind, NoEffect is false,
legacy Bind completion lookup returns no successful admission, and Start refuses.
SDK retains this raw identity before callbacks and validates source/generation
and exact request before exposing it to the existing ADR-017 Release consumer.
Only exact original Release no-further-use permits Unmap and Region/budget reclaim;
missing/copy/history/source failures remain pinned. Closing the private owner
blocks further Start and drops its execution/memory/kernel references after join.
No physical allocator, cold recovery or general containment claim follows.
Contracts1.26.0/Runtime1.18.0 are additive; operationSchema1.4.0 is unchanged.


