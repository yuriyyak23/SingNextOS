# Phase 11 — GUI/GPU/display ownership stress test

## Status

**Architectural stress test after memory/device/completion primitives exist.** A CPU-only host compositor can be prototyped earlier. Hardware GPU/display integration remains future/provider-gated until a real backend is proven.

## Goal

Prove that the same capability/ownership substrate can support interactive graphics without a privileged global shared framebuffer.

Target protocol:

```text
App owns SurfaceBuffer
  -> Present
  -> compositor gets ownership or bounded read authority
  -> GPU/display gets exact bounded device grant when available
  -> completion / release fence
  -> revoke/unmap device authority
  -> app reacquires exclusive write authority
```

`Present` is a service ownership protocol, not a framebuffer syscall.

## Standard service split

Keep independent typed SIP contracts for:

| Service | Responsibility |
|---|---|
| Display | outputs, modes, imported/presentable buffers, scanout completion |
| Compositor | composition, surface ownership/leases, frame scheduling |
| Window Manager | placement, focus, z-order and policy |
| Input | normalized input events and routing |
| Clipboard | mediated data transfer/consent/capability policy |
| Font/Text | shaping/rasterization/font resources |
| Accessibility | semantic accessibility graph/events |
| Notification | mediated notifications |
| Shell | session/system UI policy |

No single giant GUI server contract is required.

## Surface buffer model

Define `SurfaceBuffer` as a typed service abstraction backed by an owned region plus immutable metadata:

```text
format
width/height
stride/layout
plane descriptions
region slice(s)
producer generation
```

The kernel does not understand pixel formats beyond what is required to validate bounded region slices. Format negotiation belongs to Display/Compositor services.

## Present ownership modes

Support at least two semantic modes without changing kernel primitives:

### Transfer mode

App MOVE-transfers buffer authority to compositor. The app cannot mutate it until ownership is returned.

### Read-lease mode

App retains ownership but grants a bounded read-only borrow/lease to compositor. App write access is blocked for the lease lifetime.

Which mode is used is a service contract decision. Both rely on existing ownership generations and Phase 4 external mapping rules.

## Double/triple buffering

No new kernel primitive is needed:

```text
App renders B while A is presented
App renders C while B waits
A returns after release completion
```

Each buffer is a separate owned region/lease. Frame scheduling is compositor policy.

## GPU/display integration

When a real provider exists:

1. compositor validates surface authority;
2. bridge creates exact GPU read/write grants only for required planes/ranges;
3. `PrepareForConsumer` publishes/maintains CPU writes;
4. GPU composition returns completion;
5. display scanout receives bounded read grant if direct scanout is supported;
6. release fence/completion closes scanout authority;
7. `AcquireFromConsumer` and revoke occur;
8. buffer ownership/write authority returns to app.

Direct scanout/physical zero-copy is an optimization discovered from provider features, not a semantic requirement.

## Input

Raw IRQ belongs only to a device/input service using Phase 5. Applications receive normalized typed events over SIP/channel/event primitives.

Focus/global shortcut/security policy belongs to Window Manager/Shell/Input services, not to interrupt handlers in the kernel.

## HybridCPU-v2 requirements

The audit found generic I/O-domain/DMA/accelerator/completion ingredients but no code-confirmed complete GPU/display/scanout subsystem. Therefore initial SingNextOS GUI work should use a host/CPU display provider and keep hardware presentation behind a semantic optional provider.

Only request HybridCPU core changes if a concrete display/GPU backend cannot be expressed through existing memory/I/O/accelerator grant mechanisms.

Potential future feature contracts:

```text
SurfaceImport
SurfacePresentation
DisplayScanout
ReleaseFence
GpuCompute/Blit profile
```

Do not invent these as claims before a backend exists.

## Tests

- app cannot mutate a buffer while exclusive transfer/read lease forbids it;
- compositor crash returns/cancels buffers deterministically or leaves them pinned until external grants close;
- stale surface generation cannot be presented;
- GPU grant is limited to exact planes/ranges and required direction;
- release completion required before app regains write authority;
- triple buffering works without global framebuffer sharing;
- direct-scanout unsupported falls back to composition/copy without changing ownership semantics;
- raw input IRQ never reaches ordinary app API;
- Window Manager cannot use compositor/display device authority unless separately delegated.

## Acceptance criteria

Phase 11 is complete architecturally when CPU-only Present, ownership return and service separation are proven. Hardware completion requires a real provider path with exact grants, visibility and release fences; until then hardware zero-copy/display claims remain future-gated.

## Do not do

- no global shared framebuffer;
- no app-visible GPU DMA pointer;
- no giant kernel window manager;
- no universal GPU ABI in HybridCPU core without a concrete backend;
- no “Present implies zero-copy” guarantee.
