# Live Source Anchors — Freeze 2026-09-23

## SingNextOS `690913e3500956aeb18d9823b35e9e64d40e42d7`

Representative anchors to verify again in every implementation PR:

```text
README.md
src/Runtime/SingPlus.Runtime/RuntimeKernel.cs
src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs
src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs
src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs
src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs
src/Runtime/SingPlus.Runtime/VNext/RuntimeKernel.SemanticObligations.cs
src/Runtime/SingPlus.Runtime/VNext/RuntimeKernel.SemanticExecutionBinding.cs
src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs
src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs
src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs
contracts/SingPlus.Contracts/OperationObligations.cs
contracts/SingPlus.Contracts/ExecutionGuarantees.cs
contracts/SingPlus.Contracts/ExternalOperations.cs
contracts/SingPlus.Contracts/ResourceControlModelContracts.cs
eng/singcap-toolchain-v1.json
global.json
src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj
tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj
```

Representative tests:

```text
tests/SingPlus.Tests/Runtime/SemanticExecutionBindingV1Tests.cs
tests/SingPlus.Tests/Runtime/SemanticAdmissionSentryTests.cs
tests/SingPlus.Tests/Contracts/ExecutionGuaranteesV1Tests.cs
tests/SingPlus.Tests/Architecture/RepositoryArchitecturePolicyTests.cs
```

## HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`

```text
README.md
Documentation/operational-semantics.md
Documentation/validation-baseline.md
Documentation/evidence-matrix.md
Documentation/WhiteBook/7. compiler-runtime-contract.md
Documentation/WhiteBook/10. safety-isolation-and-legality.md
Documentation/WhiteBook/20. legality-predicate.md
HybridCPU_ISE/CloseToHSL/Core/Contracts/CompilerContract.cs
HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs
HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Guards.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdapterContracts.cs
HybridCPU_ExternalRuntime/HybridCpuExternalRuntime.cs
Compilers/HybridCPU_Compiler/Core/IR/Model/HybridCpuCompilerContracts.cs
```

Execution surfaces that must be verified rather than inferred from docs:

```text
MatrixTile
DmaStreamComputeRuntime / lane6 staged commit
ExternalAcceleratorRuntime / lane7 L7-SDC
retire publication
replay behavior
fence behavior
memory transport
```

## Pin rule

These paths are anchors, not proof. Claims are based on actual code behavior plus executable tests and qualification artifacts at the exact source/package tuple.
