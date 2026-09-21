# Codebase Verification Ledger

Baseline: SingNextOS `1890a8e921cfe903b5b44857e4661168bf7bbceb`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## SingNextOS verified anchors

| Classification | Path / symbol | Planning consequence |
|---|---|---|
| VERIFIED_EXISTING | `contracts/SingPlus.Contracts/ExternalOperations.cs` / `ExternalOperationState`, `OperationDependencySnapshot` | extend current lifecycle; do not duplicate |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs` / `Prepare`, `Publish` | publication final revalidation already exists |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs` / `IExternalOperationProvider`, `IExternalOperationCancellationProvider` | add semantic binding/refinement around current adapter |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/VNext/ComputePlanResourceAdmission.cs` / `IComputeSubmissionLegalityGate` | reuse CPU legality seam |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs` / `ResourceAdmissionCommit`, `PrepareResourceExternalAdmission` | extend existing cross-owner commit protocol |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs` / `ExternalOperationResourceBinding`, `ExternalResourceUsageEvidence` | charging begins after submit; exact settlement already exists |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs` | staged path is current safe compute contour; direct output future-gated |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs` | scheduler is policy/evidence only, with exact generations |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs` | gates exist but current implementation enables none |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs` | provider use currently future-gated |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs` | exact owner protocol vocabulary and consumptive-commit barrier exist |
| VERIFIED_EXISTING | `tools/SingPlus.Admission/AdmissionVerifier.cs` | reuse single ManagedCap admission verifier |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs` | restart/restore exists with fresh-generation model |
| VERIFIED_EXISTING | `src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs` | replay diagnostics are non-authoritative |
| VERIFIED_GAP | `OperationObligations` | live search: absent as general symbol |
| VERIFIED_GAP | `ExecutionGuarantees` | live search: absent as general symbol |
| VERIFIED_GAP | `EffectEpoch` / `CloseEpoch` | live search: absent |
| VERIFIED_GAP | `PublicationPermit` | live search: absent; use explicit decision record only if needed |

## HybridCPU-v2 verified anchors

| Classification | Path / symbol | Planning consequence |
|---|---|---|
| VERIFIED_EXISTING | `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs` / `ExternalOperationRequest`, `ExternalGenerationSet` | preserve exact request/generation model |
| VERIFIED_EXISTING | `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs` / `ExternalOperationAdmissionBinding` | do not invent a second CPU+provider binding combiner |
| VERIFIED_EXISTING | `HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs` / `ExternalOperationPublicationGate` | staged provider publication validation already exists |
| VERIFIED_EXISTING | `HybridCPU_ExternalRuntime.Contracts/ExternalOperationReplayContracts.cs` / `AllowsDirectSubmit == false` | replay cannot mint submit permission |
| VERIFIED_EXISTING | `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdapterContracts.cs` / `ExternalOperationSemanticRequest` | conservative Level-1/early Level-2 semantic request exists |
| VERIFIED_EXISTING | `HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs` | sticky stale/exact lifecycle behavior exists |
| VERIFIED_EXISTING | `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs` / `LegalityDecision`, `LegalityAuthoritySource` | runtime legality remains authoritative |
| VERIFIED_EXISTING | `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs` / `TypedSlotFactStaging.CurrentMode = ValidationOnly` | compiler facts remain non-authoritative |
| VERIFIED_GAP | `ExecutionGuarantees` | live search: absent |
| VERIFIED_GAP | `EffectEpoch` / `CloseEpoch` | live search: absent |

## Verification rule for implementation
Every PR must refresh this ledger against its own HEAD. A task referencing a moved/renamed symbol is BLOCKED until the roadmap entry is corrected; names in this package are not a license to recreate obsolete abstractions.
