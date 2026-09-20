# P08 evidence — ComputePlan independent gates

## Disposition

- Baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry/qualification HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- Claim: internal host/JIT `RuntimeEnforced` resource-aware staged ComputePlan admission for `ComputeTime/Nanoseconds`.
- `FG-VNX-COMPUTE-V2`: **OFF**. Ordinary resource-unaware `PlanCompute`/`ValidateComputePlanBeforeSubmit` remains unchanged and default.
- Existing P05–P07 dirty work and user-owned P14/historical state were preserved.

## Contract and owner chain

`ComputeIntent` now has an additive optional semantic `ResourceEnvelopeV1`. The plan contains no capability, lease, operation binding, provider-private handle, or cached authorization. `PrepareResourceAwareComputeSubmission` performs, in order, live plan/provider/Region revalidation, an independent CPU/runtime legality gate, exact prepared ExternalOperation Region-use matching, then P04 effect capability + resource grant + budget + ExternalOperation admission. The selected provider identity/generation is recorded only as opaque correlation in the P07 binding.

| Gate | Live owner | Negative evidence |
|---|---|---|
| effect permission | `CapabilityAuthority` | missing effect capability denied |
| resource permission | `CapabilityAuthority` resource constraint | missing/revoked grant denied |
| quantitative capacity | `ResourceBudgetAuthority` | insufficient limit denied with no charge |
| Region ownership/use | `RegionAuthority` + exact prepared operation | stale Region or mismatched uses denied |
| provider availability/generation | live planner candidate revalidation | generation drift denied |
| CPU/runtime legality | independent `IComputeSubmissionLegalityGate` | explicit denial blocks before admission |

Planner choice can change from slow to fast provider without changing the exact semantic intent/envelope. A provider lacking the required staged semantic path is not a compatible fallback. Provider evidence, plan, cache, or capability receipt cannot enable a gate or authorize submit.

## Defects/remediation and negative space

The live P08 gap was absence of a semantic resource requirement and absence of a single API proving all independent gates at submit. The additive contract and prepare path close that internal contour. Exact operation uses are now revalidated by `ExternalOperationAuthority`; P04 remains the cross-owner commit owner. Public reflection coverage rejects lane/opcode/slot/queue/DSC/L7/VMCS/IOMMU/CXL/topology/physical-address/provider-handle/token names.

`VNextPhase08ComputePlanIndependentGatesTests` covers the complete gate truth table, revoked grant, stale cached plan, provider generation drift, compatible provider selection, incompatible fallback, and public ABI negative space. Applicable invariants reviewed: VNX-002 through VNX-005, VNX-008, VNX-011, VNX-013 through VNX-021, VNX-023, VNX-024, VNX-027 and VNX-028.

## Commands and results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase08ComputePlanIndependentGatesTests" --verbosity minimal
  Passed 5, Failed 0, Skipped 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~ComputePlannerTests|FullyQualifiedName~VNextPhase04CrossOwnerAdmissionTests|FullyQualifiedName~VNextPhase05SipResourceSentryTests|FullyQualifiedName~VNextPhase06ResourceDonationTests|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests|FullyQualifiedName~VNextPhase08ComputePlanIndependentGatesTests" --verbosity minimal
  Passed 54, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1636, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one stale user-owned P14 tuple, and one security-profile project-list drift.

## Claims and exclusions

P09 remains FutureGated: it owns any provider-neutral boundary mapping and requires exact locally available adapter/artifact evidence. Bypassing it would let a host-model candidate stand in for provider admission or HybridCPU legality. No ExecutableAdapter, HybridCPU, NativeAOT, upper-bound, guarantee, production, hardware, QEMU, firmware, or CXL boot claim is made. No ISA/microarchitecture work was performed.
