# ADR-025 — Retire prepared original parent Bind at its producing owner

Accepted 2026-10-10. Actual RuntimeKernel pre-invocation source refusal consumed
caller no-effect and reclaimed backing while the same surviving native prepared
request could still invoke Bind afterward. Caller never-invoked is a component
fact, not retirement of a provider-prepared request.

Add optional V1 original Bind retirement, separate from legacy and original Bind
interfaces. The same native admission owner atomically races retirement against
Bind invocation. Retired-before-invocation returns canonical exact no-effect and
prevents all future original/legacy effects for that request/operation. Already
invoked known original outcome is lookup-only and still needs existing parent
Close; in-flight/unknown/foreign/copy/history-loss remain pinned. No repeated effect
or synthetic Closed/admission is permitted.

Preparation reserves existing operation correlation before factory/backend effects;
native original fact owners and routing reference the same objects. Lost routing
cannot reconstruct a new fact from the same request identity. Adapter retains the
retirement raw before query/callback, validates canonical continuity, consumes only
its never-invoked child component, and forwards exact parent no-effect or known
admission into existing cleanup. Incapable retirement providers keep prepared pins.

This closes a surviving-original local producer/caller boundary, not B1 durable
recovery or general unknown/all-fact-loss containment. Existing owners, authority,
V1 semantics, ISA/core architecture and operation schema remain unchanged. Gates
OFF; package adoption and terminal native/affected/main evidence required.
