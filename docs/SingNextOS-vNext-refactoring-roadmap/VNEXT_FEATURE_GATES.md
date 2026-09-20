# SingNextOS vNext feature gates

All vNext features are default-off until the exact contour reaches the required evidence level.

## Evidence levels

```text
ModelOnly
StaticAdmission
RuntimeEnforced
ExecutableAdapter
GuaranteedReservation
ProductionQualified
```

No level implies the next one.

## Gates

| Gate | Default | Minimum enable evidence | Scope |
|---|---:|---|---|
| `FG-VNX-RESOURCE-CAP` | OFF | RuntimeEnforced | resource capability ledger/derivation |
| `FG-VNX-RESOURCE-LEASE` | OFF | RuntimeEnforced | reserve/bind/settle/release |
| `FG-VNX-SIP-BUDGET-REQ` | OFF | RuntimeEnforced | generated SIP resource requirement |
| `FG-VNX-SESSION-DONATION` | OFF | RuntimeEnforced | bounded request-scoped donation |
| `FG-VNX-EXTOP-RESOURCE-BIND` | OFF | RuntimeEnforced | exact ExternalOperation binding |
| `FG-VNX-COMPUTE-V2` | OFF | RuntimeEnforced | resource-aware ComputePlan |
| `FG-VNX-HCPU-RESOURCE-CONTRACT` | OFF | ExecutableAdapter | HybridCPU provider-neutral resource seam |
| `FG-VNX-HCPU-USAGE-EVIDENCE` | OFF | ExecutableAdapter | exact usage evidence correlation |
| `FG-VNX-RESOURCE-SCHEDULER` | OFF | RuntimeEnforced | policy layer, no authority ownership |
| `FG-VNX-TEMPORAL-UPPER-BOUND` | OFF | RuntimeEnforced | enforced max consumption |
| `FG-VNX-GUARANTEED-RESERVATION` | OFF | GuaranteedReservation | minimum capacity guarantees |
| `FG-VNX-SIPJOB-RESOURCE` | OFF | RuntimeEnforced | resource-aware fused stage path |
| `FG-VNX-DMA-BANDWIDTH` | OFF | ExecutableAdapter | DMA resource class |
| `FG-VNX-CXL-BANDWIDTH` | OFF | ExecutableAdapter | CXL/fabric resource class |
| `FG-VNX-DEVICE-OCCUPANCY` | OFF | ExecutableAdapter | device memory/context/queue occupancy |
| `FG-VNX-ENERGY` | OFF | ModelOnly | energy authority; defer runtime claims |

## Promotion rules

1. Promotion is monotonic only for the exact provider/runtime/contract tuple.
2. Fallback must remain ordinary SIP/Compute/ExternalOperation behavior while a gate is off.
3. A provider feature may be `ExecutableAdapter` while a hard time guarantee remains unsupported.
4. Provider-reported availability is evidence, not a gate switch.
5. A gate may be independently rolled back without invalidating unrelated owners.
6. No gate enables HybridCPU ISA changes.
