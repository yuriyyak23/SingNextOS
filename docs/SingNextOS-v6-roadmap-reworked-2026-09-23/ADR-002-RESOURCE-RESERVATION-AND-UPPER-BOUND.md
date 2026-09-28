# ADR-002: Reservation and enforced upper bound are incomparable claims

Status: accepted for the existing semantic refinement evaluator; v6 gates remain OFF.

## Context

The V1 resource-assurance partial order treated `GuaranteedReservation` as refining `EnforcedUpperBound`. The temporal sidecar compares an upper bound by decreasing amount and a reservation by increasing amount. Treating either quantity as the other can admit a weaker or opposite constraint. Reserving capacity does not establish a consumption upper bound, minimum service, deadline, or WCET.

## Decision

`GuaranteedReservation` and `EnforcedUpperBound` are incomparable in `SemanticPartialOrdersV1.ResourceAssuranceRefines`. A temporal amount comparison requires both sides to be reservation semantics or both sides to be non-reservation semantics. Same-class reservation amounts retain the capacity direction; same-class upper bounds retain the maximum-consumption direction. `GuaranteedReservation` continues to refine the generic `RuntimeEnforced` class in the base V1 vocabulary, but a temporal envelope cannot use that generic relation to compare amounts with opposite meanings.

This is a fail-closed refinement correction. Enum values, serialized contracts, and API signatures are unchanged. Callers formerly relying on reservation as an upper-bound guarantee must present separate qualified upper-bound evidence and fresh admission. Neither sidecar nor refinement result becomes capacity, timing, or execution authority.

## Qualification limits

Contract truth-table and temporal amount tests verify the software comparator. They do not prove provider reservation, physical minimum service, deadlines, WCET, or production behavior. `V6-FORMAL-REFINEMENT`, `V6-TEMPORAL-ACCOUNTING`, and `V6-GUARANTEED-DEADLINE` remain OFF. Java-dependent checks are excluded by instruction. ISA/opcode/CPU architecture impact: NONE.
