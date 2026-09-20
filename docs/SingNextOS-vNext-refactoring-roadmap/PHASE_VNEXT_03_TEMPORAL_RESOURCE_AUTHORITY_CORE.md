# P03 — TemporalResourceAuthority core

## Goal

Implement the first-class authority owner for time/resource rights.

## Proposed public opaque types

```csharp
ResourceBudgetCapabilityHandle
ResourceAuthorityRealmId // may reuse existing AuthorityRealmId if exact semantics fit
ResourceAuthorityGeneration
ResourceDelegationHandle
```

Public handles contain opaque identity only. Descriptors/snapshots are inspection evidence and non-authoritative.

## Internal authoritative record

Conceptual schema:

```text
ResourceAuthorityRecord
  realm
  token
  issuer subject/generation
  target subject/generation
  resource class
  canonical resource constraint
  parent lineage / revocation node
  delegable reserved amount
  consumed/reserved state references
  delegation depth
  validity interval
  assurance class
  state { Active, Revoked, Retired }
```

## Mint path

Only privileged policy/admission code may mint root/child resource authority. Ordinary services cannot construct authority from DTOs.

Minting from accounting capacity:

```text
validate policy + account capacity
 -> atomically reserve delegable capacity
 -> create authority record
 -> return opaque handle
```

## Derivation

Derivation must reuse SingCap-M principles:

- child constraints subset parent;
- bounded delegation depth;
- parent-child graph established atomically;
- revoke/derive race has one linearization winner;
- no independent child counters that amplify aggregate authority.

## Revocation

Revocation blocks future reservations. Already-bound in-flight operations follow explicit P04/P07 closure semantics; revocation MUST NOT fabricate cancellation of submitted effects.

## Concurrency tests

- concurrent split: sum never exceeds parent;
- derive vs revoke linearization;
- replay after runtime realm restart;
- subject generation restart;
- 1000+ adversarial split/delegate loops;
- table exhaustion/wrap fails closed.

## Exit criteria

`FG-VNX-RESOURCE-CAP` may be promoted to `RuntimeEnforced`; no effect path consumes it yet.
