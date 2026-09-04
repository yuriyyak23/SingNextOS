# 04. Memory, Ownership, DMA And Secure I/O

> Current-status overlay: Phase-7 Slice 3 adds private RuntimeKernel admission
> serialization plus bridge lifecycle-ledger checks between bounded DMA
> submission and DSC1 Copy. An active or
> ambiguously accepted use excludes the other mechanism from the same complete
> `PlatformRegionMapping` identity, even for read/read or non-overlapping byte
> subranges. Accepted lifetimes on distinct independently authorized mappings
> may overlap, although admission is coarse-serialized; under
> current `RegionAuthority`, one live mapping per region means those mappings
> also name distinct owned regions. DMA releases its use only after exact
> direction-aware post-completion visibility, and DSC1 only after terminal
> settlement plus local publication/discard and buffer-reservation release.
> Faulted or ambiguous lifetimes keep their exact use pinned where identifiable,
> or quarantine the containing platform domain otherwise. This is local
> admission policy, not range/cache-line analysis, coherence, IOMMU or
> accelerator evidence.

## Why this is the strongest SingNextOS/HybridCPU integration seam

SingNextOS уже имеет наиболее важный software primitive для безопасной ISE-интеграции: **linear ownership of regions**. HybridCPU memory/I/O/SecureCompute documentation, в свою очередь, требует owner/domain/range/grant/lifetime/policy checks и явно отвергает идею, что raw pointer, buffer ID или compatibility handle сами по себе дают authority.

Поэтому `OwnedRegion<T>` / `OwnedBuffer<T>` должны стать не просто IPC optimization, а основой всей hardware-visible memory model SingNextOS.

## Current local invariants

`RegionAuthority` уже обеспечивает:

- stable `RegionId` plus generation;
- exactly one owner in `Owned` state;
- ownership transfer with generation increment;
- revocable borrower lease with independent generation;
- stale generation rejection;
- borrower-domain cleanup;
- owner-domain reclaim;
- backing payload invalidation on reclaim.

Это правильный OS-side ownership model. Чего пока нет — external address-space/IOMMU/DMA binding.

## Target region model

Conceptually a hardware-visible region should have two layers:

```text
LocalRegionAuthority
  RegionId
  RegionGeneration
  Owner DomainId
  byte length / element shape
  Owned / Loaned / Released

PlatformMemoryBinding
  opaque external memory-domain lease
  opaque mapping/binding identity
  platform generation/epoch
  exact mapped range
  access direction / device scope
  coherence/publication policy
```

The second layer must never be serialized into ordinary SIP messages as a raw authority token.

## Zero-copy rule

Zero-copy is allowed when all of the following are true:

1. local region ownership is current;
2. receiver/device has an explicit local capability;
3. external platform domain binding is live;
4. exact range and direction are admitted;
5. transfer/borrow lifetime matches the external grant lifetime;
6. required memory ordering/coherence contract exists;
7. publication semantics are known.

Otherwise the system copies through a kernel/service-owned bounded region or fails closed.

This is deliberately stricter than «memory is mapped into both processes».

## Ownership transfer to another SIP

Software ownership transfer today increments region generation. With a platform binding, transfer must also invalidate or rebind external mappings.

Recommended sequence:

```text
validate local sender ownership
 -> stop new external use of old binding
 -> drain/cancel outstanding DMA/compute attempts for old owner
 -> external rebind/regrant to receiver domain
 -> increment local RegionGeneration
 -> invalidate old token/backing access
 -> publish new owner handle
```

If external rebind fails, local ownership transfer must not publish a half-transferred region. The implementation may need a staged transfer transaction rather than immediately changing local owner state.

## Borrow versus shared memory

Borrow is a temporary read-only capability derived from an owner. This maps well to external read-only mapping or bounded shared-buffer grant.

Preferred semantics:

- owner keeps ownership;
- borrower gets read-only access bound to borrower domain and lease generation;
- owner mutable access is blocked while hardware/read lease is active if coherence cannot guarantee safe concurrent mutation;
- return/revoke invalidates both local lease and external mapping/grant;
- late use is stale-generation failure.

Shared mutable memory should require a distinct explicit abstraction, not reuse `BorrowLease<T>`. It needs named synchronization and coherency policy and should remain exceptional.

## DMA authority

A `DmaCapability` is necessary OS permission, but not a DMA descriptor and not platform authority.

Future DMA request should include semantic inputs:

```text
DmaRequest
  source/destination OwnedRegion or bounded borrow
  exact offset/length
  direction
  device capability
  completion policy
```

Kernel/platform bridge derives external range mappings internally. SIP never supplies raw physical addresses, IOMMU IDs or host pointers.

### Required checks

- capability subject and rights;
- region owner/generation/state;
- range overflow and bounds;
- device/domain binding;
- external memory/I/O domain generations;
- direction allowed by both local and external policy;
- no private-region DMA if secure policy forbids it;
- exact completion/commit semantics.

### Current cross-mechanism mapping-use rule

For the local model, DMA grant creation and visibility preparation do not yet
mean that a device operation is active. Accepted submission starts an exact
operation use; ambiguous provider acceptance or a fail-closed submit-path
invariant fault creates an exact grant-scoped fault pin instead. Either blocks
DSC1 source and destination admission on that mapping. Exact completion is
insufficient to release an accepted use: the operation must also finish its
direction-aware post-completion visibility transition. Any malformed, faulted
or ambiguous terminal state keeps the exact use pinned where it remains
identifiable, or leaves the containing platform domain quarantined otherwise.

DSC1 stages both exact mapping uses before provider submission and rolls them
back only for an ordinary result that proves no accepted work. Completed or
cancelled closure releases them after output is published or discarded and the
local buffer leases are returned. Pending/denied observation or cancellation
does not release an already accepted use; malformed/faulted/throw paths retain
the exact use or quarantine its containing platform domain fail closed.

The current policy intentionally excludes the entire mapping across DMA and
DSC1. It does not infer that read/read or disjoint byte ranges are safe across
engines. Conversely, distinct mapping identities are not sufficient authority
on their own: the usual process, capability, ownership, binding, generation and
access checks still apply independently.

A prepared-but-unsubmitted DMA cycle remains outside this active-use interlock.
The current managed buffer model also has no CPU-alias mutation epoch, and a
`Span<T>` acquired before a runtime reservation cannot be revoked. Consequently
an old prepare result is not executable proof after an intervening CPU or DSC1
write. A future hardware path must bind prepare evidence to the current mutation
epoch or require a fresh prepare before submission.

## Lane6 DSC as ownership-native compute

HybridCPU's code-confirmed DSC1 contour is unusually well matched to SingNextOS linear memory:

- Copy
- Add
- Mul
- Fma
- Reduce
- exact inline ranges
- `AllOrNone` completion
- explicit owner/domain/placement/token
- staged compute before commit
- destination rollback on partial physical write failure

This makes DSC1 a high-value candidate for a future `System.Compute.Bulk` service where input/output regions are passed by borrow/ownership and the caller never sees lane6.

Example conceptual contract:

```csharp
ValueTask<OwnedRegion<T>> TransformAsync(
    [Consumes] OwnedRegion<T> destination,
    BorrowedRegion<T> source,
    BulkComputeOperation operation,
    ComputeCapability capability);
```

The exact API must wait for response ownership transport and external bridge, but the authority model is already clear.

Important current limit: HybridCPU DSC1 is synchronous/all-or-none in the confirmed contour. DSC2 queues, async overlap and coherent DMA/cache are future/denied. SingNextOS must not advertise async hardware overlap merely because its public API is `ValueTask`-based; `ValueTask` can represent logical asynchronous scheduling without claiming ISE hardware overlap.

## MatrixTile memory

MatrixTile load/store shares physical lane6 but is a different semantic authority. SingNextOS must not route MatrixTile through the DSC abstraction just because both consume memory bandwidth.

A future Matrix service should manage tile state as an opaque compute resource and use owned regions for ingress/egress. HybridCPU runtime owns tile state, layout/numeric policy, capture and retire publication. The OS only owns who may request a matrix operation and which regions may be used.

## L7 accelerator buffers

External accelerator commands may reference owned regions through a kernel-owned device binding. Lane7 token or accelerator descriptor must never escape as the region authority.

Correct composition:

```text
DeviceCapability
+ OwnedRegion generation
+ external accelerator/domain binding
+ operation-specific command grant
= staged accelerator operation
```

Completion/telemetry token alone does not permit reusing, remapping or transferring the region.

## Secure/private/shared/measured memory

HybridCPU SecureCompute policy defines useful categories for a future SingNextOS confidential-domain layer:

- **private** — host inspection denied; private DMA denied unless a separately defined secure path exists;
- **shared** — explicitly declared inter-domain/device sharing;
- **measured** — included in measurement/evidence policy;
- **runtime-mutable** — requires dirty/migration classification.

These should map to **region policy metadata**, not new pointer types carrying authority.

Possible future local descriptor:

```text
RegionProtectionClass
  Ordinary
  SharedExplicit
  Private
  Measured
  RuntimeMutableMeasured
```

But it must only become enforceable when the external platform provides a real positive secure-memory/domain owner. Before that, `Private`/`Measured secure execution` requests should be rejected as unsupported rather than simulated with ordinary memory.

## Secure I/O

HybridCPU Secure I/O makes an important rule explicit: buffer ID alone is not authority. A shared buffer requires owner domain, policy epoch, current lifetime, evidence class and grant.

SingNextOS already has most local ingredients:

- owner domain;
- region generation;
- borrow generation/lifetime;
- capability rights;
- deterministic protocol metadata.

The missing external ingredients are:

- platform memory-domain binding;
- platform I/O-domain binding;
- IOMMU mapping/revocation;
- secure shared-buffer grant if SecureCompute is active.

Until these exist, MMIO/IRQ/DMA remain external-blocked exactly as current `EXT-HCPU-002` states.

## MemoryProfile mapping

Current `MemoryProfile` should remain OS/runtime semantics, but it can guide external binding policy.

### KernelNoHeap

- smallest trusted kernel contour;
- no managed allocation on admitted root;
- platform root/control memory should be minimal and non-delegable by default;
- avoid binding general device/compute buffers directly to kernel memory.

### SipRegion

- primary systems/service profile;
- ownership-backed regions;
- best target for domain-backed zero-copy, DMA and compute bindings;
- deterministic cleanup on domain termination.

### ManagedGc

- application compatibility/productivity profile;
- GC objects are not hardware ownership units;
- direct hardware operations cross through pinned/copied/owned external regions with explicit lifetime;
- a managed reference is never passed as external platform authority.

## Migration and checkpoint

The old WhiteBook treated snapshots as a general hypervisor function. Current HybridCPU SecureCompute explicitly does not have a complete production secure migration protocol and notes fail-open classifier concerns in current policy.

SingNextOS rule:

- ordinary SIP logical state may have future OS checkpointing;
- external hardware/domain bindings are classified separately;
- raw platform tokens, active pointers, host evidence, device tokens and compatibility metadata never serialize as authority;
- secure/private regions require external sealed/encrypted restore contract and anti-replay proof;
- unknown payload classes default deny.

## Memory architecture decision

Do not design a universal shared virtual memory layer first. Build from **owned regions + explicit external domain binding + staged hardware publication**. This directly matches both SingNextOS linear ownership and HybridCPU neutral memory/I/O authority while remaining implementable with a small trusted surface.
