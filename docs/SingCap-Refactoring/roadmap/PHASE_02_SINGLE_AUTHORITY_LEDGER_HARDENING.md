# Phase 02 — Single Capability Ledger Hardening

## Goal

Harden the existing `CapabilityAuthority` as the **only** local capability ledger before any V2 public facade is introduced.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Current code truth

`CapabilityAuthority` already stores authoritative records and controls mint/delegate/revoke. Its IDs are currently monotonic integers and its record lacks explicit authority-realm, resource-generation, quota-account and richer constraint state.

## Required design

Refactor internally toward one record model supporting both legacy descriptor projection and future opaque handles.

### Required additions

```text
AuthorityRealmId
subject identity + subject generation
resource identity + resource generation
non-wrapping internal identity/token
record state Active/Consumed/Revoked/Retired
parent/revocation node placeholder
per-subject/global table quota accounting
stable inspection projection
```

### Authority realm

RuntimeKernel creates a new non-empty realm identity per privileged runtime incarnation. Every capability record belongs to it. Validation of wire/V2 handles checks the realm.

### Capacity and exhaustion

- no security-relevant counter wraps;
- exhausted ID/generation space fails with `CapacityExhausted`;
- per-subject live-capability quota limits attacker-controlled table growth;
- record retirement cannot silently recycle into an old valid identity.

## Compatibility

`CapabilityDescriptorV1` stays available as a legacy/inspection projection. Existing V1 public APIs must validate against the same record. Caller-supplied descriptor fields are never trusted.

## Primary paths

```text
contracts/SingPlus.Contracts/Capabilities.cs
contracts/SingPlus.Contracts/KernelContracts.cs
src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs
src/Runtime/SingPlus.Runtime/RuntimeKernel.cs
src/Runtime/SingPlus.Runtime/Observability/AuthorityInspector.cs
tests/SingPlus.Tests/Capabilities/
```

## PR slices

- **P02-1:** introduce realm/internal identity types and inspection-only projection.
- **P02-2:** subject/resource generation fields and validation plumbing.
- **P02-3:** capacity quotas, exhaustion semantics and diagnostics.
- **P02-4:** architecture test proving only one mint/revoke record store exists.

## Tests

- old V1 tests unchanged;
- wrong authority realm rejected;
- process/resource generation stale rejected;
- token collision path fails/remints;
- ID/generation wrap simulation fails closed;
- per-subject table exhaustion does not affect unrelated subject correctness;
- runtime restart fixture rejects persisted old token;
- inspection snapshot cannot be used to mint/validate without live record.

## CI gate

No new V2 API may merge until this phase passes. Static code check should reject a second dictionary/table whose purpose is independent capability mint/revoke authority.

## Exit criteria

The single ledger can represent every field needed by later constraint/revocation/sealing phases and legacy tests still pass.

## Rollback

Internal schema can be rolled back only if persisted V2 tokens have not been shipped. V1 descriptor compatibility remains intact throughout.
