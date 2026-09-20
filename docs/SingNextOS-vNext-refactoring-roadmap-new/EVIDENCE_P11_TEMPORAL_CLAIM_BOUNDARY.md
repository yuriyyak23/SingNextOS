# P11 evidence — temporal upper-bound and guarantee boundary

## Disposition

- Baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry/qualification HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- Claim: `ModelOnly`/negative qualification only. No exact `EnforcedUpperBound` contour exists in the live tree.
- `FG-VNX-TEMPORAL-UPPER-BOUND` and `FG-VNX-GUARANTEED-RESERVATION`: **OFF**.
- Existing P05–P10 and user-owned P14/historical dirty state were preserved.

## Audit result and owner boundary

`ResourceBudgetAuthority` owns quantitative reservation, consumption and settlement, but has no clock binding, replenishment state machine, throttle/preemption operation or restart-safe period generation. `MonotonicDeadline` and `CancellationObservation` coordinate waiting/cancellation only; the DTO explicitly authorizes neither effect nor reclaim and contains no capacity/guarantee fact. The P10 scheduler has no enforcement API. No live provider supplies a qualified maximum-consumption interrupt, worst-case non-preemptible interval, timer-wrap policy or SMT/multicore isolation proof.

Accordingly, no replenishment policy was invented and no timer measurement was promoted to enforcement. Existing settlement rejects usage above the reserved envelope, which is accounting conservation after evidence, not proof that execution was stopped before exceeding a temporal maximum. Deadline metadata cannot enable either gate.

## Missing prerequisite and unsafe bypass

Owner: `ResourceBudgetAuthority` plus a future qualified runtime/provider measurement and enforcement adapter. Missing: exact monotonic clock contract and trust boundary, one replay-safe replenishment policy, preemption/throttle owner, bounded non-preemptible interval, overrun behavior, clock/provider restart and wrap handling, provider-loss containment, and SMT/multicore contention evidence. Guaranteed reservation additionally needs minimum delivered capacity under contention and provider loss. Bypassing these would confuse after-the-fact accounting or a deadline hint with maximum-consumption enforcement/minimum-capacity delivery.

Focused tests prove both gates remain default OFF, deadline evidence cannot authorize effect/reclaim/capacity, and the live budget owner has no replenishment/preemption/throttle/guarantee API. P03 lease races continue to cover no double credit/refund and P10 covers scheduler non-authority. Applicable invariants reviewed: VNX-002, VNX-004, VNX-007, VNX-008, VNX-010 through VNX-012, VNX-017, VNX-021 through VNX-028.

## Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase11TemporalClaimBoundaryTests|FullyQualifiedName~VNextPhase03ResourceLeaseTests|FullyQualifiedName~VNextPhase10ResourceSchedulerTests" --verbosity minimal
  Passed 14, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1644, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one stale user-owned P14 tuple, and one security-profile project-list drift.

## Exclusions

Ordinary SIP/Compute/ExternalOperation behavior remains unchanged. Plan/cache/receipt/telemetry remain evidence only. No temporal upper bound, hard realtime, guaranteed capacity, production, NativeAOT, HybridCPU provider/hardware, QEMU, firmware or CXL boot claim is made. No HybridCPU ISA or microarchitecture work was performed.
