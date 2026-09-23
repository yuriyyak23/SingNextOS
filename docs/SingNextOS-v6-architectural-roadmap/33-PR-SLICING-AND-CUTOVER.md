# v6 PR Slicing and Cutover

## P00

- freeze exact live source/package/toolchain tuple;
- add gate table entries, all OFF;
- record owner map and architecture-policy tests;
- no behavior change.

## Per-phase slice pattern

Each phase SHOULD use the same reversible sequence:

```text
A. contracts/types/canonicalization only
B. pure validators/refinement functions
C. owner-side runtime state/transition changes behind gate
D. provider/HybridCPU adapter changes
E. negative/race/fault tests
F. one executable vertical
G. performance characterization
H. claim/evidence closure
```

No PR combines a new authority mutation with an unreviewed provider callback path.

## Cutover

V2 semantic contracts coexist with V1 until the selected vertical is qualified. Default production path remains the strongest previously qualified path. New gates are enabled per contour, not globally.

## Rollback

Rollback disables the gate and returns to fresh V1/staged admission. It does not reinterpret V2 handles/proofs/leases as V1 authority and does not skip cleanup of already-submitted external effects.
