# Phase 5 — Lane6 DmaStreamCompute Integration

## Goal

Align lane6 `DmaStreamCompute` with SingNextOS-mediated DMA/external execution while preserving its independence from lane7 L7-SDC.

## Preserve separation

`DmaStreamCompute` remains a lane6 CPU-visible compute/DMA contour. It is not converted into L7-SDC and is not made CXL-specific.

Keep separate:

- descriptor ABI;
- token lifecycle;
- validation rules;
- retire/publication path;
- telemetry;
- lane capacity/admission.

## Refactor the backend boundary

Introduce a provider-neutral DSC external backend that translates a validated DSC descriptor into the same stable ExternalRuntime contract used by SingNextOS.

The DSC runtime should ask for semantic capabilities such as:

- DMA readable/writable region access;
- streaming execution capability;
- staged or direct-output support;
- coherent-access availability when needed;
- cancellation capability;
- visibility requirements.

It must not ask for CXL ports, HDM decoders, DPA, or fabric routes.

## DMA/IOMMU correctness

Do not model CPU access to CXL.mem as an IOMMU mapping.

For actual device DMA paths, SingNextOS decides whether an IOMMU/domain/PASID/ATS binding is required. HybridCPU may retain an opaque mapping-generation receipt if the operation contract exposes one, but it must not fabricate hardware translation authority.

## Guarding and generation validity

Before materialized execution require:

- current owner/domain guard;
- exact descriptor identity;
- current DSC token state;
- valid SingNextOS admission/binding receipt;
- compatible mapping/device/fabric generations as represented by the opaque generation set;
- replay policy allowing submission.

## Retire/publication

Refactor `DmaStreamComputeRetirePublication` so completion, visibility, publication, and release are distinguishable when external execution is used.

For staged output:

```text
submit -> DeviceComplete -> Visible -> HybridCPU publication -> Released
```

For read-only or no-architectural-write operations, publication may be trivial but visibility/release still must follow the contract.

## Failure handling

Handle:

- pre-submit stale mapping/binding;
- device reset while running;
- DMA fault;
- partial provider completion;
- visibility failure;
- cancellation unsupported after submit;
- owner/domain change before reuse.

## Tests

Extend DSC tests with fake OS adapter cases for stale generation, reset, wrong completion, duplicate submit prevention, mapping drift, staged publication and release.

## Exit criteria

DSC can execute through SingNextOS-mediated DMA/external providers while preserving lane6-specific semantics and without introducing CXL topology into its descriptor ABI.