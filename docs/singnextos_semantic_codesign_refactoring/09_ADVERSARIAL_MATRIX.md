# Mandatory Adversarial Matrix

| Scenario | Required result |
|---|---|
| revoke during admission | final revalidation fails or submit already linearized; never ambiguous authorization |
| revoke after submit | no new replay/publication authority; effect/settlement follows post-submit policy |
| session close during bind | exact invocation/session generation stale; no submit |
| process restart | old capabilities/sessions/leases/bindings stale; no resurrection |
| runtime/provider restart | generation drift -> stale/quarantine/reconcile |
| duplicate submit | exactly one submit-start winner |
| duplicate completion | idempotent exact duplicate or reject; never double transition |
| reordered evidence | reject/stale; no state skipping |
| cross-operation receipt replay | reject exact identity/correlation/generation mismatch |
| resource double spend/refund | one lease lineage, one terminal settlement |
| nested donation laundering | narrowing/provenance/priority ceiling preserved |
| guarantee laundering | unsupported/weaker guarantee cannot satisfy stronger obligation |
| Region reuse ABA | generation/mutation epoch mismatch fails closed |
| Complete but not Visible | publication blocked |
| Visible without OS publication decision | staged provider publication blocked |
| publication failure after consumption | resource settles independently; output discarded/quarantined as effect class dictates |
| cancel before submit | exact proof may release reversible reservations |
| cancel after possible submit | no refund/reclaim without terminal acknowledgement/containment/conservative settlement |
| epoch close races retire | close linearization waits/drains or returns not-closed; late effect after success is invariant violation |
| replay after revocation | fresh authority required; replay evidence cannot submit |
| stale topology | scheduler hint revalidation fails; replan |
| provider switches execution class | binding generation/class mismatch -> stale/rebind |
| integer overflow/time wrap | checked arithmetic; reject, never wrap grant/budget |
| multi-resource deadlock | up-front atomic vector/escrow or canonical protocol prevents hold-and-wait cycle |
