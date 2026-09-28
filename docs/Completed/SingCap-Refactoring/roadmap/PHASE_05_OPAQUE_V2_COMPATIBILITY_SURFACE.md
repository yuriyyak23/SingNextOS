# Phase 05 — Opaque V2 Compatibility Surface

## Goal

Expose a safer opaque public capability representation **after** the single-ledger semantics are stable, without creating new authority state.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Public shape

Introduce versioned types such as:

```text
AuthorityRealmId
CapabilityHandleV2
CapabilityInspectionDescriptorV2
```

Exact binary layout is an ADR decision. The handle contains only opaque identity/realm/version information necessary to resolve the internal record. Rights/resource constraints remain authoritative only inside the ledger.

## Copy semantics

`CapabilityHandleV2` may be a readonly value type and freely copied. Copying never clones quota, one-shot or ownership authority because those live in the record.

## V1 compatibility

During migration:

```text
V1 descriptor API -> resolve same internal record -> produce V1 projection
V2 handle API     -> resolve same internal record -> produce internal validation result
```

No adapter may mint a new V2 authority merely because it saw a valid-looking V1 descriptor from an untrusted caller.

## Serialization

Wire representation binds authority realm and token. It is explicitly ephemeral. Replaying after runtime restart fails realm validation.

## Primary paths

```text
contracts/SingPlus.Contracts/Capabilities.cs
new versioned capability contract file if useful
src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs
src/Runtime/SingPlus.Runtime/RuntimeKernel.cs
sdk/SingPlus.Generators/
tests/SingPlus.Tests/Capabilities/
```

## PR slices

- **P05-1:** contracts + canonical serialization/equality tests.
- **P05-2:** RuntimeKernel overloads over the same ledger.
- **P05-3:** compatibility projections and deprecation annotations.
- **P05-4:** first generated/SIP fixture using V2.

## Tests

- random/forged token rejected;
- V2 handle copied repeatedly does not duplicate one-shot/quota authority;
- tampered realm rejected;
- V1 and V2 resolve to same record state;
- revoking through one surface invalidates validation through the other;
- descriptor field tampering cannot widen authority.

## Exit criteria

V2 exists as a safer reference format, not a second subsystem. Existing V1 flows still work until explicitly migrated.
