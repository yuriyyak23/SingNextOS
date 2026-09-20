# P06 — ENDPOINTSESSION RESOURCE DONATION

## Purpose

Allow request-scoped resource permission/capacity delegation across `client -> server -> downstream` without budget, priority, assurance, provider-scope or refund laundering.

## Preconditions

- P05 closed.
- EndpointSession/invocation generation semantics verified.

## Architectural decisions

- Donation is an invocation-scoped binding to a narrowed resource-use grant plus budget lineage/lease source; it is not ambient server state.
- The server may consume donated capacity only through the exact invocation sentry/runtime projection.
- Nested calls derive a narrower grant and either transfer/split a lease or reserve from the same charging lineage exactly once.
- Server-owned fallback capacity, if allowed, is explicit policy with separate accounting and cannot be silently attributed to the client.
- Priority/assurance/provider scope can only narrow.

## State / linearization model

```text
InvocationCreated
 -> DonationBound
 -> Active
 -> Returned/Settled
 -> Closed
```

Session or target-generation change makes the donation stale. Donation closure does not itself release an in-flight provider-bound lease.

## Negative-space obligations

- double delegation to two downstream services;
- nested call double charging;
- double refund on cancel + downstream failure;
- server restart/session ABA;
- priority/assurance/provider widening;
- donation stored globally and reused later;
- cancellation before and after provider submit.

## Required executable tests

- Property chain client->server->downstream with monotonic narrowing.
- Parallel downstream split with conservation proof.
- Session restart/ABA stale rejection.
- Cancel-before-submit full release and cancel-after-submit quarantine behavior.
- Attempted priority/assurance/provider laundering negatives.
- Exactly-once charging/refund trace.

## Expected code / contract owners

- `EndpointSessionRegistry` / `EndpointSessionInvocationRegistry`
- existing `CapabilityAuthority` derivation
- `ResourceBudgetAuthority` split/reserve/settle
- generated SIP sentry

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Donation is request-scoped and non-ambient.
- No amplification/double charge/double refund in nested and concurrent cases.

## Prerequisite for next phase

P07 may bind the resulting lease to an ExternalOperation only after donation provenance is exact.
