# Phase 04 — Derivation, Revocation and Effect-Admission Linearization

## Goal

Make delegation and revocation correct under concurrency and prevent the classic `Validate -> Revoke -> Effect` race.

P04 is also the first implementation gate for `AUTHORITY_COMPOSITION_PROTOCOL.md`. A capability-ledger lease alone is insufficient when the effect depends on process, session, sealed-object, Region or external-operation state.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Parent-child derivation

- child linkage and parent validity are established atomically;
- parent link is immutable;
- bounded depth is checked at derive time;
- cycles are impossible;
- child stores/can reach the same revocation lineage node/account.

## Subtree revocation

Current direct V1 revoke is insufficient for descendants. Implement one selected model:

```text
bounded ancestry walk
or
shared revocation node/epoch tree
```

The model must have deterministic complexity bounds and cache invalidation semantics.

## Operation authority lease

Introduce a privileged internal primitive conceptually equivalent to:

```text
AcquireOperationAuthority(
    subject,
    capability,
    exact resource/generation,
    exact operation,
    session?,
    consume/quota request?)
        -> OperationAuthorityLease
```

The acquisition is the linearization point for starting a new effect. A plain descriptor returned by `Validate` is not enough.

The lease is not a durable authority token for application code. It is trusted runtime/sentry state for one exact effect attempt.

## Cross-authority admission

For the pilot effect, inventory every authoritative participant and implement one bounded prepare/pin/commit attempt. The final commit must reject mixed-time observations: if a subject/session/resource/Region generation changes after prepare, all prepared local leases are compensated in reverse order and no provider call occurs.

An `EffectAdmissionSnapshot` is correlation evidence only. It may name exact leases/pins and generations, but it cannot copy Region/session/sealed authority into a new coordinator. No external provider call, callback, blocking wait or user code may execute while a capability/session/Region/seal/external-operation registry lock is held.

## Revocation semantics for in-flight work

Define three distinct outcomes:

```text
new effect admission     blocked after revoke
already-admitted effect  follows operation/cancellation policy
publication/release      revalidated according to the operation's closed static policy
```

Revocation cannot claim to undo an irreversible provider action already submitted.

Use one closed trusted-runtime policy per operation class:

| `EffectRevocationPolicy` | After admission | After submission | Before publication/release |
|---|---|---|---|
| `AdmissionOnly` | admitted attempt continues | no fabricated cancellation | normal lifecycle gates |
| `CancelIfPossible` | request exact cancellation | ambiguity remains pinned/quarantined | closure still required |
| `PublicationRevocable` | work may continue | completion is evidence only | publication is denied after revoke where staging permits |
| `GrandfatherAdmitted` | exact admitted attempt continues | irreversible outcome remains possible | no fake rollback; normal closure/reclaim rules |

The classification is static generated/runtime policy and is never supplied by an application, manifest field or provider receipt. Each migrated operation documents its exact row and transition table.

## One-shot consume

One-shot state and quota consumption occur in the same authoritative transaction/lock/linearization domain as effect admission.

## Primary paths

```text
src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs
src/Runtime/SingPlus.Runtime/RuntimeKernel.cs
src/Runtime/SingPlus.Runtime/ExternalOperations/*
src/Runtime/SingPlus.Runtime/Services/*
tests/SingPlus.Tests/Capabilities/*
tests/SingPlus.Tests/Runtime/*
```

## PR slices

- **P04-1:** derivation graph/node + bounded depth.
- **P04-2:** subtree revocation and cache invalidation.
- **P04-3:** derive-vs-revoke stress semantics.
- **P04-4:** operation authority lease for one internal pilot effect.
- **P04-5:** Authority Composition prepare/pin/commit + reverse-order compensation for the pilot.
- **P04-6:** one-shot/quota atomic consume and closed effect-revocation policy.

## Tests

Deterministic barrier-based concurrency tests:

- derive linearizes before/after revoke;
- validate descriptor succeeds then revoke occurs — later effect without lease is denied by migrated pilot;
- lease acquired before revoke follows documented in-flight policy;
- two one-shot consumers => exactly one succeeds;
- quota consume races never overspend;
- revoked ancestor invalidates deep child;
- stale cache epoch cannot authorize.
- Region generation/MOVE or session close between prepare and commit denies and compensates;
- partial pin acquisition failure releases prior pins in reverse order;
- provider is never entered under an authority-registry lock;
- the pilot's revoke-after-admit/submit/before-publish behavior matches its static policy.

## Exit criteria

No effectful migrated path uses `Validate()` result alone across an unbounded race window. The real pilot composes all of its authority owners through the normative protocol, subtree revocation behavior is observable and stress-tested, and the lock/provider boundary is documented and executable-tested.
