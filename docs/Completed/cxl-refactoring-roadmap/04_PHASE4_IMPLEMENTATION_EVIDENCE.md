# Phase 4 — CXL Authority and Provider Evidence

## Status and delta audit

**Complete for the Phase 4 interface and fake-provider scope.** The existing
platform authority path already supplied domain, semantic device lease and
`DeviceResourceSet` enforcement, but no CXL-specific provider boundaries or
narrow fabric/memory/coherent generations existed.

## Implemented boundary

`CxlProviderContracts.cs` defines six independent roles:

- discovery;
- CXL.io semantic-device projection;
- fabric binding;
- memory binding;
- coherent-access binding;
- security evidence.

There is deliberately no aggregate `ICxlProvider`. Public records expose only
semantic endpoint properties and opaque binding/generation handles. HDM, HPA,
DPA, decoder, route, register, mailbox, requester and PASID details are absent
from the public CXL surface.

`CxlAuthorityBridge` materializes bindings only after validating the existing
`RegionUse`, platform-domain subject and device lease. CXL.io resolves to an
ordinary `PlatformDeviceIdentity`, so MMIO/IRQ/DMA continue through the existing
`DeviceResourceSet` path. Security evidence has a read-only method and cannot
create any region, device, fabric, memory or coherent authority.

Before an effect or publication, the bridge revalidates the exact region use,
device generation, endpoint feature set and affected fabric/memory/coherent
binding generations. Invalidation is object-scoped; no global fabric epoch was
introduced.

## Executable evidence

`CxlAuthorityBridgeTests` and the focused existing authority tests prove:

- six roles remain independently mockable and no monolithic interface exists;
- the CXL.io identity is the same semantic identity held by an existing device
  lease;
- stale device generation rejects before the fabric provider is invoked;
- stale fabric and memory generations fail revalidation;
- loss of a coherent feature invalidates a selected coherent path;
- trusted security evidence cannot substitute for a valid `RegionUse`;
- existing device lease, driver resource-set, external-operation and RegionUse
  behavior remains intact.

Final Phase 4 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused CXL/device/resource-set/lifecycle/RegionUse tests
  PASS — 43/43

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 795/795 total
    665 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Claims and Phase 5 entry

Current claim: the provider boundary and fake-provider authority chain are
executable without CXL hardware. This is not evidence of register programming,
firmware discovery, real link security or real persistent-media behavior.

`FutureGated`: hardware identities and protocol details remain provider-private;
real discovery/configuration belongs to Phase 8. Phase 5 may implement a
single-host Type-3 model provider using these narrow roles.
