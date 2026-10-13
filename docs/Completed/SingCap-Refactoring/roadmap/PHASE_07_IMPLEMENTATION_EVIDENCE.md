# Phase 07 Implementation Evidence

Date: 2026-09-19

## 1. Local baseline and preserved worktree

- Baseline HEAD: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- `git status --short` was captured before implementation and remained dirty with pre-existing modified, deleted, and untracked files from P00-P06 and unrelated roadmap migration work.
- No reset, checkout, clean, commit, push, force operation, or destructive filesystem operation was used. Existing work was preserved.

## 2. Audited dependencies and reused types

P07 reused the existing `RegionAuthority`, `RegionRecord`, `RegionUseDescriptor`, `RegionUseRange`, `RegionUseMode`, `RegionGeneration`, `BorrowLeaseGeneration`, `MutationEpoch`, `OwnedBuffer<T>`, `OwnedRegion<T>`, `BorrowLease<T>`, `BorrowLeaseLifetime`, external-operation Region uses, and platform mapping/backing reservations. No second Region authority or use ledger was introduced.

The live audit found that the baseline used unsynchronized `Dictionary` catalogs and counters, split validation from mutation, scanned every Region to resolve a use, and treated incompatible uses as whole-Region conflicts.

## 3. Design decisions

- Catalog identity/index safety uses `ConcurrentDictionary` plus checked, non-zero `Interlocked.Increment` identities.
- Every mutable `RegionRecord` has one short monitor gate. Validation and transition occur in the same gate. Unrelated records therefore have independent linearization domains.
- Bulk traversal snapshots records in deterministic `(RegionId, Generation)` order and acquires at most one record gate at a time. No multi-record gates are nested.
- Exact byte subranges use checked arithmetic. A typed overload checks element arithmetic, exact CLR element identity, and Region alignment before admission.
- Conflict is based on actual range overlap: read/read may overlap; any overlap containing a write mode is denied; disjoint uses may coexist. Future-gated modes still fail closed.
- `RegionGeneration` is ownership/reuse identity; `BorrowLeaseGeneration` is loan-incarnation identity; `MutationEpoch` is mutation evidence. Lifecycle transitions invalidate active uses, while ordinary write acquire/release advances epoch without fabricating a generation change or invalidating disjoint leases.
- `ReadBorrow<T>` and `WriteBorrow<T>` are lexical `ref struct` projections. Bounds are checked once when constructed; Span element loops contain no Region/capability table lookup.
- Existing `BorrowLease<T>` remains the heap-safe async lifetime. Every Span rematerialization checks the shared runtime-invalidated lifetime.
- The allocator never reuses Region identity or backing storage. Therefore confidentiality-domain pool reuse does not currently occur; a future pool must zeroize before making storage reachable in another domain.

## 4. Authority / identity / evidence / provider split

`RegionAuthority` remains the sole authority for Region identity, generation, owner, loan, Region use, mapping reservation, backing lease, mutation, release, and reclaim. `RegionUseDescriptor`, epochs, mappings, receipts, and external completion records remain evidence/projections. No provider token or completion receipt can create or widen a Region use.

## 5. Single-ledger and non-duplication proof

The added `_useIndex` maps opaque use identity to the owning existing `RegionRecord`; it contains no independent rights, range, state, owner, generation, or quota truth. All decisions re-enter the owning record gate and read the original `RegionUseRecord`. Typed and lexical APIs project the same Region lifecycle and do not create a second catalog.

## 6. Lifecycle, stale, revoke, cancellation, quarantine, reclaim

- Region transfer increments `RegionGeneration`; old handles and uses cannot revive.
- Loan acquisition increments `BorrowLeaseGeneration`; return/revoke invalidates the shared lease lifetime and active uses.
- Lifecycle mutation invalidates active uses at the record linearization point. Writable use acquire/release advances only `MutationEpoch`.
- External provider loss already invalidates staged Region uses through `ExternalOperationAuthority`; provider calls occur outside `RegionAuthority` gates.
- Mapping, external borrow grant, and backing reservations continue to block transfer/release/reclaim until verified closure.
- Released/reclaimed Region identity and backing are not recycled, so ambiguous completion cannot race a reuse path. Storage pooling and cross-domain reuse remain FutureGated and require zeroization before publication.

## 7. Public/SIP and non-leak status

The additive public surface is provider-neutral: typed `RuntimeKernel.AcquireRegionUse<T>` and lexical `ReadBorrow<T>` / `WriteBorrow<T>`. Public descriptors contain logical Region/use identity only. No physical address, provider token, HybridCPU identity, mutable authority record, or generic resolver is exposed.

## 8. Changed files/projects

- `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs`
- `src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs`
- `tests/SingPlus.Tests/Ownership/RegionUseTests.cs`
- `tests/SingPlus.Tests/Ownership/SingCapPhase07RegionHardeningTests.cs`
- this evidence file

## 9. Qualification commands and actual results

- `dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Ownership"`: 77 passed, 0 failed, 0 skipped.
- First full regression exposed an over-broad eager storage clear; it was removed because there is no reuse path and it violated established external-operation diagnostics.
- `dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --blame-hang-timeout 30s`: 1146 passed, 2 skipped, 0 failed.
- `dotnet build SingNextOS.slnx`: 0 warnings, 0 errors (NETSDK1057 preview-SDK notices are informational).
- Final `dotnet test SingNextOS.slnx`: 12 adapter + 58 neutral-runtime + 60 platform + 1146 SingPlus passed, 2 skipped, 0 failed (1276 passed total).
- `git diff --check`: exit 0; only existing LF-to-CRLF conversion notices.

## 10. Claim and limitations

Claim: `RuntimeEnforced` for synchronized Region catalog/record transitions, checked typed subrange admission, overlap conflict enforcement, distinct generation/epoch semantics, and lease invalidation. This is not a hardware-capability or physical-memory-isolation claim.

FutureGated: Region backing pool/reuse and mandatory pre-reuse zeroization (no reuse exists today); `SharedReadMostly`; `DirectCoherentWrite` until symmetric coherent release and CPU alias exclusion; P13 performance scaling/lock-wait measurements and wider deadlock stress.

## 11. HybridCPU boundary

HybridCPU core, ISE, ISA/opcodes, register file, compiler, scheduler, retire, load/store, legality, microarchitecture, and architecture were not changed. P07 modified only provider-neutral SingNextOS Region/SIP code and tests. `tools\HybridCpu_ExecutableAdapter\refctor master plan2.md` is absent; no content was inferred from it.
