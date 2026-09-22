# Risks, Non-Goals and Fail-Closed Policy

## Primary architectural/security risks

- BootInfo accidentally adopted as capability;
- physical mapping treated as memory ownership;
- raw BDF/DSN/HPA reused as logical authority;
- provider receipt treated as SingNext capability;
- old provider generation surviving reset;
- successful copy interpreted as publication;
- ambiguous HDM teardown treated as release;
- rollback floor sourced from writable CXL media;
- model code promoted as production by namespace move;
- unbounded PCI/CXL parsing/retries;
- NativeAOT described as isolation;
- stale HybridCPU pin “fixed” without requalification;
- claim promotion without matching evidence.

## Mandatory fail-closed outcomes

Use `Stale`, `Quarantined`, `ReclaimBlocked`, bounded recovery or fail-stop when:

- device identity/liveness is ambiguous;
- generation/epoch cannot be revalidated;
- mapping compensation cannot be proven complete;
- reset occurs mid-operation and cleanup is uncertain;
- protected state is split-brain/torn beyond deterministic selection;
- rollback floor cannot be authenticated;
- provider refuses fresh admission;
- external feature gate is absent;
- BootInfo is malformed/out of bounds;
- aperture retirement state is ambiguous.

Never “best effort” publish runtime authority.

## Non-goals

No redesign of `RegionAuthority`, provider authority or runtime CXL architecture unless `P15-00` demonstrates a narrow missing hook. Prefer local extension points.

Do not optimize boot latency by weakening authority generation or fresh-admission semantics.
