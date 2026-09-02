# Phase 9 — Evidence and SecureCompute gating

## Status

**Feature-gated / external-blocked for production security.** Depends on Phase 1 feature taxonomy, Phase 3 domain binding and Phase 8 only if secure virtual domains are in scope. Corresponds to the evidence/SecureCompute portion of `EXT-HCPU-006`.

## Goal

Expose useful platform evidence without turning evidence into authority, and make confidential/secure-domain APIs fail closed unless HybridCPU provides a production-positive backend.

## Current audit conclusion

HybridCPU-v2 contains meaningful SecureCompute descriptor/policy architecture and evidence visibility concepts, but the audited state does **not** prove a production secure execution backend, hardware-rooted attestation, confidential-VM substrate or universal tagged/capability memory enforcement.

Therefore SingNextOS may define target API shapes now, but runtime availability must remain truthful.

## Evidence contract

Introduce a read-only provider family such as:

```text
IPlatformEvidenceProvider

QueryEvidenceCatalog(domain/profile)
ReadEvidence(domainLease, evidenceKind, visibilityClass)
```

Evidence records should carry:

- evidence kind/version;
- subject/domain generation;
- producer/provider identity;
- visibility/classification;
- measurement/result payload;
- freshness/epoch metadata where meaningful.

They must **not** be accepted by any API that expects a capability, provider grant or ownership token.

## Evidence visibility

Default-deny host/internal topology. Suggested classes:

```text
PublicDiagnostic
DomainDiagnostic
PrivilegedDiagnostic
SecurityMeasurement
HostInternal   // never exposed to ordinary SIP clients
```

A local `EvidenceRead` capability controls which classes a process/service may request. The provider can further restrict what exists or is externally visible.

## SecureCompute discovery

Use Phase 1 readiness classes. A secure profile request must distinguish:

```text
Unavailable
ModelOnly
RuntimeAdmission
ExecutableButNotProductionSecure
ProductionSecure
```

Do not silently map a secure request to an ordinary domain.

Example rule:

```text
request ConfidentialDomain
AND provider level != ProductionSecure
  -> fail closed / Unsupported
```

## Secure domain API shape

Keep the high-level API neutral:

```text
CreateSecureDomain(parent, SecureDomainProfile)
BindPrivateRegion(...)
BindSharedRegion(...)
ReadAllowedEvidence(...)
Start/Park/Resume/Destroy(...)
```

The profile may describe requirements such as private memory, restricted inspection, sealed runtime, secure I/O or measured execution. Every requested property must be independently admitted; unsupported properties fail closed.

Do not expose vendor/VMX-specific confidential-compute control blocks in the Sing kernel ABI.

## Authority rules

Secure-domain creation requires both:

```text
local Sing secure-domain creation capability
AND live provider ProductionSecure authority
```

Evidence never substitutes for either half. A measurement that says “secure” is observation, not a grant to map memory or start a domain.

## Restore/migration/stale handling

If migration or restore is not production-positive, report unsupported.

If supported later, old generations must be invalidated across:

- Sing process/domain generation;
- secure-domain local generation;
- provider domain epoch;
- memory/device grants;
- evidence freshness context.

Do not let a stale evidence blob revive authority after restore.

## Tests

- `ModelOnly` SecureCompute cannot satisfy secure-domain creation;
- evidence object cannot be passed where a capability/grant is required;
- missing `EvidenceRead` capability denies request before provider call;
- host-internal evidence never crosses ordinary SIP boundary;
- secure profile requiring unsupported private memory fails closed;
- provider downgrade from `ProductionSecure` invalidates future admission but does not fabricate completion for active domains;
- stale evidence generation is rejected/marked stale;
- host test provider is never labeled hardware attestation.

## Acceptance criteria

Phase 9 is complete when SingNextOS can truthfully query evidence/readiness, expose only authorized evidence projections, and reject secure-domain creation unless the provider proves every required security property at the declared production level.

## Do not do

- no “secure by descriptor existence” claim;
- no evidence-as-capability;
- no fallback from confidential to ordinary execution;
- no hardware-rooted attestation claim without hardware-rooted evidence;
- no secure migration/checkpoint claims unless explicitly supported.
