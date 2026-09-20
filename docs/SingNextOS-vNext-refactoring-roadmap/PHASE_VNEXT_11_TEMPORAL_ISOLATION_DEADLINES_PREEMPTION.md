# P11 — Temporal isolation, period/replenishment, deadlines and preemption

## Goal

Generalize the seL4 scheduling-context idea from CPU time to qualified heterogeneous execution classes.

## Temporal envelope

Add canonical semantics for:

```text
BudgetNs
PeriodNs
OptionalBurstNs
ValidFrom/ValidUntil
MaximumConcurrency
```

Do not use provider-specific cycles as a portable unit.

## Replenishment

Define one precise policy first (e.g. sporadic-server-like or fixed replenishment). Avoid supporting multiple ambiguous policies in v1.

Required properties:

- bounded maximum consumption per period;
- no replenishment duplication on restart/replay;
- monotonic child donation;
- deterministic accounting time source;
- overflow-safe time arithmetic.

## Preemption capability taxonomy

Provider describes evidence only:

```text
None
OperationBoundary
TileBoundary
InstructionBoundary
Checkpointable
```

This is not permission. Admission must compare remaining budget with worst-case non-preemptible contour where known.

## Deadline semantics

Deadline is a temporal constraint, not evidence that capacity is guaranteed. Distinguish:

```text
DeadlineHint
DeadlineAdmissionConstraint
GuaranteedDeadline // future, stronger claim
```

## Hard-guarantee gate

`GuaranteedReservation` remains OFF until provider-specific minimum-capacity and preemption evidence exists. Upper-bound enforcement may qualify earlier.

## Tests

- replenishment no-double-credit;
- timer rollover/overflow;
- nested donation period narrowing;
- non-preemptible operation rejected if remaining budget insufficient under declared contour;
- delayed completion cannot mint extra period budget.

## Exit criteria

`FG-VNX-TEMPORAL-UPPER-BOUND` may be enabled for exact supported contours. Guaranteed reservation stays independently gated.
