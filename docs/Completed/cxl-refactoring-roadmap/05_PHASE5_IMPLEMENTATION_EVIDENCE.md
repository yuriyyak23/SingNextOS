# Phase 5 — Type-3 Memory Provider Evidence

## Implemented scope

**Complete for the single-host model backend.** `CxlType3ModelProvider`
implements discovery, semantic CXL.io projection, capacity, fabric and memory
roles. Capacity reservation, persistence class, proximity, latency and bandwidth
are semantic; provider materialization state remains private.

`CxlType3PlacementPlanner` implements deterministic policy selection with
explicit `CxlRequired`, `CxlPreferred` fallback and `LocalRequired` outcomes.
`CxlType3MemoryAuthority` attaches a Type-3 backing to an ordinary
`OwnedRegion`/`OwnedBuffer`, using a `DevicePrivate` RegionUse during its live
lifetime. It never creates a parallel `CxlRegion` ownership type.

The placement lifecycle is `Active -> MigrationRequired` on stale/hot-removed
backing, or `Active -> Draining -> Released`. Ambiguous closure is quarantined.
The owned region remains under `RegionAuthority`; active backing prevents MOVE
or reclaim. A platform mapping may coexist specifically with the backing use,
so device DMA still has to follow the existing platform mapping/device/IOMMU
contracts rather than receiving a CXL bypass.

Hot-remove and rebind advance only the selected endpoint's device, fabric and
memory generations. No global epoch is used. The model advertises memory but
not coherent access, so it cannot accidentally enable direct coherent output.

## Executable evidence

Focused Type-3, CXL bridge, RegionUse, platform mapping and DMA tests prove:

- required placement fails and preferred placement falls back when capacity is
  absent;
- an ordinary owned buffer remains CPU-visible through normal region APIs;
- live backing blocks premature reclaim and verified close restores reclaim;
- hot-remove yields controlled `MigrationRequired`, not dangling ownership;
- a second endpoint remains active when the first is removed;
- device/fabric/backing generations are explicit;
- CXL-backed memory accepts the ordinary platform mapping path and the model
  exposes no DMA-bypass interface.

Final Phase 5 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused Type-3/CXL/RegionUse/platform-mapping/DMA tests
  PASS — 53/53

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 801/801 total
    671 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Claims and Phase 6 entry

This is executable model evidence, not real firmware discovery, HDM programming,
persistence-domain proof or physical hotplug evidence. Those remain Phase 8
hardware gates. Phase 6 may consume the common operation lifecycle for a
Type-2 staged accelerator service.
