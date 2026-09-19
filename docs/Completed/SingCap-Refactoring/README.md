# SingCap-M Specification and Refactoring Package

Generated from a repository-level architecture/security audit and re-baselined on 2026-09-19.

## Pinned repositories

- SingNextOS master: `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 master: `794c4a53494f503855ac8cf209efab23fde083b2`

## Contents

- `SINGCAP_M_TECHNICAL_SPEC.md` — normative technical specification.
- `roadmap/README.md` — phase dependency graph and index.
- `roadmap/PHASE_00...PHASE_13...md` — independently reviewable implementation phases.
- `roadmap/PHASE_90_DEFERRED_CAPREF_AND_SHARED_ARENA.md` — explicit non-v1 scope.
- `roadmap/TRACEABILITY_AND_CI_GATES.md` — requirement-to-phase/test matrix and PR security checklist.

## Architectural thesis

The package does **not** replace SingNextOS with a new capability subsystem. It strengthens the already existing unified mechanisms:

```text
CapabilityAuthority
RegionAuthority
OwnedBuffer / BorrowLease
EndpointSession / SIP
external-operation lifecycle
AdmissionVerifier
ServiceManifestV1
PlatformAuthorityBridge
```

Key corrections incorporated from the audit:

1. one capability ledger only — no independent `CapabilityAuthorityV2`;
2. Region sub-capabilities stay inside `RegionAuthority`;
3. explicit authority-realm/service-incarnation replay protection;
4. non-wrapping identity/generation and table-exhaustion semantics;
5. non-amplifying atomic quota lineage;
6. revoke/effect linearization via exact operation authority admission;
7. software sealing without public `Resolve<TResource>`;
8. deep SIP value schema rather than assuming C# records are immutable;
9. ManagedCap extends the existing AdmissionVerifier and uses full-module verification;
10. ServiceManifestV2 is additive over V1;
11. NativeIsolated requires a real independent isolation boundary;
12. exact HybridCPU package version + digest + source commit qualification;
13. CapRef/SharedArena deferred from v1;
14. HybridCPU ISE remains unchanged.

## Important HybridCPU update

Compared with the previously audited baseline, current HybridCPU master `794c4a53494f503855ac8cf209efab23fde083b2` adds provider-neutral external operation admission binding, publication evidence, cancellation/generation handling and SecureCompute admission policy. The roadmap uses these only as provider-side evidence/enforcement and preserves `provider authority != SingNext authority`.
