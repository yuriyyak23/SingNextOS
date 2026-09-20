# P03 — ATOMIC RESOURCE LEASE AND SETTLEMENT CORE

**Live disposition:** closed at `RuntimeEnforced` for the internal host/JIT budget-owner contour on reviewed HEAD `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`; the feature gate remains OFF. See `EVIDENCE_P03_RESOURCE_LEASE.md` and `P03_QUALIFICATION_TUPLE.json`.

## Purpose

Extend the existing `ResourceBudgetAuthority` into the sole quantitative owner for reservation, lease binding state, consumption settlement and quarantine, while preserving `reservations admit capacity but never authorize effects`.

## Preconditions

- P02 closed.
- Current `ResourceBudgetAuthority` reserve/release hierarchy tests green.

## Architectural decisions

- Reuse current account hierarchy and single ledger; additive state only.
- Introduce an opaque lease/reservation generation with explicit state machine; snapshots remain evidence.
- One linearization point for reserve/split/bind/settle/release.
- Before possible external consumption, release may restore unused capacity; after possible consumption, only settlement/reconciliation can close.
- Idempotent terminal settlement; duplicate release/receipt cannot create credit.
- Quantitative child split transfers/slices committed capacity atomically; no independent descendant counters.

## State / linearization model

```text
Prepared -> Reserved -> Bound -> Consuming -> Settling -> Released
                    \                                              -> CancelledPreSubmit      -> Quarantined -> Reconciled -> Settling
```

`CancelledPreSubmit` may fully release only if no irreversible/provider submit boundary was crossed.

## Negative-space obligations

- concurrent reserve of last unit;
- reserve vs release / retire process;
- split vs settle;
- double settle / double refund;
- owner process restart;
- ID/generation wrap or exhaustion;
- ledger underflow/overflow;
- stale lease replay.

## Required executable tests

- High-contention last-unit reservation race with exactly one admissible winner.
- Property test: used/reserved never exceeds every ancestor limit.
- Double settlement/refund idempotence.
- Process-retire blocked by live non-checkpoint leases.
- Overflow/underflow/wrap/exhaustion fail-closed tests.
- Crash-in-transition fault injection around state mutation.

## Expected code / contract owners

- `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`
- budget contract DTOs/snapshots, kept non-authoritative
- focused budget concurrency tests

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- One quantitative owner only.
- Conservation and no-double-spend proven under concurrency.
- Lease snapshots remain non-authoritative.

## Prerequisite for next phase

P04 can compose this lease with other owners only after all P03 races are closed.
