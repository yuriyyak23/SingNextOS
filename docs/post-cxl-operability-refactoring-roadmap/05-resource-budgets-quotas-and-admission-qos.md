# Phase 5 — Resource Budgets, Quotas, and Admission QoS

## Goal

Bound resource consumption and establish predictable multi-service admission without introducing a new authority root or undertaking a full scheduler rewrite.

## Budget hierarchy

Implement a small, bounded hierarchy:

```text
SystemBudget
 -> ServiceBudget
   -> Process/DomainBudget
     -> OperationReservation
```

Child allocation must satisfy `ChildBudget <= ParentBudget` for every constrained dimension.

## Initial budget dimensions

Required dimensions:

- CPU execution allocation/window;
- owned memory bytes;
- platform-mapped/pinned memory bytes;
- active RegionUse count or bounded cost;
- IPC queued messages and bytes;
- outstanding external operations;
- outstanding device/DMA operations;
- guest memory;
- checkpoint storage;
- trace/telemetry buffers.

Provider-specific opaque cost units are allowed only if their semantics remain provider-neutral at the OS contract boundary.

## Budget identities

Use generation-bound reservation identities. A reservation is accounting state, not permission to execute an operation.

Required distinction:

```text
Capability -> may use resource
Budget reservation -> capacity admitted
Provider admission -> external effect admitted
```

All required conditions must hold independently.

## Reservation lifecycle

Reserve before consuming bounded capacity. Release only when the corresponding semantic lifetime ends.

Examples:

- mapped-memory budget releases after mapping closure and local reservation release;
- external-operation budget releases after provider resources are closed/contained and operation is released;
- IPC in-flight budget releases after message transfer/request lifecycle completes;
- checkpoint budget releases when image is deleted/retired.

A service crash does not release quota for still-live external effects.

## QoS hints

Allow advisory semantic hints such as:

```text
LatencySensitive
ThroughputOriented
Background
BoundedInteractive
```

Hints may influence scheduler/provider choice but cannot bypass hard budgets, capabilities or external admission.

Do not encode HybridCPU lane selection or CXL topology in QoS contracts.

## Pressure behavior

Define typed pressure states:

- Normal;
- SoftLimit;
- HardLimit;
- ReclaimPending;
- PinnedByExternalEffect.

Supervisor/telemetry may observe these states. Pressure itself does not authorize reclaim.

## Required tests

- child service cannot exceed parent budget;
- two reservations race without overcommit;
- stale reservation release cannot free current generation allocation;
- crash with outstanding external effect keeps corresponding quota charged;
- completed/closed operation releases quota exactly once;
- optional QoS hint does not change authority decision;
- IPC and mapped-memory budgets enforce independent dimensions;
- supervisor replacement receives fresh budget allocation.

## Exit criteria

Budgets gate admission for at least memory, IPC and external operations; hierarchical accounting is exact across crash/restart and no budget object acts as a capability.