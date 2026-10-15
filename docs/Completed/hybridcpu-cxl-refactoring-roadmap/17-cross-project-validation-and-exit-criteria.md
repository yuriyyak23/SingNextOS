# 17. Cross-Project Validation And Exit Criteria

## Purpose

Prevent either repository from declaring the Virtualization + SecureCompute + CXL contour complete based only on local unit tests or feature presence.

## Required cross-project negative matrix

The completion matrix must cover at least:

1. CXL Type-3-backed `OwnedRegion` -> exact guest mapping -> executable HybridCPU child -> clean teardown.
2. Type-3-backed guest mapping -> secure guest-region protection on the same mapping lineage.
3. CXL.io endpoint -> ordinary `DeviceLease` -> bounded virtual-I/O lease -> exact close.
4. secure CXL Type-2 execution bound to exact virtual domain + secure domain + guest mappings + device authority.
5. stale child epoch before provider effect.
6. stale secure-domain generation before provider effect.
7. stale guest mapping or parent mapping generation.
8. stale CXL fabric/backing generation.
9. stale CXL security-evidence generation.
10. Fabric Manager reconfiguration during live guest accelerator work.
11. CXL Type-3 hot-remove while a guest mapping is live.
12. security reset after `DeviceComplete` but before `Published`.
13. ambiguous provider close: no region, mapping, secure-domain or VM reclaim.
14. malformed successful secure creation whose compensation fails: tracked quarantine and teardown recovery.
15. virtual-event publication only after visibility + security/generation revalidation + publication authorization.
16. same-endpoint same-generation CXL bindings remain distinguishable through opaque binding identity where the provider ABI requires it.
17. compiler IR contains semantic secure/virtual requirements but no raw CXL topology.
18. unsupported secure operation class is denied, never generic-allowed.

## Completion gates

A feature may be advertised only at the strongest independently verified level:

- `RuntimeAdmission`: local policy/model only;
- `Executable`: exact external execution path proven;
- `ProductionSecure`: owner-bound secure enforcement + exact closure + evidence classification + negative matrix proven.

`ProductionSecure` must not be inferred from hardware-looking evidence, CXL IDE/TSP evidence, private metadata, or internal ISE descriptors alone.

## PR order

1. HybridCPU ISE policy hardening.
2. HybridCPU ExternalRuntime SecureCompute contract.
3. child/secure composition + event forwarding.
4. compiler semantic intent/lowering.
5. SingNextOS secure-virtual overlay and exact guest mapping integration.
6. SingNextOS secure virtual compute/CXL composition.
7. reconfiguration/fault orchestration.
8. cross-project tests and feature-gate promotion.

## Final exit condition

The roadmap is complete only when a secure virtualized external operation can be followed from compiler intent to HybridCPU legality, through SingNextOS authority and CXL provider effects, back through visibility/publication to guest-visible completion, while every stale/ambiguous path fails closed and prevents reclaim until closure or containment is proven.
