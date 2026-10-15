# Global Contract Evolution — normative requirements

## PHASE
Global semantic contract evolution from V1.

## Contract foundation
`OperationObligationsV1`, `ExecutionGuaranteesV1`, `SemanticExecutionBindingV1` are live and used by the semantic admission/refinement path. Resolve compiler/runtime and ExternalRuntime versions from the selected actual project/assets inputs; they are independently versioned.

## VERDICT
**Do not create a blanket V2 now.** The audit did not establish a breaking semantic need that justifies triplicating the ABI. Use additive, typed, canonical sidecars and only introduce V2 if a concrete breaking-representation criterion is met.

## Requirements to verify against actual consumers
- typed mandatory-vs-optional extension semantics;
- canonical ordering and digest of extension clauses;
- unknown mandatory class behavior;
- schema downgrade rules;
- extension-to-provider guarantee matching;
- exact binding of mutable owner generations without mutating V1 meaning.

## REMOVE_OR_MERGE
- remove immediate `OperationObligationsV2`/`ExecutionGuaranteesV2`/`SemanticExecutionBindingV2` as implementation requirements;
- keep V2 names reserved for a future proven breaking change.

## Candidate contract shapes
Use existing non-authoritative contracts where sufficient. These names describe candidate shapes, not a requirement to create parallel types or repeat implemented schema work:

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

## Applicable contract/consumer tests
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

## Current implementation navigation

[Actual state/backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md) takes precedence over historical missing-code observations. Completed bounded behavior must not be recreated without defect/drift. Full iteration journal is preserved in artifacts/v6/iteration-20261006-roadmap-modernize-split/history; original artifact directories remain unchanged.
