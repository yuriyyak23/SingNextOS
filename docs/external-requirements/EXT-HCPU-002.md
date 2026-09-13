# EXT-HCPU-002

**Status:** External Blocked

## Required external capability

The external HybridCPU platform integration must eventually provide concrete bindings for SingNextOS HAL/driver abstractions that map console/timer and future MMIO/IRQ/DMA capabilities to the platform's already existing hardware/runtime interfaces.

## Why SingNextOS needs it

SingNextOS deliberately keeps kernel business logic independent of host APIs and independent of HybridCPU-specific ABI details. Real hardware execution of driver abstractions therefore requires an external platform implementation of the local HAL contracts.

## Existing interface expected

Existing HybridCPU platform/runtime interfaces for console output, timer delivery, MMIO mapping, IRQ signaling, and DMA configuration, consumed as black-box platform capabilities. This requirement does not request new instructions, ISE changes, emulator changes, runtime ABI changes, loader changes, or compiler/backend work.

## Minimal reproduction

1. Build SingNextOS driver and kernel HAL abstractions from this repository.
2. Supply an external implementation of the existing SingNextOS HAL interfaces in an integration-only layer.
3. Exercise console/timer behavior and capability checks through the same SingNextOS runtime contracts used by host tests.
4. For MMIO/IRQ/DMA, bind only to platform capabilities that already exist externally; if no such interface exists, keep those bindings External Blocked.

## SingNextOS component blocked

Only real HybridCPU-backed hardware driver execution. The local `IConsoleDriver`, `ITimerDriver`, `IKernelConsole`, driver manifests, capability descriptors, host implementations, and their tests remain fully local and are not blocked.

## Fallback/mock used

`HostKernelConsole` and host console/timer driver implementations inside
SingNextOS, plus capability-aware runtime tests. The current HybridCPU adapter
has a tested neutral semantic IRQ binding, but no generic timer binding. DMA is
limited to bounded authority/visibility integration against merged HybridCPU
`9e001bf...`, pinned by CI; HybridCPU exposes no neutral
submit/completion/cancel API.
The Phase-6 DMA-completion `KernelEventEndpoint` path is therefore a local/model
notification over a test provider. Its cancellable asynchronous wait is likewise
local runtime consumption, not a generic HybridCPU timer/event wait, hardware
delivery or fabricated WFE/SEV ABI.
