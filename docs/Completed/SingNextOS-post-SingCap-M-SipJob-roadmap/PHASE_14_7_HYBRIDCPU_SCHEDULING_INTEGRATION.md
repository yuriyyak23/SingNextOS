# P14-7 — HybridCPU-v2 / Provider-Neutral Scheduling Integration

## 1. Goal

Allow a qualified Job stage to express semantic execution preferences/requirements without turning HybridCPU topology, provider handles, legality evidence, or accelerator tokens into SingNext authority or SIP/Job ABI.

P14-7 is a scheduling/execution co-design layer, not a new permission system.

## 2. Authority/evidence boundary

The system preserves:

```text
Intent != Authority != Evidence != Publication
```

Local admission:

```text
LocalAllowed =
    exact local capability/effect admission
  + exact subject/resource generation
  + session/ownership/object/protocol state
```

Platform admission:

```text
PlatformAllowed =
    independent live provider/platform legality and admission
```

Effect execution:

```text
EffectAllowed = LocalAllowed && PlatformAllowed
```

Provider certificate/token/completion telemetry is evidence. It never becomes a SingNext capability.

## 3. Allowed Job-to-scheduler semantics

A stage may eventually declare a closed semantic `StageExecutionClass`, for example concepts such as:

```text
ManagedDefault
ComputeVectorizable
MatrixOrTileEligible
StreamingDataflowEligible
ExternalAcceleratorEligible
LatencySensitive
ThroughputOriented
DeterministicReplaySensitive
```

The exact enum is versioned and must describe semantics, not physical placement.

A class is a hint/eligibility constraint, not permission and not a promise that acceleration occurs.

## 4. Forbidden Job ABI

The following MUST NOT appear in `SipJobPlan`, ManagedCap-visible descriptors, or service contracts as scheduling authority:

```text
HybridCPU lane number/ID
raw opcode
typed-slot physical index
DSC internal token
L7/private accelerator handle
VMX/VMCS identifier
IOMMU/domain private handle
provider admission token
physical DMA queue
CXL topology/device path
microarchitectural topology selector
```

A provider-neutral runtime may internally map semantics to such details after independent legality/admission.

## 5. Provider contract compatibility rule

A semantic hint has three legal integration modes:

### Mode A — SingNext-internal hint

Used only by SingNext scheduling. No provider protocol change.

### Mode B — existing provider-neutral mapping

The hint maps losslessly to a field/operation already present in the exact qualified external runtime/provider contract.

No protocol change, but the mapping itself needs conformance tests.

### Mode C — new cross-boundary semantic

The existing provider contract cannot represent the hint.

Then P14 requires a separately versioned provider/external-runtime contract plus provider/HybridCPU implementation and conformance evidence. It is incorrect to claim "no HybridCPU/provider change" merely because no ISA opcode changes.

Unknown/unavailable hint support causes fallback or normal managed scheduling, never raw physical encoding by the Job layer.

## 6. Current-evidence discipline

P14-7 MUST consume only HybridCPU/provider contours proven by the exact pinned live code/tests.

Qualification of one contour does not imply another. In particular, treat independently:

- scalar/managed execution;
- VectorStream;
- MatrixTile;
- assists;
- lane6 DSC/DmaStreamCompute contours;
- lane7 external accelerator contours;
- virtualization;
- SecureCompute/confidential execution.

Future-gated or model-only features remain unavailable even if the Job semantic graph looks compatible.

## 7. Region/use semantics presented to provider

The Job may pass only the already-qualified provider-neutral representation of data/use intent.

It MUST NOT turn a `RegionAuthority` BORROW/MOVE into a raw provider handle without the existing platform bridge performing independent mapping/admission.

If the current provider path requires service-owned staging/copy for a contour, P14 cannot claim zero-copy merely because the Job edge was fused. Eliminating that provider staging requires separate provider ownership/lifetime evidence.

## 8. Typed-slot legality

HybridCPU typed-slot legality remains platform-owned.

The Job may describe semantic operands/dependencies, but:

- it does not allocate physical typed slots;
- it does not assert a lane is legal;
- it does not bypass legality predicates;
- it does not treat a legality certificate as local authority.

Reject/fallback taxonomy remains provider/platform-owned and is mapped to existing SingNext error/fallback semantics.

## 9. Staged execution and publication

A provider stage may have distinct states:

```text
local admission
platform admission
submission
execution
provider completion
memory/device visibility
local result validation
publication
Region ownership settlement
release
```

Job completion MUST NOT collapse them.

A provider reports completion does not imply:

- memory visibility is established;
- local capability/session is still valid for publication;
- output ownership has settled;
- result is published.

P14-7 must preserve the current retire/publication/evidence boundary of the exact provider contour.

## 10. Replay/rollback semantics

HybridCPU replay/certificate mechanisms are evidence/processor-runtime behavior and do not convert `SipJob` into a transaction.

If a provider contour can replay internally, Job semantics still follow:

- local authority rules;
- external-operation lifecycle;
- Region ownership/use;
- final publication rules.

The Job must not promise rollback of private service state or already irreversible external effects.

## 11. SecureCompute/confidential domains

`FG-CONFIDENTIAL-DOMAIN-FUSION` remains FutureGated.

P14-7 must not infer confidential execution support from generic provider hints, virtualization metadata, SecureCompute documents, or certificate objects.

A confidential-domain fusion feature requires a separate authority/isolation/activation/publication proof and exact live implementation evidence.

## 12. Required executable tests

### Double admission

Cases:

```text
LocalAllowed=false, PlatformAllowed=true  -> no provider submission
LocalAllowed=true,  PlatformAllowed=false -> fallback/deny by contract
both true                              -> may execute if contour qualified
```

### Provider generation restart

Warm Job binding, restart provider, run old handle. Old provider token/generation must not be accepted as authority.

### Unknown semantic hint

Old provider does not understand a new `StageExecutionClass`. Expected: explicit unsupported/fallback, never reinterpretation as a physical lane/opcode.

### Completion/visibility split

Delay visibility after provider completion. Job result must not publish early.

### Region/provider ownership

Attempt reclaim/release while provider may still access data. Existing external-operation/Region rules must prevent unsafe reuse.

### Unsupported contour

Request Matrix/L7/SecureCompute-like eligibility when exact provider contour is not qualified. Must remain managed/fallback/deny; no claim inflation.

## 13. Performance reporting

Report separately:

```text
Job fusion savings
provider acceleration savings
provider staging/copy cost
platform admission cost
visibility/publication cost
```

Do not attribute hardware speedup to Job fusion or vice versa.

## 14. PR decomposition

1. semantic execution-class model internal to SingNext, gate OFF;
2. mapping to existing provider-neutral contract where possible;
3. explicit versioned contract work only for semantics that cannot map;
4. double-admission and provider-restart tests;
5. exact provider contour qualification;
6. performance characterization separated from Job fusion baseline.

## 15. Exit criteria

- no physical/provider-private topology is part of Job ABI;
- local and platform admission remain independent;
- provider evidence is not local authority;
- new cross-boundary semantics are explicitly versioned;
- completion/visibility/publication remain separate;
- only exact live provider contours receive claims;
- NativeIsolated/confidential contours remain gated unless separately proven.
