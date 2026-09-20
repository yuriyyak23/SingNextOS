# P14 — Restart, checkpoint, revocation and ambiguous-consumption reconciliation

## Goal

Make resource authority safe under the same adversarial lifecycle conditions already handled by SingCap-M and ExternalOperation.

## Restart

Runtime/service restart advances authority realm/incarnation. Old resource handles and donations become stale.

Provider restart advances provider generation; old usage receipts/bindings cannot settle new leases.

## Checkpoint

Ordinary checkpoint MUST NOT serialize live executable resource authority as reusable credentials.

Checkpoint may capture logical policy/accounting intent, but restore requires fresh authority mint under current policy/generations.

For live external operations, current checkpoint refusal/quarantine rules remain; a resource lease cannot make an unsafe operation checkpointable.

## Ambiguous provider consumption

Define reconciliation outcomes:

```text
ExactNoConsume      -> refund unused reservation
ExactUsage(x)       -> settle x
WorstCaseCharge     -> charge declared max then release
ContainedAndClosed  -> settle/close under containment proof
Unknown             -> remain Quarantined
```

No timeout alone implies `ExactNoConsume`.

## Revocation

Revocation prevents future reserve/bind. Already Submitted effects follow explicit P07 lifecycle; authority revocation does not erase real hardware work.

## Replacement

Service/provider replacement must prove no stale donation or lease can bind to the replacement generation.

## Exit criteria

Crash/restart/checkpoint suites show no authority resurrection, refund duplication or stale settlement.
