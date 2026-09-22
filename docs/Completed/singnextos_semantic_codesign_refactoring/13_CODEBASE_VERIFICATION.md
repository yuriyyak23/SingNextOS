# Codebase verification anchors — cb6a94c314055e0712b8d9b8382ca1d146fe1c0e / 794c4a53494f503855ac8cf209efab23fde083b2

## SingNextOS

| Area | Path/symbol | Verification |
|---|---|---|
| capabilities | `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` | existing authoritative semantic permission owner |
| budget | `Budgets/ResourceBudgetAuthority.cs` / `Reserve`, `BindLease`, `BeginConsumption`, `QuarantineLease`, `ReconcileLease`, `SettleLease`, `SplitLease` | existing single quantitative ledger; vector reservation already exists |
| resource semantics | `ResourceControlModelContracts.cs` / `ResourceEnvelopeV1`, `ResourceUseConstraintV1`, `ResourceAssuranceV1` | typed immutable semantics/subset already exists |
| Region | `Regions/RegionAuthority.cs` / `RegionUseRecord`, `MutationEpoch` | exact generation/use/mutation owner |
| external op | `ExternalOperations.cs`, `ExternalOperationAuthority.cs` | existing separated lifecycle/effect/publication semantics |
| admission | `VNext/ResourceAdmissionProtocol.cs`, `ComputePlanResourceAdmission.cs` | existing prepare/revalidate/commit and legality gate seams |
| binding | `VNext/ExternalOperationResourceBinding.cs` | exact resource/external-op binding and settlement seam |
| planner | `ComputePlanner.cs`, `ResourceScheduler.cs` | policy/evidence only |
| provider | `HybridCpuExternalOperationProvider.cs` | existing adapter; extend rather than duplicate |
| donation | `VNext/ResourceDonationProtocol.cs` | nested narrowing + priority/assurance ceilings + SplitLease |
| checkpoint | `Checkpointing/RuntimeKernel.Checkpointing.cs` | fresh generation/admission on restore |
| SipJob | `SipJobSegmentAdmission.cs`, `SipJobStageExecutionEligibility.cs` | optimizer protocol names existing owners; provider path future-gated |

## HybridCPU-v2

| Area | Path/symbol | Verification |
|---|---|---|
| package/API | `HybridCPU_ExternalRuntime.Contracts.csproj` | 1.14.0/net11.0 |
| external op | `ExternalOperationContracts.cs` / `ExternalOperationContract.Version` | 1.4.0; exact request/generation/stage receipts |
| admission binding | `ExternalOperationAdmissionBindingContracts.cs` / `ExternalOperationAdmissionBinding.Evaluate` | CPU guard + provider admission independent; not OS authority |
| staged publication | `ExternalOperationPublicationContracts.cs` / `ExternalOperationPublicationGate` | pure fail-closed eligibility, not publication authority |
| replay | `ExternalOperationReplayContracts.cs` | replay policy/evidence not fresh submit permission |
| adapter | `ExternalOperationAdapterSession.cs` | exact lifecycle/single submit/invalidation/cancellation seams |
| runtime legality | `SafetyVerifier.Types.cs`, `SafetyVerifier.RuntimeLegality.cs`, RuntimeLegalityService composition | `LegalityDecision` remains runtime-owned |
| typed slot | `SafetyVerifier.TypedSlot.cs` | compiler facts are ValidationOnly at baseline |
| MatrixTile | MatrixTile ISA instruction/pipeline/retire paths | executable substrate exists; not merely DTO/parser |
| Lane6 DSC | `Execution/DmaStreamCompute/DmaStreamComputeRuntime.cs`, bridge, telemetry, retire-publication tests | executable runtime exists |
| Lane7/L7-SDC | `Execution/ExternalAccelerators/*`, commit/fence/runtime and L7Sdc tests | executable staged/commit substrate exists |

No current test execution result is claimed by this document.
