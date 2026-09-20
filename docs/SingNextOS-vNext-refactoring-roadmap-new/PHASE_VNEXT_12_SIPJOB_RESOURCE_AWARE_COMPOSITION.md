# P12 — SIPJOB RESOURCE AWARE COMPOSITION

## Purpose

Compose resources with SipJob strictly as an execution optimization over ordinary SIP. Fusion may remove transport/materialization work, never authoritative transitions.

## Preconditions

- P11 closed for the enabled resource contour.
- Completed SipJob ordinary semantic oracle remains green.

## Architectural decisions

- Each fused stage resolves the same live effect/resource/session/Region owners as ordinary SIP.
- External effects, resource consumptive commits, settlement, publication and unsafe ownership transitions remain fusion barriers unless differential proof shows identical authoritative trace.
- Parallel branches receive atomically split quantitative leases/grants before execution; no shared indivisible lease race.
- Async persists opaque handles/correlation only and revalidates on resume.

## State / linearization model

Authoritative state machines remain those from ordinary SIP. SipJob stores plan/cache metadata only.

## Negative-space obligations

- nested donation in fused call;
- parallel branch double spend;
- branch cancellation/double refund;
- provider ambiguity mid-job;
- visibility/publication delay;
- stale cached fused plan;
- async resume after session/provider restart.

## Required executable tests

- Differential traces ordinary SIP vs fused for reserve/bind/consume/settle/publication.
- Parallel branch conservation race.
- Fusion barrier tests around external effects/publication.
- Async restart/resume stale handling.
- Provider-loss traces identical in authoritative outcomes.

## Expected code / contract owners

- existing SipJob planner/executor/barrier planner
- generated sentry/runtime owners reused
- no new resource owner

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Fused and ordinary authoritative traces are equivalent for enabled contours.
- SipJob has no authority/budget/provider ownership APIs.

## Prerequisite for next phase

P13 generalizes only after the single-class ordinary and fused contours are stable.
