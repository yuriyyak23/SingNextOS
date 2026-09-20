# P10 — RESOURCE SCHEDULER AND PROVIDER AGENTS

## Purpose

Add placement/fairness policy outside the authority core. Provider agents may own provider-local bindings and observations but never local capability/budget/publication truth.

## Preconditions

- P09 closed for at least host/model; HybridCPU contour optional/gated.

## Architectural decisions

- Scheduler input = intent + non-authoritative evidence + validated availability projections.
- Scheduler output = placement decision/hint only.
- Agents may cache queue depth/load/qualified semantic classes/provider generation.
- Before execution, P04/P07/P09 owners revalidate live state.
- Fairness schedules already-admitted work; it does not mint capacity or grant.

## State / linearization model

Policy/cache state may be replicated. Authority state remains singular in existing owners.

## Negative-space obligations

- cache says authorized after revoke;
- agent restart loses binding evidence;
- two agents believe they own same local lease;
- priority laundering through scheduler hint;
- provider load evidence stale or adversarial.

## Required executable tests

- Poisoned cache cannot bypass live owner validation.
- Agent restart/generation drift revalidation.
- Fairness policy cannot exceed admitted lease.
- Scheduler output serialization contains no authority token.
- Contention/performance tests separate policy overhead from admission.

## Expected code / contract owners

- ResourceScheduler/PlacementPolicy
- provider-agent adapters/caches
- existing owners unchanged

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Scheduler has no mint/settle/release API.
- Cache cannot store authoritative booleans.

## Prerequisite for next phase

P11 may use scheduler/provider execution facts only as inputs to qualified upper-bound enforcement.

## Current disposition (2026-09-21)

Closed for the internal host/JIT policy-only contour recorded in `EVIDENCE_P10_RESOURCE_SCHEDULER.md` and `P10_QUALIFICATION_TUPLE.json`. Scheduler observations and placement hints are generation-bound evidence only; restart clears them, live provider revalidation is mandatory, and the surface has no authority or terminal-mutation API. `FG-VNX-RESOURCE-SCHEDULER` remains OFF, ordinary planning remains default, and no upper-bound, guarantee, provider-wide or production claim is made.
