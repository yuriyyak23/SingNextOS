# Phase 8 — Ordinary-domain Checkpoint and Restore

Status: implemented and qualified. Evidence: `08_PHASE_08_IMPLEMENTATION_EVIDENCE.md`.

## Goal

Add checkpoint/restore for ordinary logical SIP/domain state to support planned replacement, testing and recovery without claiming confidential or transparent hardware migration.

## Scope boundary

Initial supported scope:

- ordinary process/domain logical state;
- explicitly serializable application/runtime state;
- owned memory whose lifecycle can be quiesced;
- service manifest/version identity;
- selected reconstructable IPC/service metadata.

Explicitly excluded from first implementation:

- SecureDomain private/confidential state;
- live CXL/device/DMA/provider effects;
- uncontained ExternalOperations;
- raw platform/provider leases;
- raw capabilities as restorable authority;
- cross-host live migration;
- opaque host evidence.

## Resource classification

Every relevant runtime resource must report a checkpoint classification:

```text
Checkpointable
RecreateOnRestore
RequiresDrain
NonCheckpointable(reason)
```

Unknown defaults to `NonCheckpointable`.

## Checkpoint lifecycle

Proposed lifecycle:

```text
Requested
 -> Quiescing
 -> Snapshotting
 -> Validating
 -> Committed
or -> Failed/Aborted
```

Checkpoint image becomes valid only after all required state is captured and validated. Partial image is not restorable.

## Quiescence

Checkpoint coordinates with:

- supervisor to stop new work;
- IPC to settle/define in-flight message policy;
- deadlines/cancellation for bounded drain;
- ExternalOperation authority to prove no unsupported live effects;
- RegionAuthority to freeze conflicting ownership mutation during snapshot window;
- budgets to reserve image storage.

If quiescence cannot be proven, checkpoint fails explicitly rather than snapshotting uncertain state.

## Image contents

Checkpoint image includes:

- schema/version;
- service definition/manifest digest;
- original service/process generation for provenance only;
- serialized logical state;
- owned-memory content/metadata for permitted regions;
- reconstructable resource descriptions;
- compatibility requirements;
- image digest.

It must not contain reusable live capability/provider credentials.

## Restore

Restore always creates a **new** process/service generation.

Sequence:

```text
validate image
 -> validate compatibility
 -> create fresh process/domain generation
 -> restore ordinary logical state
 -> run fresh capability/budget/platform admission
 -> recreate allowed external bindings
 -> publish readiness
```

Old handles remain stale.

## Supervisor integration

Planned service replacement may optionally use:

```text
checkpoint old -> drain/close -> start new -> restore -> re-admit -> ready
```

If restore fails, rollback policy may restart without checkpoint only if manifest policy allows it.

## Required tests

- checkpoint succeeds for ordinary quiescent service;
- live noncheckpointable external effect blocks checkpoint;
- after proven drain the same service checkpoints;
- partial snapshot never becomes valid image;
- restore creates fresh generation;
- serialized old capability/handle cannot authorize effects in restored process;
- budget/capability admission may legitimately differ at restore and failure is explicit;
- incompatible manifest/schema rejected;
- checkpoint image tamper/digest mismatch rejected;
- supervisor planned replace using checkpoint preserves logical state without stale authority.

## Exit criteria

Ordinary services can checkpoint/restore with fresh authority admission, while confidential/live-provider state remains explicitly unsupported.

The implemented first slice is deliberately bounded to synchronous ordinary-service replacement and explicitly selected `OwnedBuffer<byte>` images. Live IPC, external operations, platform/device state, secure/virtual domains, and unknown owned memory fail closed. Restore always performs fresh component, capability, dependency, and budget admission at a newer process generation.
