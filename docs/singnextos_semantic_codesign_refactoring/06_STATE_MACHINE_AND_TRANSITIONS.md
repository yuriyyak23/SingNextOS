# Global State Machine and Composition

## Conceptual global state

```text
GlobalState = <
 CapabilityState,
 RegionState,
 ResourceState,
 SessionState,
 ExternalOperationState,
 ProviderState,
 HybridCpuState,
 PublicationState
>
```

No component owns the tuple. Composition rules constrain legal joint transitions.

## Core contour

```text
Intent
 -> Prepared                (reversible OS preparation)
 -> ObligationsBound        (immutable semantic snapshot)
 -> ProviderAdmitted        (provider-local admission)
 -> GuaranteesBound         (exact provider/runtime guarantee snapshot)
 -> RefinementSatisfied     (pure decision, no authority)
 -> SubmitCommitted         (irreversible-boundary linearization)
 -> Executing
 -> RetireObserved?         (CPU architectural state only)
 -> ProviderComplete
 -> VisibilityPending
 -> Visible
 -> PublishDecisionGranted  (SingNext policy/authority decision)
 -> Published
 -> Settling / Reclaiming
 -> Released / Settled
```

Retire is not required to precede all provider completion mechanisms for every heterogeneous provider; each execution class specifies the relation. The global model therefore stores evidence relations rather than assuming one universal sequence.

## Race rules
- revoke versus admission: final exact revalidation decides; post-submit revoke suppresses new publication/replay but cannot erase already-possible effect.
- Region mutation versus submit: exact Region generation/mutation epoch mismatch fails submit or publication.
- provider restart versus binding: provider generation drift -> stale/quarantine.
- complete versus visibility: completion alone cannot transition to Visible.
- visibility versus publish: Visible alone cannot publish staged output.
- settlement versus release: quantitative settlement and Region reclaim are independent owner transitions.
- restart/replay: no serialized live authority is resurrected.
