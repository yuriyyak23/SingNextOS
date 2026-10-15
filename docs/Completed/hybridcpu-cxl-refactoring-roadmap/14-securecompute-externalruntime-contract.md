# 14. SecureCompute ExternalRuntime Contract

## Goal

Add the missing production-positive external SecureCompute contour to HybridCPU-v2 without conflating evidence, virtualization or CXL transport with authority.

## Required HybridCPU-v2 changes

1. Extend `HybridCPU_ExternalRuntime.Contracts` with a versioned SecureCompute family. The external feature model must be able to distinguish `ProductionSecure` from ordinary `Executable` or `RuntimeAdmission` support.
2. Introduce owner-bound opaque handles and generations for secure domain lifecycle, secure region binding, secure execution binding when composed with a child domain, evidence context, and terminal secure-domain/secure-region closure.
3. Every secure-region request must name the exact parent mapping identity/epoch already admitted by the external runtime. No second memory authority is created.
4. Secure-domain creation must return proven properties. Missing or malformed proof must fail closed and be compensatingly closed or quarantined.
5. External close receipts must prove exact identity + generation + terminal closure. Unavailability alone is not closure.
6. Expose no CXL-specific topology, endpoint IDs, HDM decoder identities, switch routes, DPA or Fabric Manager identities in this ABI.

## ISE policy requirements

`SecureDomainOperationClass` must be exhaustively dispatched. `CreateEvidence`, `PublishCompletion`, `PublishRetireSideEffect`, `SecureMigration`, `NestedSecureDomain`, and `CompatibilityProjection` need explicit policy rather than generic allow. Undefined or unsupported secure operation classes deny by default. Existing `DeniedMissingEvidencePolicy` and `DeniedMissingMigrationPolicy` outcomes should become reachable through real policy checks.

## Cross-project gate

SingNextOS must continue to report `SecureDomains = Unavailable` for the HybridCPU provider until the new ABI exists, is implemented by the adapter, and passes the cross-project negative matrix. Do not satisfy the current external gate by reinterpreting internal ISE descriptors as external secure authority.

## Exit criteria

- versioned SecureCompute external interfaces compile independently of SingNextOS;
- exact generation mismatch is rejected for every destructive action;
- malformed successful creation with failed compensation remains recoverable/quarantined;
- evidence never creates execution/memory/I/O permission;
- no `ProductionSecure` claim is emitted until full conformance tests pass.
