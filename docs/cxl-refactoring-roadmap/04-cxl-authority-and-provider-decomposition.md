# Phase 4 — CXL Authority Model and Provider Decomposition

Status: **Complete** for the interface/fake-provider scope. See
[04_PHASE4_IMPLEMENTATION_EVIDENCE.md](04_PHASE4_IMPLEMENTATION_EVIDENCE.md).

## Goal

Introduce CXL-specific **platform bindings and evidence** underneath the existing SingNextOS authority model. This is the first phase that should add CXL-named production types.

## Provider decomposition

Do not create one `ICxlProvider` with cross-layer authority. Prefer narrow interfaces (names provisional):

```text
ICxlDiscoveryProvider
ICxlIoProvider                // ordinary PCIe-compatible device resources
ICxlMemoryProvider            // CXL.mem placement/backing/window lifecycle
ICxlCoherentAccessProvider    // CXL.cache / coherent-access properties and binding
ICxlFabricProvider            // topology/bind/unbind/reconfiguration
ICxlSecurityEvidenceProvider  // IDE/authentication/security status only
```

`DeviceResourceSet`, memory management, compute planning, SecureCompute and virtualization consume only the interface they need.

## Authority chain

```text
Sing capability
+ exact RegionAuthority
+ DeviceLease / DeviceResourceSet authority
+ RegionUse
        |
        v
PlatformAuthorityBridge
        |
        v
provider-neutral operation/binding request
        |
        v
CXL/PCIe/IOMMU/fabric backend materialization
        |
        v
hardware-enforced configuration where supported
```

CXL packets are not assumed to carry Sing capability tokens.

## Provider-private identities

The following must remain below the platform/provider boundary unless diagnostics require a privileged projection:

- HDM decoder index;
- HPA-to-DPA/interleave details;
- DPA values;
- switch/port route IDs;
- MLD/LD binding details;
- CXL component-register offsets;
- CCI/mailbox command encodings;
- PCIe requester IDs/PASIDs used only for hardware materialization.

Application-facing APIs may ask for semantic placement properties (capacity, persistence, latency class, sharing policy, coherent accessibility) but not these physical identities.

## Minimal generation model

Capture only generations whose change invalidates the binding:

```text
RegionGeneration / authority identity     existing region layer
MutationEpoch                             per region
PlatformMappingGeneration                existing platform mapping/domain layer
DeviceGeneration                         per device/function/lease root
FabricBindingGeneration                  per affected CXL binding/window/topology object
OperationGeneration                      completion/cancellation identity
```

Do not introduce global `CxlFabricEpoch` unless the backend cannot provide narrower invalidation.

### Invalidation events

At minimum:

- device reset / function reset -> `DeviceGeneration` changes;
- link loss that invalidates device/resource binding -> device and/or affected fabric binding generation changes;
- HDM decoder/window change -> affected `FabricBindingGeneration` changes;
- Fabric Manager bind/unbind/reassignment -> affected binding generation changes;
- memory rebind/hot-remove -> backing/binding generation changes;
- IOMMU/domain remap for DMA path -> existing platform mapping/domain generation changes;
- ownership MOVE or incompatible CPU mutation -> `MutationEpoch`/region authority changes;
- operation cancel/reuse -> new `OperationGeneration`.

## Revalidation points

### Before first hardware effect / submit

Require fresh:

- region authority + `RegionUse`;
- device lease/generation;
- required platform mapping/domain generation;
- required fabric/memory binding generation;
- provider capability still valid for the selected mode.

### Before publication

Revalidate all assumptions that publication relies upon, especially:

- region authority and mutation epoch;
- destination/output `RegionUse`;
- operation identity;
- visibility completion;
- device/fabric generation if stale state could make completion ambiguous;
- platform mapping/domain generation if the operation result depended on that translated path.

A stale generation after irreversible direct write cannot erase the effect; it transitions to fault/recovery policy rather than pretending rollback occurred.

## CXL.io path

CXL.io functions should flow through the existing PCIe-compatible `DeviceResourceSet` model for MMIO/IRQ/DMA. CXL discovery may enrich feature records, but ordinary drivers should not need a parallel CXL device-capability system.

## Invariants

- discovery evidence is not access authority;
- a fabric binding is created only from existing Sing/platform authority;
- hardware enforcement can narrow software authority but never widen it;
- CXL cache/memory reachability cannot resurrect a revoked region/device capability;
- physical fabric identity never becomes an ordinary SIP capability.

## Negative tests

- stale device generation before submit -> reject before hardware effect;
- decoder/binding generation changes before submit -> reject/rematerialize;
- stale binding after completion but before staged publication -> discard/quarantine, never publish;
- provider claims coherence after link capability changed -> selected coherent path invalidated;
- security provider reports trusted device without region authority -> operation still rejected.

## Acceptance criteria

- narrow provider interfaces can be mocked independently;
- one CXL.io device can be represented through existing `DeviceResourceSet` without accelerator APIs;
- all CXL physical identities are confined to platform/provider-internal types;
- generation snapshots are explicit and unit-tested.

## External blockers

Official CXL register/protocol definitions and platform firmware discovery details are needed for a hardware backend, but not for interface/fake-provider implementation.
