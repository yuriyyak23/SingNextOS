# P14-1 — SipJob Plan, Descriptor Model, and Graph Verifier

## 1. Goal

Define immutable non-authoritative metadata for a bounded linear Job and implement a closed-world verifier that can prove a plan refers only to admitted contracts, schemas, precompiled thunks, authority sources, and edge semantics.

P14-1 does not execute fused service code.

## 2. MVP contour

The verifier initially accepts only:

```text
2..4 stages
linear chain
same qualified managed runtime
synchronous stage declarations
precompiled generated SIP operations
bounded closed copied/value edges
no Region BORROW/MOVE
no external effect
no async
no DAG
no provider hint
one final externally visible publication
no independently observable intermediate invocation
```

Anything outside this contour is rejected or routed to ordinary SIP by the caller/planner.

## 3. Descriptor rules

All descriptors MUST be immutable value data with no ambient authority.

### 3.1 `SipJobPlanDescriptor`

Contains only values such as:

```text
PlanFormatVersion
StageDescriptors[]
EdgeDescriptors[]
DeclaredGateSet
PlanDigest
Optional semantic policy IDs whose schemas are versioned and digest-bound
```

### 3.2 `StageDescriptor`

Minimum semantic fields:

```text
StageId
ContractId + ContractDigest
OperationId
RequestSchemaId + Digest
ResponseSchemaId + Digest
GeneratedThunkId + ThunkDigest
DeclaredAuthorityRequirements[]
DeclaredOwnershipUseRequirements[]
DeclaredProtocolTransitionId
DeclaredInvocationObservability
DeclaredCancellationPolicyId
DeclaredEffectClass
DeclaredExecutionClass (semantic only; initially None/ManagedDefault)
```

The descriptor MUST NOT contain:

- implementation instance;
- runtime delegate;
- `Type`/`MethodInfo` used for unrestricted dispatch;
- `IServiceProvider`;
- service locator key that bypasses closed-world binding;
- raw `Task`/`ValueTask`;
- provider-private handle.

### 3.3 `EdgeDescriptor`

Initial edge kind:

```text
ClosedCopiedValue
```

Future edge kinds are versioned and independently gated:

```text
RegionBorrow
RegionMove
MaterializedSipBoundary
ExternalEffectBoundary
```

Every edge records:

```text
ProducerStageId
ConsumerStageId
ValueSchemaId + Digest
OwnershipUseSemanticsId
IsolationSemanticsId
PublicationSemanticsId
BarrierClassId
CancellationScopeId
```

## 4. Authority source descriptors

An authority requirement identifies how runtime validation locates an existing authority owner. It never contains an authorization result.

Allowed source classes are a closed enum, for example:

```text
CallerProvidedCapabilityReference
SessionBoundCapabilityReference
PreviousStageDeclaredReturn
RegionAuthorityHandle
SealedObjectReference
ExternalOperationReference
ExistingProtocolStateOwnerReference
```

`PreviousStageDeclaredReturn` is allowed only if the generated operation contract explicitly declares a supported transferable result form.

Unknown source class → reject.

## 5. Canonical PlanDigest

The digest MUST bind every field capable of changing execution/security/lifecycle semantics, including:

```text
format versions
ordered stages
ordered edges
contract/operation IDs and digests
request/response schema digests
thunk IDs/digests
authority source descriptors
ownership/use semantics
protocol transition IDs
invocation observability
cancellation scopes/policies
effect classes
barrier classes
join policy when DAG is later enabled
settlement/publication policies
semantic execution classes
required feature-gate set
```

Unknown fields in a newer descriptor version are not silently ignored. Either the exact version is supported or the plan is rejected.

A semantic mutation of one field MUST change the digest.

## 6. Verification algorithm

```text
VerifyPlan(plan):
    require supported PlanFormatVersion
    require stage count within gate bounds
    require graph shape allowed by active gate
    require every stage ID unique
    require every edge references existing stages
    require acyclic + exact linear ordering for MVP

    for stage in stages:
        require contract/operation present in closed manifest
        require request/response schema digest exact
        require precompiled thunk ID/digest exact
        require authority source classes supported
        require effect class supported by active gate
        require protocol/cancellation policy supported
        require no reference-bearing descriptor fields

    for edge in edges:
        require producer output schema compatible with consumer input schema
        require edge kind enabled
        require isolation/ownership semantics enabled
        require barrier class recognized

    recompute canonical PlanDigest
    require exact match

    return VerifiedPlanMetadata  // evidence only, not authority
```

`VerifiedPlanMetadata` MUST NOT contain `Authorized=true` as a permission shortcut. It may record static facts such as manifest lookups and canonical descriptor offsets.

## 7. Graph verifier properties

The MVP verifier MUST prove:

- exactly one entry and one exit;
- each non-entry stage has one predecessor;
- each non-exit stage has one successor;
- no cycles;
- no orphan stages;
- no duplicate edge;
- no hidden side edge;
- only the final output is externally published in the MVP contour;
- no intermediate invocation is declared independently observable;
- no stage requires a disabled gate.

## 8. Closed-value semantics

A copied/value edge does not mean "pass the same CLR reference".

The generator/verifier must identify a closed projection form whose ordinary SIP semantics can be reproduced directly. For reference-backed mutable source data, the fused path must still perform the generated snapshot/deep-copy/projection required for isolation.

The MVP should prefer primitive/immutable/blittable/generated record-like projections where aliasing is fully specified.

## 9. Static rejection cases

CI MUST include malformed plans for:

- unknown plan/descriptor version;
- unknown stage operation;
- mismatched contract digest;
- mismatched schema digest;
- mismatched thunk digest;
- duplicate stage ID;
- cycle;
- hidden disconnected stage;
- unsupported Region edge;
- external effect under linear-MVP gate;
- async declaration;
- provider-private execution field;
- implementation reference/delegate/service locator;
- generic object payload;
- mutable reference payload with no admitted projection;
- unknown barrier/cancellation/publication policy.

Expected result: fail before service code.

## 10. No runtime binding authority

P14-1 verifier may resolve manifest metadata but MUST NOT retain live service implementation references or authority leases in the plan.

Actual runtime binding is introduced only in P14-2/P14-6 and remains TCB-private plus revalidated.

## 11. PR decomposition

1. descriptor contracts + canonical serialization;
2. closed manifest/thunk catalog view;
3. linear graph verifier;
4. digest tests/property tests;
5. negative admission/analyzer fixtures.

No PR enables fused execution by itself.

## 12. Exit criteria

- canonical immutable descriptors exist;
- MVP linear graph verifier is exhaustive for supported forms;
- plan digest changes for every semantic mutation;
- no descriptor can carry an implementation/ambient-authority object;
- unknown versions fail closed;
- all negative fixtures pass;
- `FG-JOB-LINEAR` remains non-executing until P14-2.
