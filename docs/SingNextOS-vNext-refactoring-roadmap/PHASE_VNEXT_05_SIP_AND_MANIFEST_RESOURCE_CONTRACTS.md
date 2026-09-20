# P05 — SIP and ServiceManifest resource contracts

## Goal

Make resource requirements explicit in generated SIP metadata without making metadata an authority source.

## SIP contract vocabulary

Add versioned generated semantics equivalent to:

```csharp
[RequiresResourceBudget(ResourceClass.ComputeTime, MaxAmount = ...)]
[AcceptsResourceDonation(...)]
[ConsumesResourceLease]
```

Names are illustrative. Keep the surface closed and generator-known.

Do not expose a generic `object ResourceBudget` or user-constructible descriptor that runtime trusts.

## Generated sentry responsibilities

For an operation requiring resource authority:

```text
validate caller/session/protocol
validate normal CapabilityAuthority requirement
validate Region BORROW/MOVE requirements
resolve exact ResourceBudgetCapability handle
acquire/bind request-scoped lease
invoke implementation only after all required commits linearize
settle/close lease on every terminal path
```

No user/service code runs under authority locks.

## Manifest evolution

`ServiceManifestV2` remains additive over V1. Add declarations for:

- maximum resource authority requested at startup;
- accepted donation classes;
- whether resource authority is mandatory or optional;
- assurance requirement (`AccountingOnly` / upper bound / guarantee).

Manifest admission result remains evidence and `MaterializesAuthority=false`.

## Analyzer/admission

Reject:

- unbounded resource requirement;
- generic/unknown resource class;
- service that claims GuaranteedReservation without qualified platform support;
- dynamic reflection path that bypasses generated sentry.

## Exit criteria

Generated metadata and sentries understand resource requirements, but `FG-VNX-SIP-BUDGET-REQ` remains off until P06/P07 integration tests pass.
