# Phase 10 — Manifest V2, Deterministic Audit and Supply-Chain Binding

## Goal

Extend the existing `ServiceManifestV1` and admission proof into a complete static intent/audit contract without confusing evidence with live authority.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Manifest evolution

Create V2 only where schema versioning requires it. Reuse V1 semantics for identity/version/image/contracts/dependencies/platform/resource/budget/lifecycle policies.

Add:

```text
SecurityProfile
DependencyContentDigests / closure policy
StaticCapabilityImports
SealedTypeImports/Exports
MemoryPolicy
AuthorityTableQuotaPolicy
RuntimePolicy
DelegationPolicy
AotPolicy
AdmissionPolicyVersion
ManagedCapFrameworkSurfaceVersion/Digest
```

## Startup protocol

```text
verify image digest
 -> verify dependency/native closure
 -> verify security profile
 -> verify generated SIP contract graph
 -> verify requested static authority against system policy
 -> bind required providers/services
 -> mint exact local capabilities into single ledger
 -> publish runnable state
```

Failure unwinds already-created runtime resources in reverse order.

## singcap-audit-v1.json

Emit deterministic canonical JSON. Include all fields from Technical Specification MAN-003.

The file is evidence only. Runtime never treats possession of the JSON file as authority.

For ManagedCap, the audit binds both the AdmissionVerifier policy digest and the exact positive framework/member-surface version/digest from P09. SDK/runtime drift or allowlist expansion invalidates prior qualification even if component bytes are unchanged.

## Supply-chain rule

For every external provider contract artifact, especially HybridCPU:

```text
package id + version
SHA-256 of exact package
source commit used to build/qualify it
contract schema version
```

must be recorded.

This phase explicitly addresses the current situation in which HybridCPU master `794c4a53494f503855ac8cf209efab23fde083b2` still declares package `HybridCPU.ExternalRuntime.Contracts` version `1.14.0` while the source has changed relative to the earlier audited commit. A rebuilt package with a different digest under the same SemVer is treated as a different artifact requiring review.

## Primary paths

```text
contracts/SingPlus.Contracts/ComponentManifests.cs
contracts/SingPlus.Contracts/ServiceManifestCanonicalization.cs
tools/SingPlus.Admission/
src/Runtime/SingPlus.Runtime/Components/ComponentAdmission.cs
packages.lock.json / local package evidence
CI
```

## PR slices

- **P10-1:** manifest extension/schema canonicalization.
- **P10-2:** deterministic SingCap audit contracts/emitter.
- **P10-3:** runtime startup consistency between approved static imports and minted authority.
- **P10-4:** package/source/provenance evidence and CI authority-diff gate.

## Tests

- byte-stable audit for same inputs;
- changed dependency digest changes audit;
- undeclared static authority cannot be minted at startup;
- audit cannot be replayed as authority;
- malformed/unknown manifest policy denied;
- same HybridCPU package version with different digest triggers qualification failure;
- policy/verifier/SDK version drift invalidates stale qualification evidence.
- positive framework-surface version/digest drift invalidates stale qualification evidence.

## Exit criteria

Static intended authority is reviewable/diffable and deterministically bound to the exact code/dependencies/toolchain, while runtime authority still comes only from RuntimeKernel.
