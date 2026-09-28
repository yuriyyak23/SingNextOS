# SingNextOS vNext feature gates — corrected

All new behavior is default-off until the exact source/provider/runtime contour reaches the stated evidence level.

## Evidence levels

```text
ModelOnly
StaticAdmission
RuntimeEnforced
ExecutableAdapter
EnforcedUpperBound
GuaranteedReservation
ProductionQualified
```

## Gates

| Gate | Default | Minimum evidence | Scope |
|---|---:|---|---|
| `FG-VNX-RESOURCE-GRANT` | OFF | RuntimeEnforced | resource-use grant family inside existing `CapabilityAuthority` |
| `FG-VNX-RESOURCE-LEASE` | OFF | RuntimeEnforced | `ResourceBudgetAuthority` reserve/bind/settle/quarantine |
| `FG-VNX-CROSSOWNER-ADMISSION` | OFF | RuntimeEnforced | atomic prepare/revalidate/commit protocol |
| `FG-VNX-SIP-RESOURCE` | OFF | RuntimeEnforced | generated SIP sentry resource admission |
| `FG-VNX-SESSION-DONATION` | OFF | RuntimeEnforced | request-scoped bounded donation |
| `FG-VNX-EXTOP-RESOURCE-BIND` | OFF | RuntimeEnforced | exact ExternalOperation↔lease binding |
| `FG-VNX-COMPUTE-V2` | OFF | RuntimeEnforced | semantic resource-aware planning |
| `FG-VNX-HCPU-RESOURCE-CONTRACT` | OFF | ExecutableAdapter | provider-neutral HybridCPU request/receipt seam if required |
| `FG-VNX-HCPU-USAGE-EVIDENCE` | OFF | ExecutableAdapter | exact operation/provider generation usage evidence |
| `FG-VNX-RESOURCE-SCHEDULER` | OFF | RuntimeEnforced | policy-only scheduler/agents |
| `FG-VNX-TEMPORAL-UPPER-BOUND` | OFF | EnforcedUpperBound | exact contour max-consumption enforcement |
| `FG-VNX-GUARANTEED-RESERVATION` | OFF | GuaranteedReservation | provider-specific minimum-capacity guarantee |
| `FG-VNX-SIPJOB-RESOURCE` | OFF | RuntimeEnforced | ordinary-SIP-equivalent fused resource path |
| `FG-VNX-DMA-THROUGHPUT` | OFF | ExecutableAdapter | DMA bytes/window contour |
| `FG-VNX-NETWORK-THROUGHPUT` | OFF | ExecutableAdapter | network bytes/window contour |
| `FG-VNX-FABRIC-THROUGHPUT` | OFF | ExecutableAdapter | CXL/fabric bytes/window contour |
| `FG-VNX-DEVICE-OCCUPANCY` | OFF | ExecutableAdapter | device memory/queue/inflight contour |
| `FG-VNX-ENERGY` | OFF | ModelOnly | future only; no runtime claim |
| `FG-VNX-AUDIT-ONLY` | OFF | RuntimeEnforced | telemetry collection with no decision side effects |
| `FG-VNX-HOST-RESOURCE-ADAPTER` | OFF | ExecutableAdapter | exact host/JIT ComputeTime/Nanoseconds provider resource contour only |
| `FG-VNX-SEMANTIC-SENTRY` | OFF | ExecutableAdapter | exact four-gate semantic admission sentry; no provider/runtime implementation is implied |

## Promotion rules

1. Evidence is pinned to exact SingNextOS SHA, HybridCPU/provider SHA/package version, contract version, runtime profile and test set.
2. A gate OFF path uses existing ordinary SIP/Compute/ExternalOperation semantics.
3. Provider availability/evidence cannot enable a gate dynamically.
4. Accounting evidence cannot promote `EnforcedUpperBound`; upper-bound evidence cannot promote `GuaranteedReservation`.
5. Host/model proof does not qualify HybridCPU; one HybridCPU provider does not qualify all providers.
6. JIT proof does not qualify NativeAOT unless separately tested.
7. Fake provider/parser/DTO tests do not qualify executable contours.
8. Gates must be independently rollback-safe.
9. No gate enables ISA or CHERI-like hardware changes.
