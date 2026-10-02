# Iteration 2 — Global Contract Evolution

## PHASE
Global semantic contract evolution from V1.

## BASELINE
`OperationObligationsV1`, `ExecutionGuaranteesV1`, `SemanticExecutionBindingV1` are live and used by the semantic admission/refinement path. HybridCPU compiler/runtime contract is v6; ExternalRuntime contracts are independently versioned.

## VERDICT
**Do not create a blanket V2 now.** The audit did not establish a breaking semantic need that justifies triplicating the ABI. Use additive, typed, canonical sidecars and only introduce V2 if a concrete breaking-representation criterion is met.

## VERIFIED_EXISTING
- V1 obligation/guarantee/binding records.
- runtime semantic refinement/evaluation path.
- exact contract/package versioning and compiler bridge version checks.
- existing generation-set and admission/publication contracts in ExternalRuntime.

## PARTIAL
- current V1 vocabulary is too narrow for memory ordering/atomicity, DMA mapping generations, temporal service, failure/trust and preemption clauses.
- canonical cross-project extension negotiation is not yet a stable public contract.

## GAPS
- typed mandatory-vs-optional extension semantics;
- canonical ordering and digest of extension clauses;
- unknown mandatory class behavior;
- schema downgrade rules;
- extension-to-provider guarantee matching;
- exact binding of mutable owner generations without mutating V1 meaning.

## REMOVE_OR_MERGE
- remove immediate `OperationObligationsV2`/`ExecutionGuaranteesV2`/`SemanticExecutionBindingV2` as implementation requirements;
- keep V2 names reserved for a future proven breaking change.

## NEW_REQUIRED
Recommended non-authoritative families:

```text
OperationSemanticExtensionsV1
ExecutionGuaranteeExtensionsV1
SemanticBindingExtensionSetV1
SemanticExtensionClassId
SemanticExtensionRequirement { Optional | Mandatory }
CanonicalSemanticExtensionDigest
```

Prefer dedicated extension payloads (`MemorySemanticsV1`, `DmaBindingV1`, `TemporalSemanticsV1`, etc.) over an untyped universal property bag.

## VERSIONING RULES
1. Each payload has a schema ID and version.
2. Canonical serialization is deterministic and hashable.
3. Duplicate semantic class IDs with conflicting payloads are invalid.
4. Unknown `Mandatory` class => deny.
5. Unknown `Optional` class => ignored only if absence does not weaken a requested semantic.
6. Downgrade requires fresh admission against the weaker contour; never reinterpret the strong artifact.
7. Binding digest includes base V1 objects plus canonical extension digests and relevant generation snapshot identifiers.

## AUTHORITY IMPACT
None. Extension records are requirements/guarantees/evidence; they mutate no authority store.

## HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT` only where HybridCPU/provider must expose a new guarantee/evidence class. Runtime legality remains independent.

## COMPILER IMPACT
None for baseline. PCL later adds optional evidence sidecars.

## ISA IMPACT
`NONE`.

## REQUIRED TESTS
- canonicalization and hash stability;
- unknown mandatory deny;
- unknown optional absence rules;
- duplicate/conflicting clause rejection;
- stale generation binding rejection;
- V1 fallback requires fresh admission;
- cross-language/assembly compatibility tests;
- package/API compatibility tests.

## EXIT CRITERIA
The first qualification vertical can express all required semantics without changing the meaning of any V1 field and without introducing a second owner.

## 2026-10-02 C0 telemetry counter exhaustion

Closed bounded managed V1 capture/drop counter slice. Existing kernel capture sequence long backing field advances by CAS only while 0 <= current < long.MaxValue; invalid/terminal values return existing CapacityExhausted without counter mutation. Last supported ID long.MaxValue is allocated exactly once. Clock observation precedes sequence allocation and remains outside owner locks. Sequence is capture identity, not gap-free publication/temporal/permission evidence; failed publication may consume a captured ID. No signed wrap, negative conversion exception after mutation or reset/ABA introduced.

Existing subscription owner guards Dropped==ulong.MaxValue before overflow counter, queue/dequeue or state mutation under the final telemetry publication gate. Final MaxValue increment preserves DropOldestWithMarker/RejectSample/StopSubscription V1 behavior. Existing TelemetrySubscriptionBatch reports nonzero Dropped and Complete=false; no new completeness authority/ledger/API/enum/schema. Physical coherence/clock failure completeness and fatal allocation compensation remain separately Partial.

Eleven new cases: full drop exhaustion and final drop increment across all three policies, capture -1/MinValue/MaxValue, concurrent last capture ID and concurrent last drop increment. Build 0 errors/9 warnings 21.23s before final drop race; focused 96 passed, final focused 97 passed after added race. Final broad/exact source/dependency tuple/hashes: artifacts/v6/iteration-20261002-c0-telemetry-counter-exhaustion/audit.json. All gates OFF, named managed no-wrap guard RuntimeEnforced only; Java excluded/skipped, ISA/opcode/CPU impact NONE. Next independent slice: nonfatal telemetry snapshot clock failure and existing batch incomplete consumer semantics.
