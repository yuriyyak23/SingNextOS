# Phase 3 — Ownership exchange model

## Goal

Preserve Singularity's ownership-oriented communication principle without recreating a global shared/exchange heap.

The modern primitive remains SingNextOS-owned regions/buffers with generations and explicit grants.

## Core model

```text
RegionHandle
+ Owner identity
+ Ownership generation
+ Access mode
+ Optional borrow/grant records
+ Optional platform mapping/grant state
```

`OwnedRegion<T>` / `OwnedBuffer<T>` remain the source of logical ownership truth. HybridCPU/provider mappings materialize effects but do not become a second ownership system.

## MOVE

A mutable large-buffer MOVE must be interpreted as logical authority transfer:

```text
validate current owner/generation
-> stop new old-owner use
-> drain/revoke incompatible external grants
-> acquire visibility if required
-> change owner
-> increment generation
-> optionally rebind for the new consumer
```

Physical remap/zero-copy is optional. A copy fallback is valid if semantics are preserved.

## Borrow and device grant are different

Do not overload one abstraction for all sharing:

- local read-only borrow: temporary local protocol authority;
- local mutable loan: explicit exclusive temporary authority if supported;
- device/accelerator/display grant: exact range/direction plus platform lifetime;
- shared immutable snapshot: separate semantics if needed.

## Visibility

No transition from CPU ownership to device/accelerator/display use may assume universal coherence.

Each platform-visible handoff must model:

- prepare/publish before consumer access when required;
- completion/fence;
- acquire/maintenance before CPU reuse when required.

## Claim taxonomy

Documentation and APIs MUST distinguish:

- logical zero-copy: ownership transferred without changing logical backing object;
- same-backing mapping: provider maps the same storage;
- physical zero-copy: no physical data copy occurred;
- direct device access: hardware consumed the mapped backing directly.

Only claim the strongest property that is actually proven.

## Tests

- stale owner generation rejected;
- old owner cannot mutate after MOVE commit;
- device-write buffer cannot return to CPU-mutable state before completion/acquire;
- provider revoke failure keeps backing pinned and unreclaimed;
- copy fallback preserves ownership semantics;
- local borrow cannot be reused as a device grant;
- region identity and provider mapping identity never alias.

## Acceptance criteria

One large-buffer service path must use ownership transfer/borrow semantics without ambient shared mutable state and remain correct under stale generation, cancellation and provider-drain fault injection.
