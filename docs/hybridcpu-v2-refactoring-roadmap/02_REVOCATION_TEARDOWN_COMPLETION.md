# Phase 2 — Revocation, teardown and completion lifecycle

## Status

**In progress.** The first Phase-2 vertical slice makes owned-region mapping revocation completion-backed and separates local authorization death from external closure. Process exit/fault orchestration, SIP waiter ordering and multi-resource teardown remain future Phase-2 iterations.

## Current state

Phase 1 established the prerequisites this phase consumes:

- typed/versioned provider discovery;
- opaque `PlatformOperationId` / operation generations;
- completion states and identity-validated receipts;
- explicit memory-visibility vocabulary.

The existing region authority already pins an owned region while a platform mapping reservation exists. Before this slice, however, `RuntimeKernel.RevokePlatformRegionMapping()` and capability-revocation cascade treated a successful synchronous `IPlatformAuthorityProvider.RevokeRegionMapping()` return as enough to release that reservation.

That was safe for the host model only because the provider completed synchronously; it was not a reusable rule for a real asynchronous device/runtime backend.

## Completed slice — completion-backed region mapping revocation

The bridge now tracks two independent dimensions for every platform region mapping:

```text
LocalAuthorizationRevoked = false | true

PlatformClosure:
  Active
  Draining
  Closed
  Faulted
```

This deliberately allows the security-critical state:

```text
LocalAuthorizationRevoked = true
PlatformClosure = Draining
LocalReservationReleased = false
```

A dead local capability therefore never implies that external authority has stopped touching the region.

`PlatformRegionMappingLifecycle` exposes only semantic local/closure state. It contains no provider lease, physical address, VMX/VMCS state, lane/opcode or hardware descriptor. `LocalReclaimAllowed` becomes true only when:

```text
PlatformClosure == Closed
&& LocalReservationReleased == true
```

### Narrow provider revocation contract

A new optional `IPlatformRegionRevocationProvider` extends completion observation with one mapping-specific begin operation:

```text
BeginRegionMappingRevocation(mapping lease, policy)
  -> PlatformRegionRevocationTicket
```

The ticket binds:

- provider mapping ID;
- provider mapping generation;
- opaque `PlatformOperationIdentity`.

The bridge validates that the ticket refers to the exact provider mapping/domain generation it asked to close. A provider that only implements the legacy synchronous `RevokeRegionMapping()` can still close its own state, but SingNextOS does **not** release the local region reservation without a completion-backed `Closed` receipt. That legacy path therefore remains pinned/fail-closed.

### Non-blocking begin / observe lifecycle

`RuntimeKernel.RevokePlatformRegionMapping()` now begins closure and observes once. A host provider may return `Closed` immediately; a real provider may remain `Draining`. No unbounded wait is hidden inside the call.

`RuntimeKernel.ObservePlatformRegionMappingRevocation()` performs later completion observation.

For capability revocation, local capability authority is revoked first as before. `PlatformAuthorityBridge.BeginCapabilityRevocation()` then marks every affected mapping `LocalAuthorizationRevoked = true` before platform closure begins. The cascade attempts to begin closure for every affected mapping even if one remains pending or faults.

### Reclaim gate

A region reservation is released only after all of the following succeed in order:

1. the bridge has the exact local `PlatformRegionMapping` identity;
2. the mapping-specific provider ticket matched the exact provider mapping/generation;
3. `ObserveCompletion()` returned a receipt for the exact opaque operation/domain generation;
4. that receipt state is exactly `Closed`;
5. the local platform binding is still the exact expected binding/generation/subject;
6. `RegionAuthority.Validate()` still sees the exact region generation and owner;
7. `RegionAuthority.ReleasePlatformMappingReservation()` succeeds;
8. the bridge records `LocalReservationReleased = true`.

If the final bridge acknowledgement unexpectedly fails after local release, the kernel attempts to re-reserve the region so the failure remains fail-closed.

`Completed`, `Cancelled`, `Faulted`, stale receipts, wrong-domain receipts, malformed receipts and legacy synchronous success are never reclaim proof.

### Host model

The host provider implements `IPlatformRegionRevocationProvider`.

Default host behavior completes the revocation operation immediately so existing synchronous tests/behavior stay compatible, but it still goes through ticket + completion receipt validation.

`deferRegionRevocationCompletion` is a host fault-injection/model knob for tests. It leaves the provider operation in `Draining` until `CompleteRegionMappingRevocation(...)` is invoked, allowing the bridge to prove that local authorization can be dead while the region remains pinned.

## Target state machine

Use one invariant across capabilities, mappings, future DMA grants, compute submissions and VM bindings:

```text
Active local authority
  -> LocallyRevoked / Exiting
       (no new effects)
  -> PlatformDraining
       (old external effects may still exist)
  -> PlatformClosed
       (completion receipt proves closure)
  -> LocalReclaimAllowed
```

Never interpret `LocallyRevoked` as proof that a device/IOMMU/accelerator can no longer touch memory.

## Remaining Phase-2 tasks

### 1. Generalize authorization-vs-closure outcome to process teardown

The mapping lifecycle now makes the distinction explicit. Process termination/fault still needs an aggregate internal outcome across all resource classes so callers cannot confuse local authorization death with external closure.

### 2. Add domain teardown orchestration

Replace the current all-or-nothing caller choreography with an explicit internal lifecycle:

```text
BeginProcessExit
  -> close process channels / cancel pending SIP waits
  -> revoke local capabilities for new effects
  -> begin closing mappings/DMA/compute/device/child-domain authority
  -> wait/observe platform completions
  -> close platform domain lease
  -> reclaim regions/domain state
  -> publish Exited/Faulted
```

Do not hide unbounded hardware drain inside a synchronous syscall. Preserve the Track A guarantee that pending SIP response waiters are deterministically cancelled when channels close.

### 3. Define aggregate fault containment

For provider `Faulted` during revoke:

- local authorization remains dead;
- affected resource remains pinned/reserved;
- process/domain cannot be reported fully exited/reclaimable;
- diagnostics identify semantic local mapping/operation state without exposing provider authority;
- a future platform-reset path, if one exists, must supply closure proof before reclaim.

### 4. Reuse composed generations for future resource classes

Validation for platform-visible effects must continue comparing the relevant independent epochs rather than flattening them into one counter:

```text
process generation
capability revocation state
region generation
local platform binding generation
provider lease generation / future HybridCPU domain epoch
operation generation
```

The mapping slice now composes local mapping, binding, region, provider mapping/domain and operation generations at the reclaim boundary.

## Code touched by the first Phase-2 slice

- new `src/Platform/SingPlus.Platform.Abstractions/PlatformRegionRevocationContracts.cs`;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs`;
- new `tests/SingPlus.Tests/Platform/PlatformRevocationLifecycleTests.cs`.

No HybridCPU-v2, DMA, scheduler, IRQ or SIP protocol surface is changed by this slice.

## Tests

First-slice coverage includes:

- capability revoke kills local authority immediately while deferred platform closure stays `Draining` and the region remains pinned;
- valid `Closed` receipt allows release only after exact local binding/region revalidation;
- draining re-entry does not start a second provider revocation operation;
- stale completion cannot release the reservation;
- wrong-domain completion cannot release the reservation;
- malformed completion faults closure and remains non-reclaimable;
- stale local mapping/binding/region identity cannot finalize reclaim;
- duplicate valid `Closed` observation is idempotent;
- legacy synchronous revoke without a completion receipt cannot authorize local reclaim;
- existing immediate host revocation behavior remains compatible.

Still required in later Phase-2 slices:

- termination cancels SIP waiters before waiting for platform drain;
- termination cannot reach `Exited` while any provider authority is merely `Draining`;
- committed SIP response stays committed even if later platform teardown fails;
- domain with multiple processes tears down only process-owned channels/resources until the final domain member exits;
- aggregate multiple-resource fault containment.

## Acceptance criteria

Phase 2 is **not complete yet**. The owned-region mapping/capability-revocation path now makes reclaim mechanically impossible before verified external closure. Phase 2 remains open until process exit/fault uses the same explicit lifecycle and ordering across all currently implemented resource classes.

## Do not do

- do not restore a revoked local capability because external revoke failed;
- do not free/reuse memory after a timeout unless the platform contract proves reset/revocation;
- do not model provider completion as a normal SIP response capability;
- do not assume synchronous device shutdown;
- do not treat legacy synchronous provider success as a substitute for a valid `Closed` receipt on the new reclaim path;
- no HybridCPU binding or DMA in this Phase-2 slice.
