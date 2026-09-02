# Phase 3 — Real neutral HybridCPU domain binding

## Status

**In progress.** The first cross-repository slice now materializes and closes a real HybridCPU-owned neutral runtime domain through a privileged SingNextOS provider. Execution lifecycle transitions (`StartProcess` / `ParkProcess` / `ResumeProcess`) remain a later Phase-3 slice, so the feature is still classified as `RuntimeAdmission`, not `Executable`.

The external neutral export is supplied by HybridCPU-v2 PR #5 (`Runtime: export neutral domain binding facade`) and is pinned by the SingNextOS integration gate to exact HybridCPU commit `e09147e5c2e9f5d463884d3f46cb45bd9ceeda6b`.

## Goal

Materialize a live SingNextOS security principal into neutral HybridCPU execution, memory and I/O runtime ownership without importing VMX/VMCS or HybridCPU internal implementation types into SingNextOS core authority surfaces.

Current relation:

```text
Sing PlatformDomainIdentity(DomainId, ProcessGeneration)
  -> local PlatformDomainBindingId / generation
  -> privileged HybridCpuPlatformAuthorityProvider
  -> provider PlatformProviderDomainLeaseId / generation
  -> private HybridCPU NeutralDomainBindingHandle / epoch
  -> private HybridCPU neutral runtime context
       -> private DomainTag / AddressSpaceTag
       -> neutral execution + memory + I/O owners
```

Every arrow is a validated bridge relation, not numeric identity reuse.

The local Sing identity remains authoritative for OS policy. HybridCPU owns the external neutral runtime lease and close state. Neither side treats evidence from the other as a capability.

## Completed slice — real neutral bind / revoke

### 1. Why a new narrow HybridCPU export was required

Inspection of HybridCPU-v2 master at `38bf0614d8a58e2543b4a956ccc23bb22e1a8170` found public neutral domain descriptors/admission concepts such as `DomainRuntimeContext`, execution/memory/I/O domain descriptors and their validators. However, there was no public provider-facing owner that creates a live neutral lease and later closes that exact lease.

Constructing those descriptors directly inside SingNextOS would therefore have duplicated HybridCPU admission shape without creating external HybridCPU-owned lifetime authority.

The first integration attempt also proved an important repository boundary: the current monolithic `HybridCPU_ISE.csproj` has an unrelated pre-existing SecureCompute source-baseline break (missing debug-policy source types expected by its own tests). Phase 3 does not repair or take ownership of that unrelated graph.

HybridCPU-v2 PR #5 therefore exports a dedicated narrow assembly:

```text
HybridCPU_NeutralRuntime

NeutralDomainRuntimeFacade.Bind(OrdinaryService)
  -> NeutralDomainBindingLease(handle, epoch)

NeutralDomainRuntimeFacade.Close(exact lease)
  -> Closed | NotFound | Stale | Revoked | Faulted
```

The neutral assembly is self-contained. It owns a private neutral context consisting of execution, memory and I/O owner state and private runtime tags. It does not reference SecureCompute, VMX, DMA, scheduler or the broken monolithic project graph.

### 2. Ordinary-service authority profile

The first real profile deliberately proves only external neutral domain presence/lifetime. It materializes private execution + memory + I/O owner state while denying later-phase authority:

```text
DMA authority                 = false
IOMMU authority               = false
second-stage translation      = false
compatibility projection      = false
materialized VM guest state   = false
typed HybridCPU capability    = none
```

The public facade surface exposes only the semantic profile, opaque binding handle/epoch and typed bind/close outcomes. Private `DomainTag` / `AddressSpaceTag` values and owner state never cross the facade boundary.

This slice therefore does **not** pre-authorize Phase 4 region mapping, Phase 5 DMA, Phase 8 virtualization or VMX compatibility work.

### 3. Privileged SingNextOS provider assembly

`SingPlus.Platform.HybridCpu` is a separate privileged integration assembly. It references only `HybridCPU_NeutralRuntime`; `SingPlus.Platform.Abstractions` and `SingPlus.Runtime` do not reference any HybridCPU assembly.

`HybridCpuPlatformAuthorityProvider` implements only the existing provider contract:

```text
IPlatformAuthorityProvider
IPlatformFeatureProvider
```

Its descriptor advertises only:

```text
NeutralDomainBinding
```

and its semantic feature manifest reports:

```text
NeutralDomains v1 = RuntimeAdmission
OwnedRegionMapping = Unavailable
```

`MapOwnedRegion` / `RevokeRegionMapping` explicitly return `Unsupported` in this provider slice.

### 4. Strict identity separation

No ID space is reused:

```text
DomainId
  != PlatformDomainBindingId
  != PlatformProviderDomainLeaseId
  != NeutralDomainBindingHandle

process generation
  != PlatformDomainBindingGeneration
  != PlatformProviderLeaseGeneration
  != NeutralDomainBindingEpoch
```

HybridCPU `DomainTag` and `AddressSpaceTag` are allocated inside the neutral facade and never reach the Sing provider contract at all.

`BindDomain()` passes only semantic `OrdinaryService` to HybridCPU. It does not pass `DomainId.Value`, process generation, `CapabilityId` or any local resource identifier as a HybridCPU tag/handle.

After successful external materialization, the provider allocates a fresh independent `PlatformProviderDomainLeaseId` and stores the HybridCPU lease only in its private provider ledger.

### 5. Fail-closed revoke mapping

The provider validates exact provider lease ID, provider generation and Sing subject before external close.

External close outcomes map conservatively:

- exact `Closed` -> provider success;
- external `Revoked` for the exact privately-held lease -> provider success/idempotent closure;
- external `Stale` -> `Faulted`, because current closure is not proven;
- external `NotFound` -> `Faulted`, because disappearance is not closure proof;
- external `Faulted` -> `Faulted`.

A forged/stale/wrong-subject provider lease is rejected before the HybridCPU facade is called.

### 6. Phase-2 teardown reuse

No new process-teardown state machine was added.

The completed Phase-2 lifecycle already performs:

```text
ProcessState.Exiting
  -> local channels/authority closed
  -> platform mappings closed (none for this provider slice)
  -> PlatformAuthorityBridge.RevokeDomain(exact binding)
  -> provider RevokeDomain(exact provider lease)
  -> HybridCPU facade Close(exact private lease)
  -> local process/domain cleanup
  -> Exited | Faulted
```

An ordinary process bound through `HybridCpuPlatformAuthorityProvider` therefore cannot publish `Exited` until the HybridCPU neutral domain close succeeds. A provider fault remains fail-closed through existing Phase-2 containment semantics.

## Cross-repository verification

The normal `Sing+ local guarantees` job remains independent of HybridCPU source.

A separate `HybridCPU neutral domain integration` job:

1. checks out SingNextOS;
2. checks out exact HybridCPU neutral-runtime commit `e09147e5c2e9f5d463884d3f46cb45bd9ceeda6b` as a sibling repository;
3. builds the isolated `SingPlus.Platform.HybridCpu` provider and its tests against `HybridCPU_NeutralRuntime`;
4. runs Sing provider/runtime integration tests;
5. runs focused `HybridCPU_NeutralRuntime.Tests`.

The gate intentionally does not build unrelated `HybridCPU_ISE` / SecureCompute code. That keeps this iteration responsible only for the neutral provider contract it actually consumes while making the exact cross-repository dependency reproducible.

## Tests in this slice

HybridCPU neutral-runtime coverage:

- ordinary-service bind materializes private execution/memory/I/O owners;
- private domain/address-space tags are non-zero and independent from public lease handle/epoch values;
- the profile does not gain DMA/IOMMU/second-stage/VM/compatibility authority;
- stale epoch cannot close live authority;
- exact close revokes the binding;
- duplicate close reports `Revoked`;
- unsupported profile does not materialize authority;
- public facade signatures contain no domain tag/address-space/capability/VMX/DMA/IOMMU/lane/opcode authority terms.

Sing provider coverage:

- real facade bind creates one live HybridCPU binding and exact revoke closes it;
- provider lease identity/generation types remain distinct from HybridCPU handle/epoch types;
- stale provider generation is rejected before external close;
- wrong Sing subject is rejected before external close;
- duplicate active Sing subject cannot materialize a second HybridCPU binding;
- stale `ProcessHandle` generation is rejected by Sing before HybridCPU admission;
- Phase-2 `TerminateProcess` closes the HybridCPU lease before publishing `Exited`;
- feature discovery advertises only neutral runtime admission;
- core platform/runtime assemblies do not reference `HybridCPU_NeutralRuntime`; only the privileged provider assembly does.

## Remaining Phase-3 work

### 1. Real execution lifecycle transitions

`StartProcess`, `ParkProcess` and `ResumeProcess` still only publish local execution state. The next slice must consume a narrow neutral HybridCPU transition owner and preserve this ordering:

```text
local state/generation validation
  -> exact platform-domain binding validation
  -> provider transition begin/observe
  -> exact completion proof if asynchronous
  -> local Running/Parked publication
```

If external transition fails or remains pending, Sing must not publish a stronger local state.

### 2. Keep lifecycle authority semantic

Any required HybridCPU export must remain neutral and provider-facing. Do not expose lane IDs, bundle slots, VMCS fields, SMT topology or raw execution opcodes through Sing contracts.

### 3. Completion integration only when required

If a neutral HybridCPU transition is asynchronous, reuse `PlatformOperationId` / completion receipts rather than inventing a second completion model. Synchronous transition success may remain synchronous only when the external owner can prove it truthfully.

## Acceptance criteria

Phase 3 is **not complete yet**.

The bind/revoke half is complete when the cross-repository integration gate is green: a Sing process can acquire a live HybridCPU-owned neutral domain lease, retain strict identity separation, and have Phase-2 teardown close that lease before local exit.

Full Phase 3 completes only when the same bound process can also transition through real neutral start/park/resume lifecycle state with local publication gated by external success/completion, then tear down with stale-handle rejection.

## Do not do

- no `DomainId.Value == HybridCPU DomainTag/handle` shortcut;
- no provider lease ID == HybridCPU lease handle shortcut;
- no VMCS-backed process model;
- no lane placement API;
- no scheduler rewrite in the bind/revoke slice;
- no region mapping or DMA in this slice;
- no repair of unrelated SecureCompute baseline as part of Phase 3;
- no claim that `RuntimeAdmission` means external execution is already wired;
- no claim that every Sing service is a nested VM.
