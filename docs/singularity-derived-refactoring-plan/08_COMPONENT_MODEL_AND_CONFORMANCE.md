# Phase 8 — Component model and conformance

## Goal

Make the relationship between process/domain identity, service contracts, manifests, resource authority and platform lifecycle explicit enough to reason about, test and operate as one component lifecycle.

This does **not** require one giant `ComponentInstance` class. It requires a coherent model and queryable state.

## Logical ComponentInstance

A live component is defined by the association of:

```text
Component identity/version
Executable/image identity or digest
Domain identity
Process handle + generation
Lifecycle state
ServiceManifest
Provided/required contracts
Granted capabilities
Owned regions
Open endpoint sessions/channels
Device resources where applicable
Platform domain lease where applicable
Pending external operations
```

These identifiers remain separate namespaces.

## Lifecycle

Recommended semantic states:

```text
Declared
Admitting
Created
Starting
Running
Draining
Stopped
Faulted
Reclaimable
```

Exact names may align with existing process/domain state types, but lifecycle transitions must have explicit entry/exit conditions.

## Startup

```text
verify component/image
-> validate manifest
-> resolve required contracts/features
-> allocate domain/process
-> grant local capabilities/resources
-> bind platform domain/resources
-> create service sessions
-> start
```

Failure at any step must unwind already-created authority without leaking partially live provider resources.

## Shutdown

```text
mark draining
-> reject new sessions/effects
-> close service sessions
-> revoke local capability paths
-> drain external operations
-> close provider resources/domain
-> reclaim regions/resources
-> finalize state
```

## Conformance suite

Add a cross-cutting test matrix for:

- namespace separation of all IDs/generations;
- forbidden assembly/project dependencies;
- manifest-is-not-authority;
- discovery-is-not-authority;
- session/protocol generation staleness;
- ownership and provider mapping divergence;
- teardown ordering and reclaim safety;
- driver resource confinement;
- no provider token in SIP payloads;
- no raw physical address/lane/opcode/VMCS authority in public contracts;
- truthful feature/readiness claims;
- compatibility personalities not widening authority.

## Migration order

1. architecture/dependency tests;
2. contract protocol metadata;
3. ownership lifecycle integration;
4. manifest/discovery/session admission;
5. driver resource vertical slice;
6. unified completion adoption;
7. native service/library adoption;
8. component-wide conformance and deletion of temporary exceptions.

Do not block the existing HybridCPU Phases 3–5 waiting for the full component model. Introduce only the portion needed by each vertical slice, while keeping the final lifecycle model consistent.

## Acceptance criteria

The system must be able to explain, for any admitted service/driver instance, which identity it is running as, which contracts it provides, which local capabilities/resources it owns, which platform authority is still live, which operations are draining and when local reclaim becomes legal.
