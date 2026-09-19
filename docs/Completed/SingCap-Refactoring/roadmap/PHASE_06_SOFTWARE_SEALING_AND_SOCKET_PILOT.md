# Phase 06 — Software Sealing and Socket Pilot

## Goal

Implement generic software-defined sealed object handles over the single local authority ledger and prove them on one real service object.

## Normative role: identity, not rights authority

A sealed handle answers **which service-private object** and whether that object's realm/service/session/incarnation/generation is current. Possession of the handle alone never grants `Read`, `Write`, `Send`, `Close` or any other effect.

Rights and operation constraints remain in the single `CapabilityAuthority` lineage. An effect requires the exact live capability/operation lease plus sealed-object resolution and any session/Region pins through `AUTHORITY_COMPOSITION_PROTOCOL.md`. The seal table must not implement an independent arbitrary-rights model.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Why socket first

`SocketObjectHandle` already carries session/object/generation and `RuntimeNetworkServiceHost` already validates owner, exact session and exact capability. It is a strong migration pilot because sealing can replace/encapsulate the object-table identity without inventing a new resource model.

## API rule

Do not expose `TResource` implementation type publicly. Prefer:

```csharp
public readonly struct SealedHandle<TSeal>
    where TSeal : ISealedContractMarker
```

or a generator-created named handle.

## Runtime record

A sealed object record includes:

```text
opaque object token
stable SealTypeId
AuthorityRealmId
service identity/incarnation
object generation
owner/subject/session binding
revocation/state
internal service-state key/resolver
associated exact capability-lineage identity and operation-class binding (reference only; rights remain in `CapabilityAuthority`)
```

## No generic public resolver

Only trusted generated/service sentry code resolves an exact handle for an exact operation. No `Resolve<T>()`, no sibling enumeration by object ID.

Resolution does not authorize the operation. It yields an exact object pin for the current admission attempt; final commit still requires the referenced capability lineage and session binding to be live.

## Service restart

Network service restart invalidates old socket handles through service incarnation/generation even if an internal object ID counter restarts.

## Primary paths

```text
contracts/SingPlus.Contracts/NativeServiceContracts.cs
new sealed-capability contracts
src/Runtime/SingPlus.Runtime/Capabilities/ or Services/
src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs
src/Sip/SingPlus.Sip/Networking/
sdk/SingPlus.Generators/
tests/SingPlus.Tests/NativeServices/
```

## PR slices

- **P06-1:** stable seal type registry + internal records.
- **P06-2:** sealed handle validation/revocation APIs.
- **P06-3:** socket compatibility wrapper using sealed record.
- **P06-4:** generated exact sentry plumbing for selected socket operations.

## Tests

- wrong marker/type id;
- valid token under wrong service incarnation;
- valid object under wrong session/caller;
- stale object generation;
- revoked/closed object;
- valid sealed identity without the required capability/operation lease;
- live capability for a different sealed object/lineage;
- forged serialized handle;
- two sockets cannot be enumerated/substituted;
- service restart rejects old handle;
- close race with concurrent invocation has one defined result.

## Exit criteria

A real socket service path uses generic sealing while preserving or improving existing exact session/capability checks. The seal is demonstrably non-authorizing without the capability lineage, and no parallel object-authority manager is exposed to application code.
