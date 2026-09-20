# P07 — Bind resource leases to ExternalOperation lifecycle

## Goal

Compose resource authority with the existing external-effect state machine without collapsing either owner.

## Existing effect lifecycle remains unchanged

```text
Prepared -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published -> Released
```

Add an orthogonal resource lifecycle correlated by exact operation generation.

## New correlation

`OperationAdmissionSnapshot` / internal operation record may carry an opaque local `ResourceLeaseBinding` reference. It is not provider authority and should not expose consumable rights in public DTO fields.

## Admission ordering

```text
prepare Region uses
prepare external operation
reserve resource lease
final revalidate capabilities/session/generations
commit consumptive effect authority
bind resource lease to exact ExternalOperation generation
provider admission/submission
```

If any pre-submit step fails, reversible participants unwind. If provider submission may have happened, lease no longer returns automatically.

## Settlement

Provider or host runtime reports exact usage evidence. SingNext validates:

- exact operation identity/generation;
- exact provider generation snapshot;
- exact resource class;
- usage <= bound envelope or records violation;
- receipt ordering;
- no duplicate settlement.

Then local authority settles accounting/lease state.

## Provider-loss policy

After submission:

```text
ProviderLost/Unknown
 -> lease Quarantined
 -> Region uses remain governed by existing closure rules
 -> no new resource admission from that lease
```

Release only after exact close, trusted containment or a predeclared conservative worst-case charge policy.

## Exit criteria

`FG-VNX-EXTOP-RESOURCE-BIND` reaches RuntimeEnforced on deterministic host provider; effect publication behavior unchanged.
