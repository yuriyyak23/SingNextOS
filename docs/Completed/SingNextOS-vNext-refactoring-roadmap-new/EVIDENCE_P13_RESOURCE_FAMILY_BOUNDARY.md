# P13 evidence — non-compute resource family boundary

## Disposition

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry/qualification HEAD: `cf66d014fd0fc19cdcabca2ef1b5b974805ecda1`.
- Claim: `ModelOnly` typed-dimension and negative qualification. No non-compute resource family is executable.
- `FG-VNX-DMA-THROUGHPUT`, `FG-VNX-NETWORK-THROUGHPUT`, `FG-VNX-FABRIC-THROUGHPUT`, and `FG-VNX-DEVICE-OCCUPANCY`: **OFF**.
- P12 changes remained isolated; prior user/parallel work was not overwritten.

## Live owner audit

`ResourceEnvelopeV1` has typed Time, Throughput and Occupancy algebra, exact units/windows, subset checks and checked throughput multiplication. `ResourceBudgetAuthority` can atomically reserve a heterogeneous vector under its single lock, validates every dimension before mutation, and stores amounts in canonical enum order. This infrastructure is not a family implementation.

The live `ServiceBudgetDimension` vocabulary has no window-qualified DMA/network/fabric bytes dimension and no queue-slot/inflight-operation occupancy dimensions. `DeviceDmaOperations` is an operation count, not bytes/window; `OwnedMemoryBytes`/`PinnedMappedMemoryBytes` are existing memory accounting, not provider device-memory occupancy. No live adapter binds these semantic envelopes to DMA/network/CXL admission, provider generation, usage evidence or provider-loss quarantine. Therefore no semantic-to-budget mapping is inferred and no provider-local topology is exposed.

## Negative evidence

Focused tests prove cross-family subset/conversion rejection, non-throughput window access rejection, checked multiplication overflow, atomic rollback when one vector element exceeds its limit, canonical ordering after success, all family gates OFF, and absence of CXL/topology/physical/private handle fields. P01/P03 regressions continue to prove dimensional parsing and quantitative conservation.

Applicable invariants reviewed: VNX-001 through VNX-005, VNX-007, VNX-008, VNX-011 through VNX-014, VNX-017 through VNX-021, VNX-023, VNX-024, VNX-027 and VNX-028.

## Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase13ResourceFamilyBoundaryTests|FullyQualifiedName~VNextPhase01ResourceModelTests|FullyQualifiedName~VNextPhase03ResourceLeaseTests" --verbosity minimal
  Passed 21, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1652, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one user-owned P14 tuple/HEAD coupling failure, and one security-profile project-list drift.

## FutureGated and exclusions

Each family requires a separate existing effect owner/provider adapter, exact semantic-to-budget dimension mapping including window identity, exact provider generation and usage evidence, and family-specific loss/settlement tests. Occupancy additionally requires closure-before-release proof. Bypassing this would permit dimension laundering, topology leakage or premature release.

Ordinary ComputeTime behavior remains unchanged. No blanket heterogeneous-budget, throughput, occupancy, upper-bound, guarantee, production, NativeAOT, hardware, QEMU, firmware or CXL boot claim is made. No HybridCPU ISA or microarchitecture work was performed.
