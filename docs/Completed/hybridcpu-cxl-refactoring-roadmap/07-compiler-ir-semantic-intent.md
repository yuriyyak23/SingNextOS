# Phase 7 — Compiler IR Semantic Intent

## Goal

Extend `Compilers/HybridCPU_Compiler` so programs can express external execution and memory semantics needed by SingNextOS/CXL without exposing CXL topology or binding the source/IR to a specific CXL revision.

## IR design principle

Add semantic intent, not transport identity.

Candidate IR concepts:

```text
IrExternalExecutionIntent
IrMemoryAccessIntent
IrPublicationIntent
IrCoherenceRequirement
IrExternalEffectRequirement
IrCancellationRequirement
```

Exact names may differ; the compiler must preserve the distinction between execution intent and runtime authority.

## Semantic dimensions

IR should be able to represent:

- external execution required/optional;
- DMA/streaming vs system-external accelerator intent;
- input/output/read-write region roles;
- staged output required/preferred/optional;
- coherent access required/preferred/not-required;
- direct coherent output explicitly requested only when source semantics demand it;
- idempotent/retryable/non-retryable effect intent;
- cancellation requirement;
- ordering/fence requirement;
- provider capability constraints that are stable across PCIe/CXL implementations.

## Forbidden IR fields

Do not add:

- CXL port number;
- HDM decoder ID;
- HPA/DPA layout;
- switch route;
- FM pool/binding ID;
- CXL 3.x/4.x opcode selection;
- physical BDF/PASID/IOMMU IDs as normal semantic IR.

Those are runtime/provider materialization details.

## Resource class relationship

Do not add `IrResourceClass.Cxl` or `IrSlotClass.Cxl`.

Existing semantic carriers remain:

- lane6 classes for DSC/streaming semantics;
- lane7 `SystemSingleton` for L7 system/external accelerator commands;
- ordinary LSU/memory operations for CPU access to memory that SingNextOS may back with CXL Type-3.

CXL-backed ordinary memory must not require a new instruction class.

## Analysis and propagation

Compiler passes should propagate external-operation intent through:

- IR construction;
- alias/footprint analysis;
- scheduling constraints;
- bundle admission metadata;
- descriptor construction/lowering;
- diagnostics and debug dumps.

Intent must survive optimization without being strengthened accidentally. For example `coherent-preferred` must not become `coherent-required` unless semantics require it.

## Compile-time vs runtime checks

Compile time may reject structurally impossible combinations, such as a lane7-only operation forced into lane6.

Compile time must not claim runtime provider availability, ownership, current coherence, current mapping, or SingNextOS admission.

## Tests

Add IR parsing/construction/equality tests, optimization preservation tests, forbidden-topology tests, and diagnostics verifying that provider requirements remain semantic.

## Exit criteria

The compiler can carry all runtime-relevant external-operation intent to lowering without introducing a CXL-specific slot/resource class or topology field.