# P02 — Separate accounting budget from executable resource authority

## Goal

Preserve existing `ResourceBudgetAuthority` as a single accounting/capacity ledger and introduce a clear boundary for future resource authority.

## Why this phase exists

Current code intentionally says:

```text
reservations admit capacity but never authorize effects
```

That invariant is correct. vNext MUST NOT mutate `BudgetReservationHandle` into an authority token.

## Refactoring

Conceptually separate responsibilities:

```text
ResourceBudgetAuthority (existing)
  - system/service/process accounting hierarchy
  - configured limits
  - capacity reservation
  - pressure reporting

TemporalResourceAuthority (new, initially empty/off)
  - authority lineage
  - derivation/revocation
  - lease ownership
  - temporal resource rights
```

Physical class renaming of `ResourceBudgetAuthority` is optional and deferred. Documentation should refer to it as the **accounting ledger**.

## Contract evolution

Additive `ResourceBudgetContract` v2 may add correlation hooks only, e.g. authoritative account identity references, but MUST preserve:

```text
BudgetAccountSnapshot.MaterializesAuthority == false
BudgetReservationSnapshot.AuthorizesEffect == false
BudgetReservationSnapshot.AuthorizesReclaim == false
```

No caller-supplied budget descriptor may become executable authority.

## Bridge invariant

Minting a resource capability requires a privileged policy path that checks accounting headroom atomically. The accounting account is a prerequisite/source of capacity, not the capability itself.

## Migration tests

- all Phase05ResourceBudget tests continue unchanged;
- active ExternalEffect reservation remains pinned exactly as today;
- no existing service gains authority by possessing an account or reservation snapshot;
- serialization compatibility for v1 accounting DTOs.

## Exit criteria

The repository has an explicit accounting-vs-authority split with zero behavior regression.
