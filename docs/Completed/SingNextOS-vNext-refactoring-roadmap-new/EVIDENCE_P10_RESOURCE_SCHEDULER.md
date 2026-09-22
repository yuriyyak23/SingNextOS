# P10 evidence — resource scheduler and provider observations

## Disposition

- Baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry/qualification HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- Claim: internal host/JIT `RuntimeEnforced` separation: scheduler output is a placement hint only and cannot mutate authority.
- `FG-VNX-RESOURCE-SCHEDULER`: **OFF**. The ordinary planner path remains the default; no configuration or provider evidence enables the new policy dynamically.
- Existing P05–P09 and user-owned P14/historical dirty state were preserved.

## Contract and owner chain

`ResourceScheduler` caches only provider identity/generation, observation generation, semantic resource class, queue depth and load basis points. `ResourcePlacementHint` contains only request correlation and exact scheduler/provider/observation generations. It contains no capability, resource grant, budget, reservation, lease, Region ownership, ExternalOperation, publication state or authorization boolean.

Observations must be well-formed and strictly increase per provider. Selection validates the caller-supplied priority against an external ceiling but does not serialize priority into the hint. Before use, `Revalidate` requires the same request, live scheduler epoch, exact cached observation, and a live available non-faulted provider with the exact generation. Restart clears all evidence and increments the scheduler epoch. P08/P04/P07 remain the only path from a hint to execution, quantitative admission and ExternalOperation binding.

Two scheduler instances may select the same provider, but neither owns or duplicates a lease. Fairness/placement therefore cannot exceed admitted capacity: it has no capacity mutation API, while P03/P04 contention tests continue to prove the single budget owner.

## Negative evidence and remediation

The live gap was absence of an explicit scheduler/cache boundary with restart and exact-generation semantics. New focused coverage proves poisoned/stale cache rejection, agent restart invalidation, priority-laundering rejection, malformed/adversarial/reordered load rejection, and reflection absence of authorize/mint/reserve/lease/settle/release/publish/ownership/capability/budget APIs. Applicable invariants reviewed: VNX-001 through VNX-004, VNX-007, VNX-008, VNX-011 through VNX-018, VNX-021 through VNX-028.

## Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase10ResourceSchedulerTests" --verbosity minimal
  Passed 5, Failed 0, Skipped 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase03ResourceBudgetAuthorityTests|FullyQualifiedName~VNextPhase04CrossOwnerAdmissionTests|FullyQualifiedName~VNextPhase08ComputePlanIndependentGatesTests|FullyQualifiedName~VNextPhase10ResourceSchedulerTests" --verbosity minimal
  Passed 23, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1641, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one stale user-owned P14 tuple, and one security-profile project-list drift.

## Claims and exclusions

P11 upper-bound and guarantee contours remain FutureGated. Scheduler observations are inputs only; bypassing P11 enforcement would turn estimated load or a placement hint into a timing/capacity claim. Ordinary SIP/Compute/ExternalOperation fallback remains intact. Plan/cache/receipt/telemetry remain non-authoritative. No HybridCPU ISA/microarchitecture, NativeAOT, hardware, QEMU, firmware, CXL boot, upper-bound, guarantee or production work/claim was performed.
