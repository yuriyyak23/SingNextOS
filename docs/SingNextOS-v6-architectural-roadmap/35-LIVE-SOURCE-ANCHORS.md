# Live Source Anchors at v6 Baseline

## SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`

Representative live anchors used by this roadmap:

```text
src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs
src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs
src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs
src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs
src/Runtime/SingPlus.Runtime/VNext/RuntimeKernel.SemanticObligations.cs
src/Runtime/SingPlus.Runtime/VNext/RuntimeKernel.SemanticExecutionBinding.cs
src/Runtime/SingPlus.Runtime/VNext/HybridCpu114SemanticCompatibility.cs
src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs
src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs
contracts/SingPlus.Contracts/OperationObligations.cs
contracts/SingPlus.Contracts/ExecutionGuarantees.cs
contracts/SingPlus.Contracts/ExternalOperations.cs
contracts/SingPlus.Contracts/ResourceControlModelContracts.cs
tools/SingPlus.Admission/AdmissionVerifier.cs
tools/SingPlus.Admission/AdmissionProof.cs
src/Kernel/Boot/SingNext.Boot.Capsule/BootCapsuleEntry.cs
```

## HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`

Representative live anchors:

```text
HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs
HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs
HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs
HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Guards.cs
HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/
HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/
HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/MatrixTile/
Compilers/HybridCPU_Compiler/
```

Live code/tests remain authoritative over this roadmap. Every implementation PR must refresh the source tuple before making a runtime/security/performance claim.
