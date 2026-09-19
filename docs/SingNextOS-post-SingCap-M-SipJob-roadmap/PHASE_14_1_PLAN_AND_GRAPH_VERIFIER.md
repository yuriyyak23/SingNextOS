# P14-1 — Immutable SipJob Plan, Stage Metadata and Graph Verifier

## Goal

Define a non-authoritative immutable plan model assembled from existing generated SIP metadata and introduce a verifier for a deliberately narrow **linear same-runtime MVP**.

## Scope

Initial supported graph:

```text
Entry -> Stage0 -> Stage1 [-> Stage2 -> Stage3] -> Exit
```

No branch/join, provider effect fusion, split-runtime path or arbitrary loop in the first gate. Maximum stage/edge counts are bounded and included in admission policy.

## Proposed internal model

Conceptual shapes (exact names may differ):

```text
SipJobPlan
  PlanId / PlanDigest
  ContractTupleDigest
  StageDescriptor[]
  EdgeDescriptor[]
  EntrySchema / ExitSchema
  RequiredFeatureGates
  MaximumResources

StageDescriptor
  StageId
  ServiceContractIdentity + exact digest/version
  MessageId
  GeneratedSentryThunkId
  StageInputShape / StageOutputShape
  AuthorityRequirement[] + AuthoritySource
  OwnershipTransition[]
  StageEffectClass
  EffectRevocationPolicy
  ProtocolTransitionDescriptor

EdgeDescriptor
  SourceStage / TargetStage
  EdgeKind = BoundedValue | Borrow | Move | DeclaredHandle
  schema/type/range constraints
```

The plan stores no `OperationAuthorityLease`, active session pin or “authorized” bit.

## Graph verifier

The verifier must prove before binding/execution:

```text
all stage contract digests and message IDs exist in the admitted generated catalog
all edge schemas exactly match producer/consumer declarations
no raw CLR/reference graph edge
all authority sources are explicit
no undeclared returned authority flows into a later stage
no unsupported effect/barrier appears in a linear-only gate
stage/edge/resource cardinalities are bounded
no cycles in P14-1
all required feature gates are enabled for the target runtime tuple
```

## Plan digest

Canonical digest binds at least:

```text
schema version
ordered stages/edges
contract digests
message IDs
generated sentry IDs
edge kinds and schemas
authority-source classes
ownership declarations
effect/barrier classes
feature-gate requirements
```

Digest is evidence/cache identity only.

## No implementation reference leakage

The public/ManagedCap surface may expose an opaque Job plan/binding handle or a generated typed builder, but not actual service objects/delegates. The trusted runtime maps `GeneratedSentryThunkId` to precompiled trusted code internally.

## Primary paths

```text
contracts/SingPlus.Contracts/ (opaque metadata/handles only if needed)
sdk/SingPlus.Generators/
sdk/SingPlus.Analyzers/
src/Runtime/SingPlus.Runtime/Services/ or new tightly scoped Jobs/
tools/SingPlus.Admission/
tests/SingPlus.Tests/Jobs/
```

## PR slices

- P14-1.1: versioned plan/stage/edge internal contracts.
- P14-1.2: deterministic canonicalization/digest.
- P14-1.3: linear graph verifier and bounded-resource policy.
- P14-1.4: generator output linking exact SIP method to generated stage metadata.
- P14-1.5: AdmissionVerifier rules rejecting raw CLR/dynamic stage references.

## Tests

Positive:

```text
2/3/4-stage valid linear plan
bounded copied-value edge
opaque handle edge
exact matching contract digest/message ID
```

Negative:

```text
raw object/class/delegate edge
stage substitution by name only
wrong contract digest/version
undeclared authority return
cycle
unbounded stage count
unknown edge type
required feature gate disabled
plan digest tamper/replay across incompatible runtime tuple
```

## Performance gate

Plan verification/binding is not on the per-element hot path. Measure cold bind separately from steady-state run. No reflection-based per-run method resolution.

## Exit criteria

`FG-JOB-LINEAR` can be enabled only in test/experimental builds after P14-8 qualification. The plan model is demonstrably non-authoritative and contains no ManagedCap-visible raw implementation references.
