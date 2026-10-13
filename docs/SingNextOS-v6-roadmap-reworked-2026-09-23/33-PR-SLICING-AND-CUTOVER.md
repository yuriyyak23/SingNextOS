# Corrected PR Slicing and Cutover

## Rules

- One PR changes one semantic seam whenever possible.
- Contract-only PRs make no enforcement claim.
- Owner mutation changes include meaningful negative/race checks for the affected mutable dependencies; use existing consumer tests where sufficient, not tests mirroring helpers.
- Provider/HybridCPU changes are separate from SingNext owner changes unless a joint atomic package bump is required.
- Feature gates remain OFF through schema and adapter landing.
- Promotion/qualification is separate and identifies exact source/package/toolchain/deployment inputs; dirty dev attribution follows policy42. No commit/PR is mandatory without user request.

## Suggested slice components

Select only changed components; existing schemas, models and passed cuts are not mandatory rework. Qualification/promotion is a separate checkpoint.

```text
A. schema / canonicalization / API compatibility
B. model/reference evaluator
C. owner-side runtime transitions behind gate
D. provider/HybridCPU adapter changes
E. race/fault/negative tests
F. applicable differential trace; performance only for relevant hot-path/claim changes
G. separate qualification campaign; gate promotion only after exact deployment exits
```

## Cutover

The first cutover target is QV1 (`23-FIRST-QUALIFICATION-VERTICAL.md`). V1/staged behavior remains production default until QV1 evidence closes. Subsequent features are enabled per contour, not globally.

## Rollback

Rollback disables the feature gate, stops new admissions to the new contour, and fresh-admits new work on the previous qualified path. In-flight external effects must complete, contain, reconcile or quarantine; rollback is not permission to forget possible effects.

## ABI policy

V1 contract types remain available. Additive sidecars are versioned independently. A true V2 contract family requires an ADR demonstrating why a sidecar cannot safely encode the required semantics.
