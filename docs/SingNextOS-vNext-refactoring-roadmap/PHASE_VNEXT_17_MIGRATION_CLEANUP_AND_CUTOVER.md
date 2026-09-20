# P17 — Migration, cleanup and final cutover

## Goal

Move from additive compatibility to a coherent vNext architecture only after all required contours are qualified.

## Migration order

1. Keep all existing accounting APIs working.
2. Add resource authority APIs behind gates.
3. Opt in one service/compute contour at a time.
4. Enable ordinary SIP resource donation before SipJob donation.
5. Qualify Host/model before HybridCPU executable path.
6. Qualify ComputeTime before bandwidth/occupancy classes.
7. Deprecate any temporary adapter DTO only after all call sites use canonical contracts.

## Cleanup targets

After qualification, remove only duplicated/transitional logic, never authoritative owners. Examples:

- duplicate resource validation in service implementations once generated sentry owns it;
- temporary test-only resource descriptors;
- compatibility shims between ComputePlan v1/v2;
- redundant provider mapping code after a versioned contract is canonical.

Do **not** merge `ResourceBudgetAuthority` and `TemporalResourceAuthority` merely to reduce file count; their semantic separation is intentional.

## Backward compatibility

Old components without resource authority requirements continue under legacy accounting/policy rules until explicitly migrated. They do not gain stronger claims.

## Final architecture acceptance

The cutover is complete only when:

```text
Effect authority
Region ownership/use
Temporal resource authority
Provider legality/admission
External effect lifecycle
Publication/release
```

are separately observable, testable and composable without authority aliasing.

## Repository documentation

Move completed phase docs under `docs/Completed/SingNextOS-vNext-Refactoring/` only after executable qualification, preserving evidence and exact source tuple.
