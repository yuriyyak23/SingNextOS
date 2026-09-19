# Phase 6 — CXL Type-2 Accelerator Service

Status: **Complete** for the staged model-provider scope. See
[06_PHASE6_IMPLEMENTATION_EVIDENCE.md](06_PHASE6_IMPLEMENTATION_EVIDENCE.md).

## Goal

Add the SingNextOS side of a CXL Type-2 accelerator provider without coupling applications to CXL transport or requiring HybridCPU-v2 changes. The provider must work through compute intent, region authority/use, device authority and the common external-operation lifecycle.

## Target path

```text
ComputeIntent
  -> ComputePlanner / provider selection
  -> CXL Type-2 accelerator service
  -> region/device/fabric materialization
  -> Submitted
  -> DeviceComplete
  -> Visible
  -> Published
  -> Released
```

## Device representation

The same physical Type-2 device may simultaneously appear as:

- a normal `DeviceResourceSet` participant for CXL.io-compatible MMIO/IRQ/DMA control;
- a coherent-access provider where CXL.cache semantics are available;
- a CXL.mem consumer/provider for device-attached memory as applicable;
- an accelerator execution provider consumed by the compute planner.

These are roles, not mutually exclusive device classes.

## Descriptor validation

SingNextOS validates provider-neutral compute descriptors before hardware submission:

- operation/service identity;
- input/output region authority;
- ranges and access modes;
- `RegionUse` compatibility;
- device lease/service authority;
- required security policy;
- selected effect/publication class;
- shape/size/alignment constraints required by the service.

CXL register, route, decoder and transport details are backend materialization data and must not be accepted from ordinary application descriptors.

## Staged output default

Default output model:

```text
accelerator executes
  -> device-private or staging destination
  -> DeviceComplete
  -> provider visibility/acquire
  -> revalidate RegionAuthority + MutationEpoch + generations
  -> copy/move/commit into destination
  -> Published
```

This permits stale-authority failures to discard/quarantine output before architectural/application publication.

## Direct coherent output

A direct CXL.cache/coherent write into the final region is allowed only when all of the following are proven:

- exact destination `RegionUse` permits direct coherent write;
- incompatible CPU/device writers are excluded;
- provider has a defined coherent access/visibility contract for all relevant agents;
- device/fabric/platform bindings are current at submit;
- completion and visibility semantics are sufficient for the consumer;
- Phase 7 effect/replay policy explicitly admits the operation;
- fault/reset handling cannot misrepresent an already-visible external write as rolled back.

Direct output is an optimization. Provider selection must be able to choose staged execution when these conditions are not met.

## CXL.cache vs IOMMU

Do not define CXL.cache access as always IOMMU-translated. The concrete backend records which hardware translation/protection mechanism applies. Software admission still derives from Sing authority and a valid device/service binding.

## Negative tests

- descriptor names raw CXL decoder/DPA -> reject at public API boundary;
- stale region use before submit -> no hardware effect;
- device reset during staged execution -> never publish result;
- direct coherent mode requested but only staged capability exists -> reject or select staged policy, never silently weaken semantics;
- completion arrives after operation generation reuse -> reject as stale;
- ordinary CXL.io driver path remains usable without compute-service authority.

## Acceptance criteria

- fake Type-2 provider executes a staged operation through the common lifecycle;
- same fake device can expose ordinary `DeviceResourceSet` resources independently of accelerator service;
- no L7-SDC/ISA/compiler implementation is required;
- direct coherent mode exists only behind explicit feature/policy gating;
- result publication goes through existing/extended SingNextOS memory authority, not device completion alone.

## External blockers

A later HybridCPU-v2 integration may use L7-SDC as a command carrier, but that work is outside this roadmap. Real Type-2 devices also require vendor/device-specific accelerator command ABIs above standard CXL transport; CXL itself does not define a universal accelerator command set.
