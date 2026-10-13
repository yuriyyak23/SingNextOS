# P12 evidence — SipJob resource-aware composition boundary

## Disposition

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- Qualification HEAD: `cf66d014fd0fc19cdcabca2ef1b5b974805ecda1`.
- A parallel commit `cf66d01` landed during qualification and contains the already-qualified P05–P11 work. It did not overlap the P12 SipJob files; P12 qualification ran against that new HEAD plus the explicit P12 dirty changes.
- Claim: internal host/JIT `RuntimeEnforced` ordinary-boundary preservation. Resource consumption cannot be fused.
- `FG-VNX-SIPJOB-RESOURCE`: **OFF**. No resource-aware fused execution, parallel lease split or async resource resume is claimed.

## Authoritative equivalence boundary

`SipJobBarrierClass.ResourceConsumption` is classified as `MaterializeOrdinarySip` with lifecycle owner `ResourceBudgetAuthority` and mandatory live revalidation. It is distinct from `ExternalEffect` (`ExternalOperationAuthority`) and `Publication` (`ResponseRegistry`). Consequently the enabled ordinary resource contour executes the same P05/P04/P07 authority transitions; SipJob removes none of reserve/bind/consume/settle or publication transitions.

The existing ordinary-vs-fused SipJob oracle remains green for closed values, Region borrow and Region move. Existing barrier, cache restart/staleness and final-publication tests remain green. The new reflection regression shows plan/verified metadata and barrier decisions expose no mint/reserve/bind/consume/settle/release/authorize resource API. Since the resource gate is OFF, nested donation, parallel branch splitting, branch cancellation, provider ambiguity and async resume remain ordinary materialized paths, not incompletely implemented fused paths.

## Defect and remediation

The live barrier vocabulary had explicit effect/publication/ownership/async boundaries but no exact resource-consumption boundary. A future plan could otherwise fail to name why quantitative work must materialize. The additive enum value and barrier mapping close that negative space without adding a ledger or authority API.

Applicable invariants reviewed: VNX-001 through VNX-004, VNX-007 through VNX-017, VNX-021 through VNX-028.

## Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase12SipJobResourceBoundaryTests|FullyQualifiedName~Phase142ClosedValueDirectContourTests|FullyQualifiedName~Phase143InlineRegionBorrowTests|FullyQualifiedName~Phase143InlineRegionMoveTests|FullyQualifiedName~Phase145ABarrierPlannerTests|FullyQualifiedName~Phase146CacheIdentityTests" --verbosity minimal
  Passed 43, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1648, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one user-owned P14 tuple/HEAD coupling failure observed during the mid-phase commit, and one security-profile project-list drift.

## FutureGated and exclusions

Owner for a future fused contour: existing `CapabilityAuthority`, `ResourceBudgetAuthority`, session/invocation, Region, ExternalOperation and publication owners, coordinated by an owner-defined atomic branch split protocol. Missing evidence: ordinary-vs-fused resource transition traces, parallel conservation/cancellation races, provider-loss equivalence and restart-resume revalidation. Bypassing the barrier would permit plan metadata to race an indivisible lease or hide ambiguous provider consumption.

Ordinary SIP fallback remains intact. SipJob plan/cache/trace metadata remains non-authoritative. No temporal upper bound, guarantee, production, NativeAOT, hardware, QEMU, firmware or CXL boot claim is made. No HybridCPU ISA or microarchitecture work was performed.
