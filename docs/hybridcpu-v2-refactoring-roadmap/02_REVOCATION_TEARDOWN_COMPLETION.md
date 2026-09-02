# Phase 2 — Revocation, teardown and completion lifecycle

## Status

**Immediate after Phase 1.** This is the most important security refactoring before real DMA/device integration.

## Current state

The current bridge already has a strong local pattern:

```text
MappingState.Active
  -> MappingState.Draining
  -> MappingState.Revoked
```

and `RegionAuthority` reservations prevent transfer/loan/release while platform mapping authority exists.

However `RuntimeKernel.RevokeCapability()` currently revokes the local capability first, removes it from processes, then cascades provider mapping revocation. A provider failure can therefore leave the system in a valid-but-subtle state:

```text
local authorization dead
external authority still draining/faulted
resource reclaim still forbidden
```

That state is correct and safer than rolling local authority back, but it must become explicit rather than being represented only by a failed return code.

Current `TerminateProcess`/`FaultProcess` also refuse cleanup while platform authority is active. This is conservative, but a real device/runtime path needs a structured way to initiate and finish teardown.

## Target state machine

Use one invariant across capabilities, mappings, DMA grants, compute submissions and VM bindings:

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

## Refactoring tasks

### 1. Separate authorization state from external closure state

Do not roll back a local capability revoke if the provider cannot close immediately.

Instead introduce an explicit outcome/snapshot, for example:

```text
CapabilityRevocationOutcome
  LocalAuthorizationRevoked = true
  PlatformClosure = None | Draining | Closed | Faulted
  PendingOperations = [...]
```

The exact type can remain kernel-internal if no public caller needs it. What matters is that tests and lifecycle code cannot confuse a provider fault with a live local capability.

### 2. Refactor `PlatformAuthorityBridge` around operation receipts

For every stateful mapping/grant:

- `Begin...Revocation` marks the record `Draining` before invoking provider closure;
- new submissions validate and fail on `Draining`;
- provider completion is identity/generation checked;
- `Closed` is published only from a valid terminal receipt;
- region mapping reservation is released only after `Closed`;
- a `Faulted` closure remains non-reclaimable unless a stronger platform reset contract explicitly proves revocation.

### 3. Add domain teardown orchestration

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

Do not hide unbounded hardware drain inside a synchronous syscall. A host provider may complete immediately; a real provider may return pending completion that is observed via kernel event/completion machinery.

Preserve the Track A guarantee that pending SIP response waiters are deterministically cancelled when channels close.

### 4. Define fault containment

For provider `Faulted` during revoke:

- local authorization remains dead;
- affected resource remains pinned/reserved;
- process/domain cannot be reported fully exited/reclaimable;
- diagnostics identify the exact opaque operation/lease without leaking provider authority;
- a future platform-reset path, if one exists, must supply a closure receipt before reclaim.

### 5. Compose generations instead of inventing a global generation

Validation for platform-visible effects should compare all relevant epochs:

```text
process generation
capability generation/revocation epoch
region generation
local platform binding generation
provider lease generation / HybridCPU domain epoch
operation generation
```

Any mismatch invalidates the operation. Do not flatten these into one counter.

## Likely code touched

- `RuntimeKernel.RevokeCapability`;
- `RuntimeKernel.TerminateProcess` / `FaultProcess`;
- `RuntimeKernel.Platform.cs`;
- `PlatformAuthorityBridge`;
- `RegionAuthority` reservation/reclaim hooks;
- platform host provider fault-injection support;
- teardown tests next to `ResponseClientAdapterTeardownTests` and platform bridge tests.

## Tests

Required negative tests:

- local revoke succeeds, provider revoke faults: capability cannot authorize new effect and region cannot reclaim;
- draining mapping rejects new operation;
- stale completion cannot close a newer mapping;
- duplicate completion is harmless/fail-closed;
- termination cancels SIP waiters before waiting for platform drain;
- termination cannot reach `Exited` while any provider authority is merely `Draining`;
- committed SIP response stays committed even if later platform teardown fails;
- domain with multiple processes tears down only process-owned channels/resources until the final domain member exits.

## Acceptance criteria

Phase 2 is done when reclaim is mechanically impossible before a verified external closure, while local revocation immediately prevents all new authority uses. This lifecycle becomes reusable unchanged by DMA, accelerators, virtualization and display.

## Do not do

- do not restore a revoked local capability because external revoke failed;
- do not free/reuse memory after a timeout unless the platform contract proves reset/revocation;
- do not model provider completion as a normal SIP response capability;
- do not assume synchronous device shutdown.
