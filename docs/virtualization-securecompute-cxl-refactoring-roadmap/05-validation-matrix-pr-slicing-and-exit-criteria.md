# 05. Validation Matrix, PR Slicing And Exit Criteria

## Required end-to-end matrix

The SingNextOS roadmap is not complete without tests covering Type-3 `OwnedRegion` -> guest mapping -> executable HybridCPU child; the same mapping lineage -> secure guest-region protection; CXL.io -> DeviceLease -> VirtualIo; secure+virtual CXL Type-2 execution with exact mappings/domain bindings; stale virtual/secure/guest/fabric/backing/security generations; FM reconfiguration and hot-remove at each external-operation stage; ambiguous provider submit/close/release; guest event blocked until Published; process teardown with every resource live; and no `ProductionSecure` promotion from evidence alone.

## Suggested PR slices

1. `SecureExecutionBinding` + lifecycle/quarantine tests.
2. `BindSecureGuestRegion` using the exact hidden parent mapping.
3. VirtualIo/event forwarding wiring and publication-order tests.
4. `VirtualComputeContext` + exact Type-2 virtualized admission.
5. secure+virtual Type-2 compound snapshot/revalidation.
6. CXL reconfiguration/hot-remove orchestration into VM/secure state.
7. teardown/reclaim integration and fault injection.
8. cross-project conformance tests + feature-gate promotion.

Each slice remains fail-closed when the corresponding HybridCPU external capability is absent.

## Feature promotion rules

- `VirtualizationDomains = Executable` only with the explicit executable child adapter and qualified artifact/VirtualIo contracts.
- `SecureDomains = ProductionSecure` only with the new HybridCPU SecureCompute external ABI and independent negative tests.
- secure virtualized compute requires both claims plus exact local authority; neither feature implies the other.
- CXL security evidence may satisfy a predicate but never promotes a platform feature by itself.

## FutureGated after this roadmap

Keep DirectCoherentWrite/zero-copy ABI guarantees, secure writable multi-host CXL memory, secure CXL P2P, transparent confidential migration, nested confidential domains, and unproven guest hardware-attestation claims outside completion.

## Final exit criterion

For one secure virtualized CXL-backed workload, tests prove:

```text
local capability -> VirtualDomain -> SecureExecutionBinding
 -> exact guest mapping / VirtualIo -> CXL generation/security checks
 -> provider effect -> completion -> visibility -> revalidation
 -> publication -> guest event -> exact provider closure
 -> authority release/reclaim
```

Every stale, malformed, unavailable or ambiguous branch has a deterministic fail-closed result and never silently reclaims a still-referenceable resource.
