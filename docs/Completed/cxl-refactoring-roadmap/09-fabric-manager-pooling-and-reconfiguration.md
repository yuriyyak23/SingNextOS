# Phase 9 — Fabric Manager, Pooling and Reconfiguration

Status: **Complete for the software/model fabric-management scope.** Production
hardware/FM adapters are skipped with Phase 8. See
[09_PHASE9_IMPLEMENTATION_EVIDENCE.md](09_PHASE9_IMPLEMENTATION_EVIDENCE.md).

## Goal

Add dynamic CXL fabric/pooling support only after single-host Type-3/Type-2 paths and generation-safe reclaim are working. Fabric Manager events are treated as platform reconfiguration, not application authority.

## Scope

This phase covers:

- switched CXL fabrics;
- dynamic device/memory binding;
- memory pooling and capacity assignment;
- multi-level switch topology;
- resource unbind/rebind;
- peer-to-peer feasibility where supported;
- host/fabric fault containment.

Multi-host shared-memory ownership semantics remain gated until Phase 10.

## Fabric model

Use provider-private topology objects plus semantic exported facts:

```text
FabricNode / Port / Endpoint        provider-private identities
FabricBinding                      opaque platform binding
FabricBindingGeneration            narrow invalidation counter
FabricResourceState                available/bound/draining/faulted
```

Application/region APIs see only semantic allocation/placement outcomes and normal region authority.

## Fabric Manager interaction

The Fabric Manager/provider may report or request:

- resource assignment;
- decoder/window changes;
- LD/MLD binding changes;
- capacity changes;
- path/topology changes;
- device removal/reset;
- fault or isolation state.

SingNextOS must translate those observations into controlled platform binding updates. The FM is not allowed to mutate `RegionAuthority` directly.

## Reconfiguration sequence

For any binding that must change while operations/regions exist:

1. mark affected binding `Draining` and stop new admissions;
2. bump/replace `FabricBindingGeneration` before any old binding may be reused;
3. cancel/drain in-flight operations according to lifecycle/effect class;
4. wait for or force local `RegionUse` release as policy permits;
5. migrate/revoke affected memory regions through the normal region layer;
6. perform backend unbind/rebind;
7. create new binding generation;
8. reopen admission only after provider and platform state are consistent.

Do not silently reuse an old opaque binding object after reconfiguration.

## Pooling semantics

Memory pool membership is a provider/platform property. Allocation from a pool still yields normal `OwnedRegion` authority scoped to one host/owner according to current policy.

Pool capacity reassignment must not transfer Sing ownership implicitly. Reassignment requires reclaim/release of old host bindings first.

## Peer-to-peer

P2P is a provider optimization only when:

- both device/resource authorities exist;
- the fabric reports supported routing/access;
- memory/access protection is materialized correctly;
- visibility/publication semantics are defined;
- virtualization/isolation policy permits it.

Otherwise route through existing host-visible staging/DMA paths.

## Negative tests

- FM rebind while operation is `Admitted` but not submitted -> old admission fails/rebinds before hardware effect;
- FM rebind during staged execution -> no publish from stale binding;
- pool capacity revoked with live region -> drain/migrate/revoke, never silent ownership transfer;
- unrelated port change -> unaffected binding generations remain valid where backend supports narrow scope;
- P2P requested without platform isolation/route support -> reject/fallback;
- old opaque binding reused after reconfiguration -> deterministic test failure.

## Acceptance criteria

- reconfiguration is represented by generation/lifecycle events, not ad-hoc CXL callbacks into applications;
- memory pooling preserves ordinary `OwnedRegion` ownership semantics;
- local fabric changes do not require global epoch invalidation when narrower scope is known;
- fault/reclaim tests cover bind/unbind/reset during every lifecycle stage.

## External blockers

Production support depends on available Fabric Manager interfaces, switch/device capabilities, firmware ownership model and hardware that exposes dynamic pooling/binding features.
