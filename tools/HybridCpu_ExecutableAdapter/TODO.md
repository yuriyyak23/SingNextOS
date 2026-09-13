# HybridCpu_ExecutableAdapter prerequisites

- [x] Add neutral per-feature discovery without deriving claims from the global profile.
- [x] Extract the canonical registry, lease validator, dependency state and lifecycle primitives into AuthorityCore.
- [x] Physically separate Contracts and Model and add the AuthorityCore migration seam.
- [x] Add and pin ExternalRuntime Contracts and implementation packages 1.3.0 from the repository-local feed.
- [x] Implement the bounded synchronous V3 child lifecycle with exact receipts.
- [x] Add adapter-side malformed/unknown receipt, quarantine, pin and late-result reconciliation tests.
- [x] Qualify external V3 stale/replay/wrong-binding/fault/terminal evidence in ExternalRuntime and adapter conformance tests.

Ordinary `INeutralDomainRuntime` executable lifecycle is not a Phase 5 child-domain task. The adapter intentionally does not implement that interface and makes no `DomainLifecycle = Executable` claim.
