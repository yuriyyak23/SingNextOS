# Phase 00 — Baseline, Threat Model and Architectural Freeze

## Goal

Freeze the security semantics that later code must implement. This phase contains no capability V2 semantic implementation.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Why this phase is mandatory

The previous roadmap assumed a future .NET 11 migration and did not fully freeze revocation linearization, quota lineage, restart replay, ID exhaustion or exact HybridCPU package/source identity. Implementing public V2 types before these decisions would freeze the wrong ABI.

## Required outputs

Create authoritative ADRs/doc sections for:

1. **Single authority ledger** — V1 and V2 are projections of one record store.
2. **AuthorityRealmId** — runtime incarnation/restart semantics.
3. **Service incarnation** — stale sealed handle behavior after service crash/restart.
4. **Identity strategy** — random token or slot+nonce/generation; no wrap.
5. **Delegation depth** — exact maximum and policy owner.
6. **Quota lineage** — shared account vs reservation transfer.
7. **Revocation/effect linearization** — define operation authority lease semantics.
8. **Serialization** — ephemeral token wire format; durable credentials explicitly excluded.
9. **Region epoch roles** — RegionGeneration vs BorrowGeneration vs MutationEpoch.
10. **NativeIsolated** — exact independent isolation primitive.
11. **ManagedCap NativeAOT policy** — required claim level and exceptions.
12. **HybridCPU evidence pin** — `794c4a53494f503855ac8cf209efab23fde083b2`, package version `1.14.0`, package SHA-256 from the actual qualified artifact, operation schema version(s).

## Repository inventory

Produce an `EXISTS/PARTIAL/MISSING` table for at least:

```text
CapabilityAuthority
CapabilityDescriptorV1
RegionAuthority
OwnedBuffer<T>
OwnedRegion<T>
BorrowLease<T>
RegionUseHandle
RegionBackingLeaseHandle
EndpointSessionRegistry
RuntimeKernel external operation lifecycle
HybridCpuExternalOperationProvider
ServiceManifestV1
SingPlusGenerator
SingPlusAnalyzer
AdmissionVerifier
FileObjectHandle / SocketObjectHandle / ProcessAuthority
```

Any proposed file/class from older plans that duplicates one of these must be removed or relabeled as an extension.

## HybridCPU delta review

Record that current HybridCPU master is `794c4a53494f503855ac8cf209efab23fde083b2` and contains provider-neutral external admission/publication/cancellation/generation semantics. Explicitly state:

```text
CPU guard evidence != SingNext capability
provider admission receipt != SingNext capability
provider generation != SingNext resource generation
HybridCPU domain tag != CHERI tag
completion != publication
```

## PR decomposition

- **P00-1:** baseline/evidence inventory only.
- **P00-2:** ADRs for authority realm, quota, revocation linearization and Region epochs.
- **P00-3:** profile/TCB ADR and HybridCPU package/source qualification policy.

## Tests / CI

No new security claim. Add architecture tests where practical to fail if a second authority implementation or direct HybridCPU project reference is introduced.

## Exit criteria

- all decisions above have one selected answer, not an open placeholder;
- exact source SHAs recorded;
- old `.NET 10 -> .NET 11` roadmap wording removed;
- CapRef/SharedArena explicitly deferred;
- implementation phases can refer to stable requirement IDs.

## Rollback

Documentation-only. No runtime rollback needed.
