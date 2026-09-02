# Phase 4 — Manifests, discovery and sessions

## Goal

Adapt Singularity's manifest/binder/component discipline into an explicit SingNextOS service model while keeping authority capability-native.

## ServiceManifest

Introduce a declarative manifest model capable of expressing at least:

```text
ComponentIdentity
ComponentVersion
Executable/Image digest
Entrypoint
ProvidedContracts[]
RequiredContracts[]
PlatformRequirements[]
ResourceRequirements[]
MemoryProfile
Optional protocol/contract digests
```

Example driver requirements:

```text
provides: BlockDevice.v1
requires:
  DeviceClass.Nvme
  MmioRegion(count=1)
  Irq(count<=4)
  DmaMapping.v1
  ExplicitMemoryVisibility.v1
```

## Critical rule

```text
manifest != authority
```

A manifest says what a component requires to run. It does not authorize any specific device, region, IRQ, DMA window, service or platform lease.

## Service discovery

Discovery returns identity/contract metadata only:

```text
Resolve(serviceName/contract)
  -> ServiceIdentity
  -> ContractDescriptor
  -> endpoint discovery metadata
```

It MUST NOT mint an unrestricted channel or provider token merely because a name was resolved.

## EndpointSession

Connection/admission produces a capability-scoped session:

```text
EndpointSession
  = service endpoint
  + caller domain/process generation
  + contract version/digest
  + granted local capabilities
  + protocol state
  + session generation
```

Session creation is the authority boundary; discovery is not.

## Binding/admission

Startup flow:

```text
verify image identity
-> verify/parse manifest
-> resolve required contracts/features
-> create domain/process
-> grant exact local capabilities/resources
-> bind platform domain if required
-> create admitted endpoint sessions
-> start component
```

A missing required feature MUST fail admission rather than silently widening authority or substituting an incompatible backend.

## Tests

- manifest requesting DMA receives no DMA without a local grant;
- service name resolution alone cannot perform service operations;
- wrong contract version/digest rejected;
- stale session generation rejected;
- provider availability failure cannot be represented as successful admission;
- a manifest cannot encode raw provider lease IDs or physical addresses as authority.

## Acceptance criteria

At least one service/driver can be launched from declarative requirements and receives only explicitly granted resources; discovery and admission must be testably separate.
