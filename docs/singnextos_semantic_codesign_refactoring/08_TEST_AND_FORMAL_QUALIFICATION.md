# Test and formal qualification

Evidence classes are kept separate: unit, property, concurrency, fault injection, integration, provider conformance, differential, performance, model checking, formal proof.

- TLA+/PlusCal: final sentry cross-owner races, cancel/retire/containment, quarantine/reconciliation, optional sharding.
- Alloy: finite structural/refinement/configuration relations if trait/lattice combinations become hard to review.
- Property/state exploration: typed refinement, generation combinations, resource arithmetic/conservation, evidence replay.
- Lean/Coq: deferred; no stable mathematical core currently justifies the cost.

No test is called passed unless its executable result is recorded for the exact source/package/runtime tuple. Test fakes qualify protocol behavior only, not a real provider/ISE path.
