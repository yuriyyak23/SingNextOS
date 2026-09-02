# Phase 10 — Native API and system services

## Status

**Service-layer expansion after the authority substrate is stable.** Some host-only services may be prototyped earlier, but they must not create alternative low-level authority mechanisms.

## Goal

Make the native Sing+ programming model source-familiar and capability-native:

```text
.NET-like source API
  -> generated typed SIP client
  -> isolated service SIP
  -> explicit capabilities + ownership
  -> minimal kernel authority
```

Win32/POSIX/Wine remain optional compatibility personalities over this model.

## Track A foundation to preserve

Current master already provides generated typed client/runtime response adaptation and teardown semantics. System services should reuse that path rather than hand-writing transport-specific clients.

Where generator extensions are needed, extend typed metadata for semantic needs such as:

- returned ownership;
- bounded payloads;
- cancellation;
- service-specific capability requirements;
- future explicit borrow/read-only grant metadata.

Do not turn transport details into the source-facing API.

## Filesystem

Source-familiar shape:

```text
File / Stream-like library
  -> FileService SIP
  -> FileObject capability
  -> OwnedBuffer/OwnedRegion for large I/O
```

Service authority owns filesystem namespace/object policy. Kernel only enforces capabilities, channels, ownership and platform mappings needed by the service.

Do not put pathname parsing, mount policy, filesystem formats or POSIX file descriptor semantics into kernel ABI.

## Networking

Source-familiar shape:

```text
Socket-like library
  -> NetworkService SIP
  -> Socket/Endpoint capability
  -> owned packet buffers / bounded rings where appropriate
  -> device service + DMA grants underneath
```

TCP/IP state, socket options and protocol stacks stay service-side. NIC access uses Phase 5 device/DMA authority; client buffers are never mapped through ambient driver authority.

## Process/domain management

High-level process API wraps:

- process/domain creation;
- admission;
- capability delegation;
- start/park/resume/termination;
- child relationship/policy where useful.

The underlying kernel objects remain `DomainId`, process generation, capabilities, channels/regions and platform domain bindings. No POSIX PID/signal semantics are required as the native substrate.

## Device services

Device protocol logic belongs in isolated SIP services. A hand-written capability-declared driver is the first practical implementation.

Progression:

```text
hand-written SIP driver
  -> generated SIP/state metadata
  -> declarative register/queue descriptors for repeated families
  -> generated validation/access code
```

Do not block real drivers on a universal hardware-description language.

## Virtualization

High-level `VirtualMachine`-like library calls the Phase 8 `VirtualizationService`. VMCS/VMX personalities remain separate compatibility adapters.

## UI

High-level window/surface/input APIs call the Phase 11 service family. Window styles, widget models, text shaping and shell policy are not kernel ABI.

## Compatibility personalities

Correct layering:

```text
legacy API
  -> compatibility/personality library or SIP service
  -> native Sing+ service contracts
```

A compatibility layer may translate semantics, copy buffers or emulate legacy behavior, but it cannot mint authority or bypass ownership/capability checks.

## Kernel ABI boundary

Keep privileged ABI focused on neutral mechanisms:

```text
domain/process lifecycle
capability mint/delegate/revoke/check
owned region lifecycle
typed channel/event transport
platform binding/mapping/grants
completion/revocation
minimal trap/interrupt entry and event routing
```

Do not add kernel calls for:

- paths/directories;
- TCP state/socket personalities;
- window placement/z-order;
- GPU command streams;
- VMCS fields;
- HybridCPU lanes/opcodes.

## Tests

For each service family, require:

- generated typed client path, not ad-hoc transport;
- missing resource capability denied;
- large mutable payload ownership/borrow semantics tested;
- malformed service response rejected before publication;
- service crash/termination cancels outstanding calls and returns/reclaims ownership according to protocol;
- provider/device denial cannot be widened into service success;
- compatibility personality cannot obtain more authority than the underlying native service contract.

## Acceptance criteria

Phase 10 is complete incrementally as service families appear, but each service is considered native only if its source API compiles to typed SIP and all resource authority remains explicit. No service should require Win32/POSIX as the privileged substrate.

## Do not do

- no giant syscall surface;
- no binary compatibility as the native design goal;
- no shared global mutable buffers as a convenience shortcut;
- no service-owned provider tokens exposed to clients;
- no compatibility personality with independent root authority.
