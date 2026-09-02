# EXT-HCPU-003

**Status:** External Blocked

## Required external capability

The existing HybridCPU platform integration must provide a stable way to bind a SingNextOS security principal/domain to **neutral execution, memory and I/O runtime authority** without requiring SingNextOS to treat VMX/VMCS compatibility state as the authoritative domain model.

## Why SingNextOS needs it

SingNextOS currently has a local `DomainId` lifecycle authority but no concrete binding to HybridCPU execution/memory/I/O domain owners. Full use of HybridCPU virtualization, isolation, IOMMU/DMA and domain scheduling requires an external platform binding while preserving the rule:

```text
SingNextOS DomainId != raw HybridCPU runtime handle
```

The local kernel must remain the OS capability authority; the external runtime remains authoritative for live platform-domain admission.

## Existing interface expected

An already existing or externally supplied HybridCPU runtime/platform interface that can:

- create/adopt or bind a neutral execution domain;
- create/adopt or bind a neutral memory/address-space domain;
- create/adopt or bind a neutral I/O domain;
- identify stale/revoked/terminated bindings;
- support bounded lifecycle operations required for start/park/resume/terminate or equivalent neutral operations.

The exact external handle representation is intentionally unspecified and must be treated as opaque by SingNextOS.

## Minimal reproduction

1. Create and admit a SingNextOS SIP domain using the local runtime.
2. Request external execution/memory/I/O bindings through an integration-only adapter.
3. Verify that the adapter can distinguish supported, denied, stale and terminated domain states.
4. Terminate the SingNextOS domain and prove old external bindings cannot be reused.
5. Verify that no VMCS mutable field store or VMX compatibility object is required as the local authority representation.

## SingNextOS component blocked

Only concrete HybridCPU-backed platform-domain binding and later hardware-backed services that depend on it. Local process/domain lifecycle, capabilities, ownership, SIP contracts and host tests remain unblocked.

## Explicit non-request

This requirement does **not** ask for:

- new VMX instructions;
- a VMCS manager;
- HybridCPU scheduler changes;
- compiler/backend/loader changes;
- SecureCompute activation.

It requests a binding to existing neutral runtime authority only.

## Fallback/mock used

The current local `DomainRegistry` and host runtime tests remain the source of truth for SingNextOS-owned lifecycle semantics until an external binding is available.