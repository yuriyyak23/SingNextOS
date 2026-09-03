# EXT-HCPU-003

**Status:** Current (base neutral binding); ExternalBlocked (Phase-6
scheduler-policy admission)

The concrete neutral bind/close and synchronous `Start / Park / Resume` portion
is implemented by `HybridCpuPlatformAuthorityProvider` and exercised against the
pinned `HybridCPU_NeutralRuntime` in cross-repository CI. The remaining external
gap relevant to Phase 6 is a stable neutral API for semantic execution budget,
priority and latency/throughput intent. No such API is currently exported, so
SingNextOS must not project HybridCPU lane/slot/SMT internals as a substitute.

## Required external capability

The existing HybridCPU platform integration must provide a stable way to bind a SingNextOS security principal/domain to **neutral execution, memory and I/O runtime authority** without requiring SingNextOS to treat VMX/VMCS compatibility state as the authoritative domain model.

## Why SingNextOS needs it

SingNextOS has a concrete, narrow binding from an exact local process subject to
neutral HybridCPU runtime authority. Broader use of HybridCPU virtualization,
hardware isolation, executable DMA and domain scheduling still requires separate
feature-specific contracts and evidence while preserving the rule:

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

The base HybridCPU-backed platform-domain binding is no longer blocked. Neutral
scheduler-policy admission and later hardware-backed services remain blocked on
their own external contracts/evidence. Local process/domain lifecycle,
capabilities, ownership, SIP contracts and host tests remain unblocked.

## Explicit non-request

This requirement does **not** ask for:

- new VMX instructions;
- a VMCS manager;
- a redesign of HybridCPU scheduler internals or topology;
- compiler/backend/loader changes;
- SecureCompute activation.

For the remaining Phase-6 gap, it requests only a stable semantic admission
interface for budget, priority and latency/throughput intent. HybridCPU remains
the owner of placement and enforcement; SingNextOS neither requests nor exposes
lane/slot/SMT details.

## Fallback/mock used

The deterministic host provider remains a non-hardware reference for
SingNextOS-owned policy and negative tests; each host feature keeps its exact
manifest classification (`NeutralDomains v2` is `RuntimeAdmission`). The pinned
HybridCPU integration is the evidence source for the narrow neutral bind/close
and synchronous execution lifecycle; it is not evidence for scheduler quality,
executable DMA, boot/AOT or hardware security.
