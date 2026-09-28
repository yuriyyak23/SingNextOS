# Corrected PR Slicing and Cutover

## Rules

- One PR changes one semantic seam whenever possible.
- Contract-only PRs make no enforcement claim.
- Every owner mutation PR includes race/negative tests in the same change.
- Provider/HybridCPU changes are separate from SingNext owner changes unless a joint atomic package bump is required.
- Feature gates remain OFF through schema and adapter landing.
- A claim/evidence PR is separate and references the exact merged SHAs/package digests.

## Standard slice pattern

```text
A. schema / canonicalization / API compatibility
B. model/reference evaluator
C. owner-side runtime transitions behind gate
D. provider/HybridCPU adapter changes
E. race/fault/negative tests
F. differential trace + performance
G. qualification artifact + gate promotion
```

## Cutover

The first cutover target is QV1 (`23-FIRST-QUALIFICATION-VERTICAL.md`). V1/staged behavior remains production default until QV1 evidence closes. Subsequent features are enabled per contour, not globally.

## Rollback

Rollback disables the feature gate, stops new admissions to the new contour, and fresh-admits new work on the previous qualified path. In-flight external effects must complete, contain, reconcile or quarantine; rollback is not permission to forget possible effects.

## ABI policy

V1 contract types remain available. Additive sidecars are versioned independently. A true V2 contract family requires an ADR demonstrating why a sidecar cannot safely encode the required semantics.
