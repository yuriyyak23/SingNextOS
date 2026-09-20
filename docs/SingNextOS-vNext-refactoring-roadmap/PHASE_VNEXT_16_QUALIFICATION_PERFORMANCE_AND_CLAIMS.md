# P16 — Security qualification, performance and bounded claims

## Goal

Close the roadmap with executable evidence and prevent claim inflation.

## Qualification lanes

### A — Pure model/property

- constraint algebra;
- conservation;
- monotonic derivation;
- serialization/versioning.

### B — Runtime authority

- concurrency/races;
- revocation;
- stale generations;
- no double spend;
- session donation.

### C — External effect

- bind/submit/settle;
- provider loss;
- quarantine;
- visibility/publication independence.

### D — SipJob differential

- ordinary SIP vs fused trace equivalence.

### E — HybridCPU adapter

- exact package/source tuple;
- CPU guard + provider admission + local resource authority;
- usage receipt correlation;
- no ISA/private-topology leakage.

### F — Temporal

- upper-bound enforcement;
- replenishment;
- preemption contour;
- deadline behavior.

## Claim vocabulary

Allowed claims only at proven levels:

```text
ModelOnly
StaticAdmission
RuntimeEnforced
ExecutableAdapter
GuaranteedReservation
ProductionQualified
```

Do not infer:

- hard realtime from average latency;
- HybridCPU acceleration from generic provider scheduling;
- guaranteed bandwidth from accounting;
- no-copy from SipJob fusion;
- security from DTO shapes;
- production readiness from emulator/model tests.

## Performance methodology

Report separately:

```text
capability validation cost
resource reserve/bind cost
session donation cost
planner/scheduler cost
provider admission cost
execution time
usage settlement cost
publication cost
```

Run contention tests at multiple thread/domain counts. Include failure-path cost and quarantine pressure.

## Exit criteria

Every enabled feature gate has:

- source implementation owner;
- focused executable tests;
- cross-cutting race tests;
- pinned provider/toolchain tuple where relevant;
- explicit exclusions;
- rollback/fallback path.
