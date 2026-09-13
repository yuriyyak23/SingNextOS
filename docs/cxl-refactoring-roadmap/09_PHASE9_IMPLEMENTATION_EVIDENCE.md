# Phase 9 — Fabric Manager, Pooling and Reconfiguration Evidence

## Implemented scope

**Complete for the software/model scope.** `ICxlFabricManagementProvider`
exposes semantic resource state, pool capacity, opaque reconfiguration tickets
and P2P feasibility evidence. Physical topology, switch ports, decoder and LD/MLD
identity remain provider-private.

`CxlFabricManagerAuthority` closes admission by changing a binding to
`Draining` before asking the provider to change its generation. It drains
Prepared, Admitted, Submitted, DeviceComplete and Visible operations through
the common cancellation/provider-loss lifecycle. A replacement binding reopens
only after exact ticket and replacement-generation validation; the old opaque
binding remains deterministically stale.

Pool capacity assignment acquires a `DevicePrivate` RegionUse against the
ordinary owned region. It therefore cannot transfer ownership implicitly and
must release the assignment before MOVE/reclaim. P2P remains an optimization:
both provider route support and materialized platform isolation are mandatory.

The Type-3 model now supports object-scoped drain/rebind, pool assignment and
peer-route evidence. Reconfiguring one endpoint does not change another
endpoint's binding generation.

## Executable evidence

Focused fabric, Type-3, Type-2, RegionUse and lifecycle tests prove:

- admission closes before old binding reuse;
- all five unpublished lifecycle stages drain without publication;
- old generation rejects after replacement;
- unrelated endpoint generation remains valid;
- live pool assignment blocks ownership MOVE until explicit release;
- P2P rejects missing isolation or route support;
- staged provider loss after completion is discardable and reclaimable.

Final Phase 9 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused fabric/Type-3/Type-2/RegionUse/lifecycle tests
  PASS — 42/42

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 821/821 total
    691 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Scope boundary

Real Fabric Manager, switch and hardware fault injection adapters remain part
of the explicitly skipped QEMU/physical-hardware scope. Phase 10 keeps
multi-host writable ownership disabled until a separately reviewed distributed
authority protocol exists.
