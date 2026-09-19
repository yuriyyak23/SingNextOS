# P14-4 — Multi-Session Composed Admission and Fusion Segments

## Goal

Generalize the P04 Authority Composition Protocol to a Job segment containing multiple existing SIP stages without turning the Job executor into a second admission authority.

## Why segmentation is required

A complete future graph may contain one-shot rights, quotas, conditional stages and irreversible effects. Acquiring all stage authority at Job entry can consume rights for stages never reached. Therefore execution is split into **admission segments**.

```text
Segment 0: local managed/Region work
    -> barrier
Segment 1: exact effect admission + local continuation
    -> barrier
Segment 2: ...
```

Authority is acquired at the latest bounded point before the stage/segment that needs it.

## Segment prepare/pin/commit

Conceptual protocol:

```text
resolve current subject/service/session identities
acquire exact EndpointSession pins in deterministic order
acquire exact CapabilityAuthority operation leases
acquire exact seal pins if required
acquire exact Region use/borrow/transfer reservations
bind observed generations/epochs to one SegmentAdmissionAttemptId
revalidate prepared participants
commit segment admission
release registry locks
execute trusted sentries/service code
```

The attempt object contains correlations/evidence only.

## Deterministic ordering

Document one total ordering or reservation/revalidation discipline. Recommended stable key order for multi-record preparation:

```text
Process/subject identity observations
EndpointSessionId + generation
Capability stable internal identity
Sealed object stable identity
RegionId + generation
ExternalOperation identity (only once P14-5/7 gate enables it)
```

No external provider, blocking wait or user code executes while these owners are locked.

## Exact-capability binding optimization

At plan bind time the runtime may resolve “Stage 2 requirement X” to an exact capability record/reference candidate. Per run it validates that exact current reference/epoch/generation rather than scanning an enumerable capability bag. This is a cache of **where to validate**, not a cached positive authorization decision.

## Protocol/session handling

Multiple stages may use multiple `EndpointSessionHandle`s. A Job segment cannot infer that co-resident services share session authority. Each exact session remains separately pinned/revalidated. Session close transitions to draining/closed under existing rules.

## One-shot/quota tests

Required proof:

```text
unreached stage does not consume quota/one-shot authority
exactly one competing segment admission consumes a one-shot right
revoke before segment admission denies
revoke after committed admission follows existing EffectRevocationPolicy
partial acquisition failure compensates pins/leases in reverse order
```

## Primary paths

```text
src/Runtime/SingPlus.Runtime/Capabilities/RuntimeKernel.EffectAdmission.cs
src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs
src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs
src/Runtime/SingPlus.Runtime/Capabilities/ or Jobs segment coordinator
src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs
```

## PR slices

- P14-4.1: segment model + non-reusable admission attempt identity.
- P14-4.2: multi-session pin collection with reverse compensation.
- P14-4.3: exact capability-reference binding + live revalidation.
- P14-4.4: Region/seal participant composition.
- P14-4.5: one-shot/quota segmented admission tests.

## Concurrency tests

Barrier-based races for:

```text
session close vs segment commit
capability revoke vs segment commit
seal close/service restart vs segment commit
Region MOVE/reclaim vs segment commit
two Jobs competing for one-shot/quota
opposite multi-record acquisition orders
```

Assert permitted winner and zero leaked pins/leases.

## Exit criteria

`FG-MULTI-SESSION-SEGMENT` has executable prepare/pin/commit evidence across representative capability/session/Region/seal owners, and authority for future stages is not consumed prematurely.
