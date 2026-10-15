# 15. Secure Virtualization Composition And Event Publication

## Goal

Make HybridCPU-v2 capable of composing exact child-domain authority with exact SecureCompute authority without inventing a monolithic secure-VM authority root.

## Composition contract

The target external relation is conceptually:

```text
ChildDomainLease + SecureDomainLease + ParentDomainLease
        -> SecureExecutionBinding
```

The binding carries exact child epoch, secure-domain generation, parent-domain epoch and policy generation. It is a composition receipt, not a new root capability.

## Guest memory protection

A secure guest region binds security protection to the same external parent mapping already used by the guest mapping:

```text
SecureDomain + ChildDomain + GuestMapping + ParentMapping
        -> SecureGuestRegionBinding
```

No duplicate parent mapping, shadow region owner or CXL-specific guest memory object is allowed.

## Virtual I/O and events

Secure virtual I/O requires both the child-domain bounded VirtualIo lease and secure-I/O admission for the exact secure execution binding. Neither substitutes for the other.

The executable child adapter must forward virtual events through the existing ExternalRuntime child event contract. A physical CXL interrupt/completion cannot be surfaced directly to the guest.

Required order:

```text
provider completion -> visibility -> security/generation revalidation
 -> architectural publication -> virtual-event injection -> guest event
```

## Fault handling

If secure or child generation becomes stale after a provider effect but before guest-visible publication, the effect must be contained/closed or the composed binding quarantined. Guest event injection is forbidden in that state.

## Exit criteria

- exact child + secure composition has a versioned contract;
- secure guest mapping reuses the exact existing mapping lineage;
- virtual event forwarding is implemented through the executable adapter;
- stale child, secure, mapping or parent epochs fail closed;
- tests prove no direct physical completion-to-guest shortcut exists.
