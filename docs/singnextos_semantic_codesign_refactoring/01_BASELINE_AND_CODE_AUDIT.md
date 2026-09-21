# Live Baseline and Code Audit

## Source freeze

- SingNextOS: `1890a8e921cfe903b5b44857e4661168bf7bbceb`; tree `83a683fd39d745d322cb12bd178fa179e5f14a4f`.
- HybridCPU-v2: `794c4a53494f503855ac8cf209efab23fde083b2`; tree `189f59a4b95fb822618117c60c8224bed701f9a5`.

## De-facto SingNextOS findings

### VERIFIED_EXISTING — authority/lifecycle owners
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` — live `CapabilityAuthority`; one logical capability ledger.
- `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs` — live `RegionAuthority`; Region ownership/use remains independent.
- `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs` — live quantitative budget owner.
- `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs` — live `Prepared -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published -> Released` lifecycle.
- `src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs` — exact invocation owner.

### VERIFIED_EXISTING — co-design seams
- `src/Runtime/SingPlus.Runtime/VNext/ComputePlanResourceAdmission.cs` — `IComputeSubmissionLegalityGate` and `PrepareResourceAwareComputeSubmission(...)` already separate CPU legality from OS/resource gates.
- `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs` — prepare/revalidate/commit with pre-submit compensation; no provider callback should run under authority locks.
- `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs` — exact ExternalOperation↔lease/provider generation binding and exact usage settlement.
- `src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs` — provider generation, Region probing and semantic planning; direct output is explicitly future-gated until CPU alias exclusion exists.
- `src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs` — policy/cache only; exact observation/provider/scheduler generations; restart invalidates hints.
- `src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs` — current HybridCPU provider adapter, exact generation checks and provider lifecycle mapping.
- `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.ExecutionPolicy.cs` — platform execution policy evidence is explicitly non-authoritative.
- `src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs`, `src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs`, `src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs` — existing ordinary-owner composition barriers and provider execution currently future-gated.

### VERIFIED_EXISTING — resource semantics
- `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs` — typed resource family/class/unit algebra.
- `contracts/SingPlus.Contracts/ResourceBudgetContracts.cs` — budget snapshots explicitly do not authorize effects/reclaim.
- Current exact compute contour is ComputeTime/nanoseconds; non-compute throughput/occupancy are not qualified end-to-end.
- Resource binding moves to `Consuming` once the exact operation is `Submitted`; settlement requires exact provider/operation binding evidence and rejects over-envelope evidence.

### VERIFIED_EXISTING — publication/cancellation/replay
- `contracts/SingPlus.Contracts/ExternalOperations.cs` separates completion, visibility, publication and release; provider loss does not imply safe release.
- `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs` contains stale/too-late/provider-closure states; observation is not reclaim authority.
- `src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs` is diagnostic-only and has no submit/settle/publish/release authority path.

### VERIFIED_GAP — target concepts absent as current general contracts
Live repository search found no general SingNext symbols named `OperationObligations`, `ExecutionGuarantees`, `EffectEpoch`, `CloseEpoch`, or `PublicationPermit`. `ExecutionBinding` hits existing secure/virtualization composition vocabulary; do not reuse that name ambiguously.

## De-facto HybridCPU-v2 findings

### VERIFIED_EXISTING — external operation contract
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs` — exact immutable request/generation/lifecycle receipts; receipts are evidence, construction is not authentication.
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs` — `ExternalOperationAdmissionBinding.Evaluate(...)` combines exact CPU guard + provider admission + current generations fail-closed.
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs` — staged publication eligibility requires exact current completion + visibility; direct-coherent output is not accepted by that gate.
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationReplayContracts.cs` — replay decisions never allow direct submit (`AllowsDirectSubmit == false`).
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdapterContracts.cs` — semantic request currently carries effect/visibility/cancellation/replay, but not the full obligation/guarantee vocabulary.
- `HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs` — exact lifecycle/generation/correlation; sticky stale and explicit cancellation acknowledgement behavior.

### VERIFIED_EXISTING — runtime legality remains authoritative
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs` — 8-slot bundle runtime legality path routes through explicit legality decisions.
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs` — `LegalityDecision`, `LegalityAuthoritySource`, reject taxonomy; compiler typed-slot facts are currently `ValidationOnly` and cannot replace runtime legality.
- `HybridCPU_ISE/CloseToHSL/Core/State/AdmissionState.cs` — admission state explicitly does not own scheduler legality, execution or publication.
- Retire/replay code and tests exist independently from external provider lifecycle.

### VERIFIED_GAP
No general `ExecutionGuarantees`, `EffectEpoch`, or `CloseEpoch` contract exists. These must be additive and contour-qualified, not inferred from current feature bits.

## Audit consequence
The modernization should extend existing owner/state-machine seams, not introduce a second capability ledger, budget ledger, Region ledger, external-operation lifecycle, provider lifecycle, or runtime legality service.
