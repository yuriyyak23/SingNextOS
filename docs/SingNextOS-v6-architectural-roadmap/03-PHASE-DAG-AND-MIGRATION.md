# v6 Phase DAG and Migration

## Dependency graph

```text
P00 baseline + live owner freeze
 |
 +--> P01 unified memory semantics -----------------------------+
 |       |                                                     |
 |       +--> P02 durability/persistent memory                 |
 |       +--> P04 translation/DMA authority ----------------+  |
 |       +--> P07 locality/data-motion ---------------------|--+
 |                                                           |
 +--> P05 machine-checkable refinement <---------------------+
 |       |
 |       +--> Proof-Carrying Lowering
 |       +--> P06 IFC
 |       +--> P09 device trust/attestation
 |
 +--> P03 temporal execution contracts
         |
         +--> P08 preemptible heterogeneous execution
         +--> P12 energy/thermal resources

P10 RAS consumes P01 + P04 + P05.
P11 multi-host consumes P01..P10 safety foundations and is not a first-wave blocker.
```

## Migration principles

1. All new APIs are additive and feature-gated.
2. Existing `OperationObligationsV1`, `ExecutionGuaranteesV1` and `SemanticExecutionBindingV1` remain valid compatibility contours until V2 qualification is complete.
3. Unknown V2 semantic classes fail closed when marked Mandatory and fall back to V1/staged execution only when the operation contract explicitly allows weaker semantics.
4. Direct coherent/shared-mutable paths remain OFF until P01 + P04 + P05 gates are closed.
5. Durable output remains staged until P02 is qualified.
6. Guaranteed/deadline execution remains claim-disabled until P03/P08 evidence exists.
7. Multi-host remains off by default and MUST NOT alter local single-host semantics.

## Recommended delivery waves

### Wave A — semantic foundations

P00, P01, P04, P05.

### Wave B — durable and temporal execution

P02, P03, P08.

### Wave C — trust and policy enrichment

P06, P09, Proof-Carrying Lowering.

### Wave D — optimization and resiliency

P07, P10, P12.

### Wave E — fabric scale

P11.

## Rollback

Every feature gate must allow a downgrade to the previously qualified staged path without reinterpreting a stronger token as a weaker authority. Rollback never converts stale V2 proof/evidence into V1 permission; it recomputes admission on the fallback path.
