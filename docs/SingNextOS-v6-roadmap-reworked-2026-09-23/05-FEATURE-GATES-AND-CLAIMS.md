# v6 Feature Gates and Claim Discipline

## 1. Global rule

All v6 gates start OFF. Gates are enabled per qualified contour and exact tuple. There is no global "v6 enabled" claim.

## 2. Gates

| Gate | Initial state | Maximum claim before hardware qualification | Notes |
|---|---:|---|---|
| `V6-MEMORY-SEMANTICS` | OFF | RuntimeEnforced / ExecutableAdapter | staged/exclusive contour first |
| `V6-SHARED-ATOMIC-REGION` | OFF | FutureGated | requires exact provider + ISE atomic/order evidence |
| `V6-DMA-TRANSLATION-BINDING` | OFF | RuntimeEnforced / ExecutableAdapter | zero-copy/SVA subcontours separately qualified |
| `V6-FORMAL-REFINEMENT` | OFF | StaticAdmission + executable trace evidence | model is not runtime authority |
| `V6-DURABLE-OUTPUT` | OFF | ExecutableAdapter on modeled provider; hardware-specific thereafter | no generic durability claim |
| `V6-TEMPORAL-CONTRACTS` | OFF | RuntimeEnforced for accounting/upper-bound subcontours | no hard RT claim |
| `V6-GUARANTEED-DEADLINE` | OFF | FutureGated | requires schedulability + enforcement |
| `V6-PREEMPTION` | OFF | ExecutableAdapter per provider | cancel alone does not qualify |
| `V6-STATEFUL-RESUME` | OFF | FutureGated unless exact provider proves capture/resume |
| `V6-IFC` | OFF | ModelOnly/StaticAdmission initially | protected contours only |
| `V6-LOCALITY-PLANNING` | OFF | advisory/performance evidence only | topology is evidence |
| `V6-DEVICE-ATTESTATION` | OFF | StaticAdmission/ExecutableAdapter on named producer | not permission |
| `V6-RAS-PARTIAL-FAILURE` | OFF | ModelOnly/ExecutableAdapter on fault model | hardware claim requires physical evidence |
| `V6-MULTIHOST-LEASES` | OFF | FutureGated | not on single-host critical path |
| `V6-ENERGY-BUDGETS` | OFF | measurement / EnforcedUpperBound where enforceable | no PowerAuthority |
| `V6-PROOF-CARRYING-LOWERING` | OFF | StaticAdmission / optimization evidence | never permission |

## 3. Claim levels

- `ModelOnly`: executable or written model/spec exists; no runtime enforcement claim.
- `StaticAdmission`: inputs/artifacts are validated before execution, but mutable runtime conditions may change later.
- `RuntimeEnforced`: live owner/runtime path enforces the claimed property for the named contour.
- `ExecutableAdapter`: a concrete cross-project adapter/provider path executes and exposes the necessary guarantee/evidence.
- `EnforcedUpperBound`: a measurable/controllable maximum is enforced; this is not minimum service.
- `GuaranteedReservation`: capacity is committed and protected from oversubscription according to the named owner/provider contract.
- `ProductionQualified`: fault, security, performance, operational, supply-chain and physical platform evidence is closed for a named deployment tuple.
- `FutureGated`: design vocabulary exists but implementation/qualification is intentionally disabled.

## 4. Promotion rules

A feature may move upward only when the next level has independent executable evidence. In particular:

```text
DTO != enforcement
parser != enforcement
fake provider != production adapter
telemetry != upper-bound enforcement
reservation != minimum service
completion != visibility/publication
attestation != permission
compiler proof != legality
model check != hardware qualification
emulator result != physical DMA/persistence/RAS evidence
```

## 5. Downgrade and rollback

A disabled/failed v6 contour returns to a **freshly admitted** previously qualified path. No V2/extension proof, lease, captured state or stale receipt is reinterpreted as V1 authority. Already-submitted external effects must complete, reconcile, contain or quarantine before cleanup claims are made.
