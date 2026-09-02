# Phase 7 — Native libraries and compatibility

## Goal

Preserve the Singularity lesson that a rich programming surface does not require a rich privileged ABI.

## Native API layering

```text
.NET-like source-facing API
-> SingNextOS library/SDK
-> generated typed SIP client
-> isolated system service
-> capabilities + ownership
-> minimal kernel/platform mechanisms
```

## Filesystem

`File`/`Stream`-like libraries should call `FileService` contracts. Path parsing, namespace policy, filesystem formats and compatibility file-descriptor semantics remain outside the kernel.

Large I/O should use owned buffers/regions with explicit MOVE/borrow rules.

## Networking

`Socket`-like libraries should call `NetworkService`. TCP/IP state and socket policy stay service-side. NIC drivers use the Phase 5 device/DMA model and cannot map client memory through ambient driver authority.

## Process/domain APIs

High-level process APIs wrap domain/process creation, admission, capability delegation and lifecycle. Native authority remains `DomainId`, process generation, capabilities, channels/regions and platform bindings rather than POSIX PID/signal semantics.

## UI and virtualization

Window/surface/input APIs and `VirtualMachine`-like APIs remain libraries over their respective SIP services. GPU command streams and VMCS fields are not kernel ABI.

## Compatibility personalities

Correct layering:

```text
POSIX / Win32 / Wine / VMX-compatible facade
-> native SingNextOS library/service contract
-> native capability/ownership authority
```

A compatibility personality:

- may translate semantics;
- may copy data;
- may emulate legacy behavior;
- MUST NOT mint independent root authority;
- MUST NOT bypass native ownership/protocol checks.

## Library dependency rule

A source-facing convenience library may be large and ergonomic, but it may only consume public contracts and local pure-library helpers. It must never bind raw provider leases or hardware identities.

## Tests

- source-facing API path reaches generated SIP, not an ad-hoc privileged call;
- missing capability fails at native service boundary;
- service crash cancels outstanding high-level operations correctly;
- compatibility layer has no more authority than underlying native session;
- large mutable I/O obeys ownership lifecycle.

## Acceptance criteria

At least one native service family exposes a source-familiar library API while keeping all policy and compatibility semantics outside the privileged ABI.
