# Phase 3 — Real neutral HybridCPU domain binding

## Status

**Implementation complete; merge acceptance requires the pinned cross-repository CI gate.**

Phase 3 now has both required halves:

1. real neutral HybridCPU bind/revoke, merged into SingNextOS by PR #23;
2. real neutral `Start / Park / Resume` execution lifecycle, implemented by this iteration.

The external dependency chain is intentionally split because HybridCPU-v2 PR #5 is still open:

```text
HybridCPU-v2 PR #5
  neutral bind / close owner
  head e09147e5c2e9f5d463884d3f46cb45bd9ceeda6b

    -> stacked HybridCPU-v2 PR #6
       neutral Start / Park / Resume owner
       head 3ea2303e1a5fe423e76ef3c2f3c399001ca08288

         -> SingNextOS Phase-3 lifecycle PR
            exact pinned cross-repository integration
```

No new lifecycle scope was folded into PR #5. PR #6 is a one-commit, two-file delta on top of the already-reviewed neutral runtime export.

## Goal

Materialize a live SingNextOS security principal into neutral HybridCPU runtime ownership and ensure that local execution state is never published stronger than the exact external state proved by the bound provider.

Current identity and authority relation:

```text
Sing DomainId + process generation
  -> local PlatformDomainBindingId / generation
  -> privileged HybridCpuPlatformAuthorityProvider
  -> provider PlatformProviderDomainLeaseId / generation
  -> private HybridCPU NeutralDomainBindingHandle / epoch
  -> private HybridCPU neutral runtime context
       -> private DomainTag / AddressSpaceTag
       -> neutral execution + memory + I/O owners
       -> semantic execution state Ready / Running / Parked
```

Every arrow is a validated bridge relation, not numeric identity reuse.

The local Sing identity remains authoritative for OS policy. HybridCPU owns the external neutral runtime lease and execution state. Neither side treats evidence from the other as a capability.

## Completed slice A — real neutral bind / revoke

SingNextOS PR #23 added a privileged `SingPlus.Platform.HybridCpu` provider backed by the narrow `HybridCPU_NeutralRuntime` export from HybridCPU-v2 PR #5.

The bind/revoke relation is:

```text
NeutralDomainRuntimeFacade.Bind(OrdinaryService)
  -> NeutralDomainBindingLease(handle, epoch)

NeutralDomainRuntimeFacade.Close(exact lease)
  -> Closed | NotFound | Stale | Revoked | Faulted
```

The provider issues a fresh independent `PlatformProviderDomainLeaseId` only after external materialization succeeds and stores the HybridCPU lease only in its private ledger.

### Ordinary-service authority profile

The neutral runtime profile deliberately grants none of the later-phase authority:

```text
DMA authority                 = false
IOMMU authority               = false
second-stage translation      = false
compatibility projection      = false
materialized VM guest state   = false
typed HybridCPU capability    = none
```

Private `DomainTag` / `AddressSpaceTag` values and owner state never cross the HybridCPU facade boundary.

## Completed slice B — real neutral execution lifecycle

### 1. HybridCPU semantic transition owner

HybridCPU-v2 PR #6 adds one synchronous provider-facing operation to the existing narrow neutral owner:

```text
NeutralDomainRuntimeFacade.TransitionExecution(exact lease, transition)
```

Semantic state machine:

```text
Ready   -- Start  --> Running
Running -- Park   --> Parked
Parked  -- Resume --> Running
```

Public types are limited to:

```text
NeutralExecutionTransition = Start | Park | Resume
NeutralExecutionState      = Ready | Running | Parked
```

The transition result echoes:

- the exact opaque HybridCPU lease;
- the requested semantic transition;
- the resulting semantic state.

No scheduler placement, lane ID, SMT identity, bundle/slot state, ISA opcode, VMCS field or completion token is exposed.

### 2. Why this lifecycle is synchronous

No new `PlatformOperationId` or completion state machine is invented for these transitions.

The neutral facade owns the lease and execution state and performs each current transition atomically in the narrow runtime owner. It can therefore return definitive synchronous success/failure truthfully.

If a future external implementation makes these transitions asynchronous, it must reuse the existing Platform Contract vNext operation/completion model rather than introducing a second completion authority.

### 3. HybridCPU fail-closed transition semantics

The neutral owner rejects without state mutation:

- stale lease epoch -> `Stale`;
- unknown/unmaterialized lease -> `NotFound`;
- already-closed lease -> `Revoked`;
- invalid state/transition pair -> `InvalidTransition`;
- undefined transition enum -> `Faulted`.

Close remains valid from a live `Ready`, `Running`, or `Parked` binding and still requires the exact lease/epoch.

### 4. Sing platform execution contract

`SingPlus.Platform.Abstractions` adds a semantic optional provider interface:

```text
IPlatformDomainExecutionProvider
  TransitionDomainExecution(
    exact PlatformProviderDomainLease,
    Start | Park | Resume)
```

The result contains only:

```text
exact provider domain lease
requested semantic transition
semantic resulting state
```

`PlatformDomainExecutionContract.ValidateResult(...)` requires:

- exact provider lease ID;
- exact provider lease generation;
- exact local subject;
- exact requested transition;
- exact expected resulting state.

A provider that returns `Success` with mismatched identity/transition/state produces `PlatformFaulted`; malformed evidence never becomes local execution authority.

### 5. HybridCpuPlatformAuthorityProvider bridge

`HybridCpuPlatformAuthorityProvider` now implements:

```text
IPlatformAuthorityProvider
IPlatformFeatureProvider
IPlatformDomainExecutionProvider
```

The provider validates the exact Sing provider lease before touching HybridCPU, translates only semantic transitions, calls the exact privately-held HybridCPU lease, and validates the returned external lease/transition/state before returning Sing platform evidence.

External failures map conservatively:

- invalid external transition -> `Denied`;
- external `Revoked` -> provider lease becomes revoked;
- external `Stale` or `NotFound` for the provider-owned private lease -> `Faulted`;
- external `Faulted` -> `Faulted`.

### 6. Local RuntimeKernel publication ordering

For a process without a tracked platform binding, the existing local-only lifecycle remains unchanged.

For a process with a tracked platform binding, `RuntimeKernel` now requires this order:

```text
StartProcess
  Resolve exact ProcessHandle generation
  require local Admitted
  validate exact PlatformDomainBinding
  provider Start succeeds with exact evidence
  -> publish local Runnable
  -> publish local Running

ParkProcess
  Resolve exact ProcessHandle generation
  require local Running
  validate exact PlatformDomainBinding
  provider Park succeeds with exact evidence
  -> publish local Parked

ResumeProcess
  Resolve exact ProcessHandle generation
  require local Parked
  validate exact PlatformDomainBinding
  provider Resume succeeds with exact evidence
  -> publish local Running
```

If the bound provider does not implement `IPlatformDomainExecutionProvider`, the transition fails with `PlatformUnsupported`. There is no silent fallback to local-only publication for an externally bound process.

Provider denial/revocation/fault or malformed success evidence leaves the current local process state unchanged.

## Feature classification

The real HybridCPU provider now reports:

```text
NeutralDomains v1 = Executable
OwnedRegionMapping = Unavailable
```

`Executable` here means only that the neutral domain bind + start/park/resume + close lifecycle is wired and verified through the pinned provider integration.

It does **not** claim:

- region mapping;
- coherent/non-coherent memory access;
- DMA;
- device execution;
- scheduler placement/quality;
- VM/nested-domain execution;
- production secure execution.

The host/model provider retains its own existing availability classification; feature availability remains provider-specific and exact, not ordinal.

## Identity separation

No identity space is reused:

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

HybridCPU `DomainTag` and `AddressSpaceTag` remain private to `HybridCPU_NeutralRuntime`.

`Start / Park / Resume` requests never carry local `CapabilityId`, physical addresses, mapping IDs, lane IDs, opcodes or HybridCPU tags.

## Phase-2 teardown composition remains authoritative

No second teardown state machine was added.

A bound process still tears down through the completed Phase-2 ordering:

```text
ProcessState.Exiting
  -> local channels/authority closed
  -> platform mappings closed (none for this provider phase)
  -> PlatformAuthorityBridge.RevokeDomain(exact binding)
  -> provider RevokeDomain(exact provider lease)
  -> HybridCPU Close(exact private lease)
  -> local process/domain cleanup
  -> Exited | Faulted
```

The same exact close path works when the external neutral execution state is `Running` or `Parked`.

## Tests

### HybridCPU neutral-runtime tests

- exact `Ready -> Running -> Parked -> Running` lifecycle;
- invalid transition ordering does not mutate state;
- stale epoch cannot transition live authority;
- closed binding cannot transition;
- undefined transition fails `Faulted` without mutation;
- exact close/revoked behavior remains intact;
- public facade signatures contain no domain-tag/address-space/capability/VMX/DMA/IOMMU/lane/opcode/bundle/slot/SMT authority terms.

### Sing platform contract / bridge tests

- bound process publishes `Running/Parked` only after provider success;
- denied Start leaves local process `Admitted`;
- provider revocation during Park leaves local process `Running` and revokes the bridge binding;
- malformed provider success is `PlatformFaulted` and leaves local state unchanged;
- bound provider without execution interface cannot fall back to local-only state;
- stale process generation is rejected before provider transition;
- execution result validation rejects stale generation, wrong subject, wrong transition and wrong resulting state.

### Real HybridCPU provider integration tests

- provider bind + Start + Park + Resume + revoke against exact `HybridCPU_NeutralRuntime`;
- invalid real HybridCPU transition order maps to provider denial without losing the lease;
- stale/wrong-subject provider leases are rejected before external transition/close;
- RuntimeKernel publishes real `Running/Parked/Running` only after the external transitions succeed;
- Phase-2 teardown closes a real currently-Running HybridCPU binding before local `Exited` publication;
- feature discovery reports `NeutralDomains = Executable` and `OwnedRegionMapping = Unavailable`;
- core platform/runtime assemblies still do not reference `HybridCPU_NeutralRuntime`.

## Cross-repository verification

The normal `SingNextOS local guarantees` job remains independent of HybridCPU source.

The separate `HybridCPU neutral domain integration` job pins exact HybridCPU transition head:

```text
3ea2303e1a5fe423e76ef3c2f3c399001ca08288
```

It must:

1. checkout SingNextOS;
2. checkout the exact stacked HybridCPU transition commit;
3. restore/build the isolated provider integration graph;
4. run Sing real-provider integration tests;
5. run `HybridCPU_NeutralRuntime.Tests`.

## Acceptance criteria

Phase 3 is complete at implementation level when the same bound process can:

```text
bind exact neutral external lease
 -> Start externally, then publish local Running
 -> Park externally, then publish local Parked
 -> Resume externally, then publish local Running
 -> teardown through exact external Close
 -> publish local Exited only after closure
```

with stale/revoked/denied/malformed paths failing closed and all external identities remaining bridge-private.

Merge acceptance additionally requires the full Sing regression gate and pinned cross-repository integration gate to be green on the final PR head.

## Next phase

After Phase 3 lands, the first actually incomplete roadmap phase is:

**Phase 4 — exact non-coherent-safe owned-region mapping.**

That phase must start from exact range/access/coherence/visibility semantics. It must not infer DMA, IOMMU, cache behavior or physical zero-copy from the existence of the neutral execution lifecycle.

## Do not do

- no `DomainId.Value == HybridCPU DomainTag/handle` shortcut;
- no provider lease ID == HybridCPU lease handle shortcut;
- no VMCS-backed process model;
- no lane/SMT placement API;
- no raw scheduler/ISA opcode lifecycle API;
- no region mapping in Phase 3;
- no DMA in Phase 3;
- no repair of unrelated SecureCompute baseline as part of Phase 3;
- no claim that `Executable` implies mapping/DMA/virtualization/production-secure support.
