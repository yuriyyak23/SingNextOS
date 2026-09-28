# 01. Secure Guest Memory And CXL Type-3 Composition

## Problem

`MapGuestRegion()` already creates an exact parent `PlatformRegionMapping` internally and then an exact guest mapping. `BindSecureRegion()` accepts a `PlatformRegionMapping`, so callers cannot prove that security protects the same mapping lineage without exposing or duplicating hidden platform state.

## Decision

Add a kernel-mediated operation equivalent to:

```text
BindSecureGuestRegion(SecureExecutionBinding, GuestRegionMappingHandle, PlatformSecureRegionClass)
```

The kernel resolves the exact existing guest mapping and its hidden parent platform mapping, then asks the secure provider to protect that exact mapping. No second `PlatformRegionMapping` is created.

## CXL Type-3 rule

```text
CXL.mem backing -> RegionBackingLease -> OwnedRegion
 -> existing parent platform mapping -> GuestRegionMapping
 -> SecureGuestRegionBinding
```

Do not add `CxlGuestMemory`, `CxlVirtualRegion`, or CXL-specific guest capabilities.

## Lifetime and reclaim

- the Type-3 backing lease is independent of active guest/secure access;
- guest mapping closure closes secure protection first;
- secure mapping closure does not release Type-3 backing by itself;
- Type-3 backing release waits for guest mappings, secure overlays and external operations to close;
- stale backing/fabric/security generations before a new effect fail closed;
- ambiguity after an effect quarantines the composed mapping and blocks region reclaim.

## Required tests

- Type-3-backed regions remain valid guest memory;
- exact secure overlay uses the same parent mapping, not a duplicate;
- stale guest mapping cannot bind secure protection;
- stale Type-3 backing/fabric generation blocks secure provider effect;
- teardown closes protection -> guest mapping -> CXL backing before reclaim;
- failed secure unbind leaves the region pinned/quarantined.
