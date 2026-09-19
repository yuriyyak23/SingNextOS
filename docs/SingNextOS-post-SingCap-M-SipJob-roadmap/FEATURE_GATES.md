# SipJob Feature Gates

All gates default **OFF** unless the exact implementation tuple has the phase evidence named below. A disabled gate either uses normal SIP materialization or rejects plan construction; it never weakens authority checks.

| Gate | Initial state | Enables | Required phase/evidence |
|---|---|---|---|
| `FG-JOB-LINEAR` | OFF | linear 2–4 stage same-runtime Job plan | P14-1 + P14-8 |
| `FG-DIRECT-SENTRY` | OFF | direct generated sentry thunk instead of intermediate channel/response | P14-2 + P14-8 |
| `FG-REGION-BORROW` | OFF | qualified linear BORROW edges | P14-3 + P14-8 |
| `FG-REGION-MOVE` | OFF | qualified linear MOVE edges with terminal settlement proof | P14-3 + P14-8 |
| `FG-MULTI-SESSION-SEGMENT` | OFF | prepare/pin/commit across multiple sessions/owners | P14-4 + P14-8 |
| `FG-READONLY-DAG` | OFF | read-only fan-out DAG using qualified shared-read Region uses | P14-5 + P14-8 |
| `FG-DAG-PARALLEL` | OFF | parallel scheduling of read-only independent branches | P14-5/P14-8 contention + deterministic join evidence |
| `FG-ASYNC-STAGE` | OFF | fused segment suspension/resume using heap-safe leases | P14-5 + P14-8 |
| `FG-EXTERNAL-EFFECT-STAGE` | OFF | Job stages that enter existing external-operation lifecycle | P14-5/P14-7 + provider conformance + P14-8 |
| `FG-DYNAMIC-PLAN-BIND` | OFF | runtime-selectable graph from precompiled qualified stage thunks | P14-6 + P14-8 |
| `FG-PLAN-CACHE` | OFF | cache exact bindings/where-to-check metadata | P14-6 stale-generation/revocation cache tests |
| `FG-SPLIT-RUNTIME` | OFF | one plan containing local fused segments separated by normal SIP transport | P14-6 + P14-8 conformance |
| `FG-HYBRIDCPU-HINTS` | OFF | provider-neutral execution-class hints to scheduler/bridge | P14-7 + P14-8 |
| `FG-HYBRIDCPU-ACCEL` | OFF | actual qualified provider-backed DSC/Matrix/etc stage execution | separate provider evidence; not implied by hints |
| `FG-NATIVEISOLATED-FUSION` | OFF | fusion across NativeIsolated contour | FutureGated; requires independently qualified isolation model |
| `FG-MUTABLE-DAG` | OFF | shared mutable fan-out or multi-writer graph | **FutureGated beyond P14**; not part of this roadmap's release target |
| `FG-CONFIDENTIAL-DOMAIN-FUSION` | OFF | fusion crossing confidential/secure-compute domains | **FutureGated** until real enforcement exists |

## Gate ordering

Recommended release order:

```text
FG-JOB-LINEAR
  -> FG-DIRECT-SENTRY
      -> FG-REGION-BORROW
          -> FG-REGION-MOVE
              -> FG-MULTI-SESSION-SEGMENT
                  -> FG-READONLY-DAG
                      -> FG-DAG-PARALLEL
                          -> FG-ASYNC-STAGE
                              -> FG-DYNAMIC-PLAN-BIND / FG-SPLIT-RUNTIME
                                  -> FG-HYBRIDCPU-HINTS
                                      -> provider-specific acceleration gates
```

`FG-READONLY-DAG` is deliberately not part of the first linear MVP. It requires the P07 Region conflict matrix, P14 branch/join settlement proof, cancellation/join semantics and P14-8 race/contention qualification. `FG-MUTABLE-DAG` remains explicitly outside the P14 release target.

## Fail-closed rule

A plan that needs a disabled gate MUST produce one of two explicit outcomes:

```text
MaterializeOrdinarySipBoundary
RejectPlan(FeatureGateUnavailable)
```

Silently executing a weaker direct path is forbidden.
