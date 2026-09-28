# Phase 8 — Real Hardware and QEMU Backend

Status: **Skipped by explicit user scope (QEMU/physical hardware/FPGA tasks); not claimed as executed.** See
[08_PHASE8_EXECUTION_STATUS.md](08_PHASE8_EXECUTION_STATUS.md).

## Goal

Move from fake/model CXL providers to executable CXL discovery, Type-3 backing and Type-2/device integration against QEMU first and physical hardware second, without changing the higher-level authority model.

## Backend layering

Keep protocol/register handling behind provider-private adapters:

```text
firmware / PCI enumeration
  -> CXL discovery adapter
  -> component/device capability parser
  -> memory/fabric/device provider backends
  -> provider-neutral SingNextOS contracts
```

Do not let ACPI tables, CXL DVSEC/ext-cap offsets, HDM decoder register layouts or mailbox opcodes escape into `OwnedRegion`, compute intent or normal device capabilities.

## QEMU milestone

Use upstream QEMU CXL support to validate at least:

- CXL host bridge/root port/device discovery;
- Type-3 memory region creation and host mapping;
- decoder/window changes where the model supports them;
- hotplug/unplug or modeled fault/reconfiguration paths where available;
- normal CXL.io/PCI-compatible resource discovery.

QEMU is an implementation/test reference. It does not prove hardware security/coherence properties that the emulation does not implement.

## Physical hardware milestone

Add a hardware backend only after model tests pass. Hardware bring-up must verify:

- platform firmware ownership and ACPI discovery;
- PCIe/CXL capability negotiation;
- CXL.mem decoder/window programming responsibility;
- reset/link/error reporting;
- IOMMU interactions for actual DMA/PASID/ATS paths;
- cache/coherence support used by the selected device path;
- RAS/poison/error events relevant to memory regions;
- IDE/security state only where the hardware/platform actually supports it.

## Feature taxonomy

Expose discovered features as provider capability/evidence records. Suggested dimensions include:

```text
CxlIoAvailable
CxlMemAvailable
CxlCacheAvailable
MemoryDeviceType
Switch/FabricManaged
DynamicCapacity/Pooling support
Security/IDE capability
RAS capability
```

These records inform admission/selection. They do not grant authority.

## Fault/reset injection

Add test hooks for:

- function/device reset;
- link down/up;
- decoder/binding invalidation;
- device removal;
- memory poison/RAS event where available;
- completion loss/duplication;
- IOMMU/domain invalidation for DMA paths.

Each event must update only the narrowest valid generation and drive operation/region reclaim through the common lifecycle.

## Negative tests

- unsupported protocol capability -> provider refuses feature rather than emulating it as real hardware support;
- firmware-owned decoder cannot be reprogrammed -> fail/operate in read-only discovered mode per backend contract;
- QEMU lacks a security property -> tests mark it unavailable, not trusted;
- hardware reset with outstanding staged operation -> no publish;
- IOMMU remap invalidates DMA binding but does not incorrectly invalidate unrelated CPU CXL.mem access.

## Acceptance criteria

- the same provider-neutral tests run against fake and QEMU backends;
- Type-3 QEMU memory backs normal `OwnedRegion` allocations/placements;
- CXL.io resources flow through `DeviceResourceSet`;
- hardware-specific IDs are confined to backend diagnostics/internal state;
- fault injection demonstrates fail-closed generation/reclaim behavior.

## External blockers

- QEMU's implemented CXL feature subset;
- available platform firmware and hardware;
- access to official CXL/PCIe/IOMMU specifications needed for production register semantics;
- vendor-specific Type-2 accelerator command ABI for accelerator execution tests.
