# 02. Virtual I/O, Events And CXL.io

## Goal

Complete the ordinary CXL.io -> DeviceLease -> VirtualIo path and make guest-visible completion publication flow through virtual-domain authority instead of directly from physical interrupts.

## CXL.io composition

```text
CXL endpoint -> PlatformDeviceIdentity -> DeviceLease
 -> bounded VirtualIo lease -> exact VirtualDomain
```

No guest-visible CXL endpoint/fabric identity is introduced. VirtualIo bounds remain an exact subset of the parent `DeviceLease` rights and transfer limits.

## Secure virtual I/O

For a `SecureExecutionBinding`, an I/O effect requires both the exact VirtualIo lease and secure-I/O admission for the exact secure execution binding. Neither token is reinterpreted as the other.

## Event publication

Wire `HybridCpuPlatformAuthorityProvider` and the executable adapter through the HybridCPU ExternalRuntime child-event contract when the external implementation supports it end-to-end.

Required sequence:

```text
physical/provider completion
 -> ExternalOperation DeviceComplete
 -> visibility acquire
 -> security + virtual/CXL generation revalidation
 -> Published
 -> InjectVirtualEvent
 -> guest-visible event
```

A physical MSI/MSI-X/CXL interrupt is never proof of guest completion or publication authority.

## Faults

Device hot-remove, CXL.io generation change, VirtualIo revocation or secure-I/O policy loss stops new guest I/O effects immediately. Existing effects close/contain before device or region authority is reclaimed.

## Required tests

- CXL.io device lease composes with bounded VirtualIo;
- stale VirtualIo generation rejects effect before provider access;
- rights/transfer limits cannot amplify parent lease;
- event injection before visibility/publication is rejected;
- security loss after completion but before publication suppresses guest event;
- physical interrupt cannot bypass virtual event contract;
- ambiguous VirtualIo close blocks domain/device reclaim.
