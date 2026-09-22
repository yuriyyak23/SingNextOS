# P15.13 — Claim levels, evidence and promotion rules

## Allowed claim vocabulary

### `ContractOnly`

Wire/API structure exists and passes parser/build checks. No execution claim.

### `ModelValidated`

Semantic model and deterministic tests validate a rule. No executable platform effect claim.

### `AdapterQualified`

Real SingNext code executes against an executable adapter/provider boundary. Hardware behavior is still unclaimed.

### `IseValidated`

The actual HybridCPU executable artifact runs through the ISE path and demonstrates the requirement with the intended entry ABI and image format.

### `QemuProtocolValidated`

A QEMU profile demonstrates relevant PCI/CXL protocol behavior. This is not real HybridCPU silicon validation.

### `HardwareValidated`

Independent evidence on the exact target hardware/platform profile demonstrates the requirement.

## No implicit promotion

Examples:

```text
BootInfo parser tests
    != ISE kernel handoff evidence

TemporaryApertureModelTests
    != real HDM decoder evidence

NativeAOT win-x64 publish
    != HybridCPU AOT evidence

ISE reset surrogate
    != silicon reset-vector evidence

QEMU CXL device
    != physical CXL endpoint evidence
```

## Required traceability row fields

Every P15 requirement should carry:

```text
RequirementId
Description
Owner
SourceFiles
Tests
ClaimLevel
EvidenceArtifact
ProductionGate
HardwareDependency
FailureSemantics
```

## Suggested requirement groups

```text
P15-Cxx  contracts/wire ABI
P15-Sxx  selection/trust/A-B semantics
P15-Axx  capsule admission/TCB
P15-Txx  HybridCPU toolchain/image
P15-Pxx  boot platform adapter
P15-Xxx  PCI/CXL/HDM
P15-Hxx  kernel handoff/takeover
P15-Rxx  recovery/reset
P15-Qxx  qualification/hardware
```

## Final release rule

The Direct SingNext architecture can be called **implemented** when the mandatory ISE contour is closed. It can be called **hardware validated** only when reset/ROM, protected state, PCI/CXL/HDM and DMA/IOMMU gates have direct hardware evidence. The word `Completed` in a roadmap directory MUST NOT itself promote a claim level.
