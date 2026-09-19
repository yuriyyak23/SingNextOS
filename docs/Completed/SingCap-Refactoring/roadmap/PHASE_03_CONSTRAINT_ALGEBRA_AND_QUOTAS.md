# Phase 03 — Typed Constraint Algebra and Non-Amplifying Quotas

## Goal

Add monotonic derivation semantics to the existing capability ledger before enabling arbitrary V2 delegation.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Constraint families

Implement explicit typed constraints for the minimum useful set:

```text
RightsConstraint
ExactResource / typed subresource identity
OperationSetConstraint
RangeConstraint where the resource family supports ranges
LifetimeConstraint
TargetSubjectConstraint
SessionConstraint
DelegationDepthConstraint
QuotaConstraint / QuotaAccountReference
```

Do not create a universal string dictionary matcher as the security core.

## Algebra contract

Every family implements canonicalization and `IsSubset(child,parent)`. Unknown families fail closed. Checked integer arithmetic is mandatory for ranges/quotas.

## Quotas

Implement the Phase-00 decision; it is no longer an open option:

### Shared atomic account

Descendants point to an atomic `QuotaAccount`; consumption reduces one shared remaining budget.

Derivation may narrow a per-handle operation ceiling but never copies remaining capacity. The account is authoritative state owned by the existing capability ledger implementation, not a caller DTO or a second quota registry. Existing `ResourceBudgetAuthority` remains the owner of service resource-admission budgets; any integration must explicitly distinguish those budgets from consumable capability authority and must not double-charge or duplicate counters.

Reservation-transfer and child-local copied counters are out of scope for v1. Shared-account contention is measured explicitly in P13 and cannot be hidden by replacing the model with copied counters.

## No composition bypass

Normal untrusted code gets no API that unions sibling capabilities. Privileged composition, if ever added, must require explicit authority wider than the result.

## Primary paths

```text
contracts/SingPlus.Contracts/Capabilities.cs
new capability-constraint contracts under contracts/SingPlus.Contracts/
src/Runtime/SingPlus.Runtime/Capabilities/
tests/SingPlus.Tests/Capabilities/
```

Prefer a namespace/path consistent with current code rather than introducing a new top-level `Authority/` tree solely for aesthetics.

## PR slices

- **P03-1:** canonical constraint interfaces/internal forms.
- **P03-2:** rights/resource/operation/lifetime/session subset rules.
- **P03-3:** range algebra and overflow checks.
- **P03-4:** shared atomic quota account implementation and concurrent consumption.
- **P03-5:** property-based derivation tests.

## Tests

Property tests generate parent/child constraints and prove:

```text
admitted child never exceeds parent on any dimension
```

Negative cases include:

- rights widening;
- different resource;
- range expansion/overflow;
- operation addition;
- later expiry than parent;
- wrong target subject;
- session widening;
- depth reset;
- sibling quota amplification;
- concurrent consume beyond aggregate quota.

## Performance

Canonical constraints should be precomputed at mint/derive time. SIP validation must not repeatedly parse strings or allocate policy graphs.

## Exit criteria

Every derivation dimension has executable subset logic and property/adversarial coverage. Quota aggregate cannot exceed the parent/root budget under concurrency, and no compatibility/resource-budget projection creates a second remaining-counter truth.
