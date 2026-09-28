# Iteration 5 — Minimal Formal Semantics Core

## Goal
Define only the mathematical core needed to check cross-layer refinement. Do not introduce a theorem prover or attempt a full machine proof before the state/event vocabulary stabilizes.

## Core objects

```text
ObligationSet O
GuaranteeSet G
RuntimeState R
OwnerGenerationVector E
ProviderTrace T_p
SemanticTrace T_s
Projection pi : T_p -> T_s
Refinement relation G <= O  (read: guarantees satisfy obligations)
```

The relation is a product of independently typed domains rather than one total score:

```text
memory ordering/atomicity
visibility/publication
resource upper bounds/reservations
failure/containment
preemption/resume
trust predicates
optional temporal quantities
optional durability quantities
```

For each dimension, `G <= O` means the guarantee is at least as strong as the obligation according to that dimension's partial order. Incomparable guarantees do not refine by convenience.

## Mandatory vs optional

An obligation dimension is either `Mandatory` or `Optional`. Missing/unknown mandatory semantics deny refinement. Optional absence may fall back only where the operation explicitly accepts the weaker behavior.

## Trace property

For the selected contour:

```text
pi(HybridCPU/provider trace) in AllowedTraces(SingNext obligations, live owner states)
```

Trace projection must erase provider-private details without erasing security-relevant events such as submit, possible-effect, retire, visibility, publication, quarantine and generation change.

## Initial state machine

```text
Prepared
 -> Admitted
 -> Submitted
 -> EffectPossible
 -> DeviceComplete/Retired
 -> Visible
 -> Published
 -> Settling
 -> Released

Exceptional overlays:
CancelledBeforeSubmit
DrainRequested
Contained
Quarantined
ProviderLost
GenerationStale
RecoveredFresh
```

Persistence adds `Persisted` and `Durable` only on a separately qualified contour.

## Tool assignment

- **TLA+/PlusCal:** mutable-owner concurrency, revoke/submit races, cancel/retire, generation invalidation, lease expiry, provider loss/quarantine.
- **Alloy or finite relational exploration:** owner graph uniqueness, alias/mapping configurations, subrange damage, lease delegation narrowing.
- **Property/state exploration:** runtime state machines and canonicalization.
- **Differential execution:** ordinary SIP/reference path vs optimized/heterogeneous path projected to the same semantic trace.
- **Trace checking:** provider/HybridCPU event stream against allowed semantic traces.

No theorem prover is required for the first vertical.

## Formal claim ceiling

Model checking proves properties of the model and explored bounds. It does not prove the C# implementation, hardware ordering, device firmware, compiler correctness or production security. Those require separate executable evidence.
