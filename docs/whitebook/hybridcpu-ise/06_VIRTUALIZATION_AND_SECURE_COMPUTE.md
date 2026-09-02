# 06. Virtualization And SecureCompute

## Virtualization is a neutral domain service

HybridCPU-v2 делает принципиально важный архитектурный выбор: **VMX не является virtualization architecture**. VMX — frozen compatibility frontend, тогда как authoritative state belongs to neutral runtime owners for execution, memory, I/O, traps, completion, migration, nested composition and secure policy.

SingNextOS должен принять этот выбор буквально.

Целевая модель:

```text
SingNextOS Virtualization Service (privileged SIP + kernel mediation)
  -> local VM/domain capabilities
  -> kernel PlatformDomainBindingAuthority
  -> external HybridCPU neutral execution/memory/I/O/nested owners
  -> optional VMX compatibility projection
```

Не должно появиться:

- kernel `VmcsManager`;
- mutable VMCS state store;
- active-VMCS-pointer authority;
- policy branching on `VmExitReason`;
- VMX capability bits as source of OS grants;
- direct user-mode VMX authority.

## What SingNextOS should expose

A future `.NET-like` virtualization API should be neutral and capability-based.

Conceptual surface:

```text
System.Virtualization.Domain
  CreateAsync(VirtualMachineSpec, VirtualMachineCapability)
  StartAsync
  PauseAsync
  ResumeAsync
  StopAsync
  MapMemoryAsync(OwnedRegion<byte>, ...)
  AttachDeviceAsync(DeviceCapability, ...)
  CreateChildDomainAsync(...)
  ReadEvidenceAsync(...)
```

The API talks about domains, memory, devices, policy and evidence — not VMCS fields.

An optional compatibility namespace may expose VMX-shaped data to a legacy VMM SIP:

```text
System.Virtualization.Compatibility.Vmx
```

but that namespace can only project neutral facts or invoke explicitly admitted compatibility operations.

## Trap and hypercall model

HybridCPU separates:

- trap request;
- trap policy;
- neutral trap result;
- backend admission;
- completion route;
- completion publication;
- retire publication;
- compatibility projection.

SingNextOS should mirror this separation for privileged hypercalls and virtualization exits.

Recommended conceptual chain:

```text
Guest event
 -> platform neutral trap result
 -> kernel maps event to VM/domain capability policy
 -> optional service backend
 -> completion object
 -> explicit publication authorization
 -> guest-visible completion/exit projection
```

A VM exit reason is presentation data. It cannot be used as proof that the backend was authorized or that a guest-visible effect committed.

## Nested virtualization and nested domains

HybridCPU nested model is already neutral in shape: child domain descriptors, nested memory composition, capability filtering and nested evidence policy. This maps well to a SingNextOS nested-domain capability model.

Recommended future invariant:

```text
ChildAuthority <= ParentAuthority
```

for every resource dimension:

- execution budget;
- address-space range;
- device set;
- DMA windows;
- compute services;
- evidence visibility;
- checkpoint rights;
- compatibility projections.

Nested compatibility state is never a second mutable authority store.

This architecture can support both virtual machines and lighter nested service isolation without forcing every child domain to emulate x86-style VM semantics.

## SecureCompute: desired architecture versus current implementation state

SecureCompute is the most important area where SingNextOS must avoid overclaiming.

HybridCPU SecureCompute WhiteBook currently describes a **neutral secure-domain architecture under activation hardening**, not a production-positive secure execution path. Current documents explicitly identify missing canonical registry/lifecycle ownership, Stage-B certificate integration, runtime-owned grant ledger and production backend/publication path.

Therefore SingNextOS should define the abstraction now but keep activation fail-closed.

### Future conceptual API

```text
System.Security.Confidential
  ConfidentialDomain.OpenAsync(ConfidentialDomainSpec, ConfidentialCapability)
  MeasureAsync
  CreateSharedRegionAsync
  ExportEvidenceAsync
  SealCheckpointAsync
```

### Current behavior requirement

Until HybridCPU exposes a named production-positive external interface and SingNextOS integration tests prove it:

```text
ConfidentialDomain.OpenAsync
  -> PlatformFeatureUnavailable / ExternalBlocked
```

No ordinary memory mapping should silently emulate a confidential domain.

## Secure memory

Future secure domains should preserve HybridCPU classification:

- private;
- shared explicit;
- measured;
- runtime mutable.

SingNextOS ownership remains necessary but not sufficient. A private owned region is only truly private if an external secure memory-domain owner enforces host/DMA restrictions.

Thus:

```text
OwnedRegion + Confidential metadata != confidential memory
```

without external secure-domain enforcement.

## Secure grants

SingNextOS already has a runtime-owned capability ledger. HybridCPU SecureCompute currently documents descriptor-level grant policy but notes that a canonical runtime-owned mint/revoke ledger is not yet production-established in that secure contour.

This creates a useful integration rule:

- SingNextOS local capability remains the OS permission;
- external secure grant, if one exists in a future platform ABI, remains an opaque platform permission;
- neither side's token is reinterpreted as the other's capability;
- the effect requires both.

## Measurement and evidence

SecureCompute measurement should be consumed as evidence, not authority.

A future `AttestationEvidence` object may include:

- domain measurement identity;
- policy digest;
- memory digest;
- epoch;
- platform evidence classification;
- signing/verification metadata if externally supported.

But evidence publication is gated by an explicit `PlatformEvidence` capability and visibility policy.

An application must never be able to acquire new memory/device rights merely because it possesses a valid measurement record.

## Host evidence non-leak

HybridCPU makes host-evidence quarantine a first-class invariant. SingNextOS should preserve it across SIPs.

Default policy:

- scheduler telemetry: kernel/platform diagnostic only;
- physical topology/lane state: not application-visible;
- host accelerator tokens: not application-visible;
- secure backend diagnostics: not guest-visible;
- guest-compatible evidence: explicit projected DTO only;
- debug evidence: requires dedicated debug/evidence capability.

This is especially important for multi-tenant and confidential workloads where telemetry itself can become a side channel.

## Checkpoint and migration

The previous Singularity+ whitebook treated snapshots as a broad hypervisor primitive. Current HybridCPU SecureCompute documentation is much more conservative: serializers, key owner, anti-replay protocol, atomic restore and exhaustive payload classification are not complete production facts.

SingNextOS must therefore split migration into three categories:

### Ordinary logical SIP state

Can be designed locally in the future using deterministic contracts and owned regions.

### Platform domain state

Requires explicit external checkpoint/restore support and generation revalidation.

### Confidential/private state

Requires a sealed/encrypted external contract, anti-replay semantics, re-attestation and explicit migration classification.

Unknown or host-owned state defaults to non-migratable.

## VMX compatibility policy

If SingNextOS later supports legacy VMs, VMX should live in an isolated privileged compatibility SIP rather than in the kernel object model.

The SIP receives only:

- a bounded virtualization-domain capability;
- read-only compatibility projections;
- explicit compatibility trap/operation endpoints;
- guest memory/device capabilities assigned by kernel policy.

It does not receive root memory/IOMMU authority, platform host evidence or raw platform runtime owners.

## SecureCompute and VMX must remain separate

A legacy VM cannot activate SecureCompute by manipulating VMX capability bits, VMCS fields or hypercalls. Secure domain activation must be a kernel-mediated neutral platform operation with a dedicated local capability and external grant.

The same applies in the opposite direction: a secure domain does not imply VMX compatibility or nested-VM rights.

## Recommended system roles

A mature SingNextOS could eventually have:

- `VirtualizationService` SIP — neutral VM lifecycle orchestration;
- `VmxCompatibilityService` SIP — optional legacy projection/backend compatibility;
- `EvidenceService` SIP — policy-gated evidence aggregation;
- `ConfidentialComputeService` SIP — only when production-positive externally;
- privileged kernel platform bridge — sole holder of external runtime owner bindings.

Each service is separately capability-scoped and replaceable. None owns the external hardware/runtime authority directly.

## Decision

Virtualization is a first-class future direction for SingNextOS, but it must be implemented as **neutral domain orchestration**, not as a VMX-centric kernel. SecureCompute should be architecturally prepared now and operationally unavailable until a positive, owner-bound HybridCPU platform contour exists and is independently verified.