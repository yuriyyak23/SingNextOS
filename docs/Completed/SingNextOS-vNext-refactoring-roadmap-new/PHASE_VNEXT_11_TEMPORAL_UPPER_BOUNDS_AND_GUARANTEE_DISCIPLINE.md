# P11 — TEMPORAL UPPER BOUNDS AND GUARANTEE DISCIPLINE

## Purpose

Implement temporal semantics only for contours that can honestly measure/enforce them; keep guaranteed minimum capacity and hard realtime separate and OFF unless independently proven.

## Preconditions

- P10 closed.
- At least one measurement/enforcement contour identified.

## Architectural decisions

- Define one replenishment policy first; do not support ambiguous multiple policies.
- For each contour document measurement source, trust, clock, preemption granularity, worst-case non-preemptible interval, overrun behavior, restart semantics and SMT/multicore effects.
- `budget/period` may be used only where arithmetic and enforcement owner are exact.
- If provider cannot enforce hard maximum, claim remains accounting/runtime reservation only.
- `GuaranteedReservation` requires proof of minimum capacity under contention/provider loss; otherwise gate remains OFF.

## State / linearization model

Time state is owned by the budget/lease subsystem plus a qualified measurement/enforcement source. Provider reports are evidence; replenishment credits are issued only by local authoritative logic.

## Negative-space obligations

- timer wrap/clock drift;
- replenishment replay/double credit;
- non-preemptible overrun;
- SMT sibling contention invalidates guarantee;
- provider loss during period;
- burst/period multiplication overflow;
- deadline hint treated as guarantee.

## Required executable tests

- No-double-credit replenishment race.
- Overrun/throttle/deny behavior.
- Worst-case non-preemptible contour test.
- Clock/time-provider restart tests.
- SMT/multicore contention qualification for any stronger claim.
- Negative test: deadline metadata alone never enables guarantee gate.

## Expected code / contract owners

- `ResourceBudgetAuthority` temporal extension if needed
- qualified runtime/provider measurement adapter
- scheduler only as policy input

## Claim boundary

`EnforcedUpperBound`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- At least one exact contour has executable upper-bound evidence or gate remains OFF.
- Guaranteed reservation remains independently OFF unless separately qualified.

## Prerequisite for next phase

P12 must preserve these checks/transitions in fused execution.

## Current disposition (2026-09-21)

Closed as an explicit FutureGated boundary recorded in `EVIDENCE_P11_TEMPORAL_CLAIM_BOUNDARY.md` and `P11_QUALIFICATION_TUPLE.json`. Audit found accounting and deadline evidence but no exact measurement/preemption/replenishment enforcement contour, so neither temporal upper-bound nor guaranteed-reservation gate is enabled and no `EnforcedUpperBound` claim is made. P12 must preserve the existing accounting/admission transitions but cannot infer temporal enforcement from them.

## Sequential re-audit (2026-09-22)

Current HEAD is `800027893dcf1ed23d8d7fe775841dd9d01a9fff`. The absence of an executable temporal owner/adapter was reconfirmed; no stronger claim is inferred from accounting or deadlines. Current evidence is `P11_REAUDIT_20260922.md` with tuple `P11_REAUDIT_20260922_TUPLE.json`. Both gates remain OFF.
