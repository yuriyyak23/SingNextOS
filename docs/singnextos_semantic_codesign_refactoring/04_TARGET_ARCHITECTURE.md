# Target Semantic Architecture

## Surface 1 — `OperationObligations` (NEW_PROPOSED)

An immutable, versioned descriptor assembled by SingNext orchestration from current authoritative owner snapshots immediately before provider binding. It SHOULD contain typed fields for semantic effect, data/ownership use, resource envelope(s), temporal/preemption/cancellation constraints, isolation, visibility/publication policy, replay/determinism, failure/containment, locality and optional information-flow constraints.

It MUST NOT contain live capabilities as portable credentials, CPU lane IDs, raw topology, or a bool `Authorized=true`.

## Surface 2 — `ExecutionGuarantees` (NEW_PROPOSED)

A versioned provider/runtime claim for one semantic execution class. Dimensions include measurement/enforcement, preemption class/bound, isolation/contention, visibility/publication enforcement, replay/determinism, effect containment/failure, locality and retire evidence.

A guarantee dimension has explicit strength/semantics. Unsupported is a valid result and causes refinement failure for mandatory obligations.

## Surface 3 — `SemanticExecutionBinding` (NEW_PROPOSED)

Provisional non-colliding name. Exact binding context:

```text
ExternalOperationCorrelation
SingNext OperationGeneration
ProviderGeneration / ExternalGenerationSet
ExecutionContractVersion
SemanticExecutionClass
ResourceEnvelopeIdentity/Version
MeasurementContractIdentity/Version
VisibilityContractIdentity/Version
PublicationContractIdentity/Version
Replay/DeterminismClass
EffectEpoch/ContainmentDomain (optional)
```

This binding is evidence/enforcement context only. It is stored with or referenced by the existing ExternalOperation record; it is not a new authority ledger.

## Typed refinement

`GuaranteesRefineObligations(G,O)` is a pure, total, version-aware function. Each dimension defines its own partial order. Unknown version/value => false/stale. Optional/advisory obligations are represented explicitly; mandatory obligations cannot be silently dropped.

## Irreversible-boundary sentry

```text
current SingNext authority owners revalidated
AND exact invocation/session is live
AND exact Region uses are live
AND exact resource lease(s) are live
AND exact provider generation/binding is current
AND typed refinement succeeds
AND provider admission succeeds
AND HybridCPU runtime legality allows
    => commit submit marker / cross irreversible boundary
```

No provider callback runs under owner locks. The protocol uses reversible prepare, exact final revalidation, single submit-start linearization, and explicit pre-submit compensation.
