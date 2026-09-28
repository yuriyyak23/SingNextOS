# Corrected Claim Matrix

The table states the **maximum realistic claim at the current emulator/runtime level** and the condition for promotion. A blank higher level is not implied.

| Feature | Current realistic level | Target software level | Hardware/production requirement | FutureGated? |
|---|---|---|---|---:|
| existing capability authority | RuntimeEnforced | RuntimeEnforced | normal production regression | no |
| existing Region ownership | RuntimeEnforced | RuntimeEnforced | hardware memory safety still platform-specific | no |
| existing resource accounting | RuntimeEnforced | RuntimeEnforced | provider reservations if claimed | no |
| v6 staged memory semantics | ModelOnly -> ExecutableAdapter after QV1 | ExecutableAdapter | physical memory-order campaign for hardware claim | no |
| shared atomic Region | ModelOnly | RuntimeEnforced only after exact provider support | hardware atomic/coherence/order tests | YES initially |
| DMA generation binding | ModelOnly -> ExecutableAdapter | ExecutableAdapter | real IOMMU/device invalidation/reset tests | no for adapter; hardware promotion gated |
| SVA/PASID zero-copy | ModelOnly | provider-specific | real SVA/IOMMU/page-fault tests | YES until named provider |
| refinement checker | ModelOnly/StaticAdmission -> RuntimeEnforced checker | RuntimeEnforced checker | N/A to physical semantics | no |
| formal model | ModelOnly | ModelOnly + qualification evidence | never directly ProductionQualified | no |
| durable output | ModelOnly | ExecutableAdapter named provider | persistence-domain/crash hardware tests | YES per provider |
| temporal accounting | RuntimeEnforced base | RuntimeEnforced | provider-specific for service guarantees | no |
| enforced upper bound | provider-dependent | EnforcedUpperBound | contention/hardware enforcement | per provider |
| guaranteed reservation | provider-dependent | GuaranteedReservation | protected capacity evidence | per provider |
| hard deadline/WCET | Unsupported | FutureGated | hardware/runtime schedulability proof | YES |
| basic preemption | provider-dependent | ExecutableAdapter | device/runtime safe-point tests | per provider |
| stateful resume | ModelOnly | provider-specific | reset/state integrity evidence | YES initially |
| IFC protected contour | ModelOnly | StaticAdmission/RuntimeEnforced narrow contour | security review/hardware isolation if relevant | YES initially |
| locality planning | ModelOnly/advisory | performance-qualified advisory | topology/performance production tests | no authority claim |
| device attestation | ModelOnly/StaticAdmission | ExecutableAdapter | real device/firmware trust chain | YES for production security |
| RAS partial failure | ModelOnly | ExecutableAdapter fault model | ECC/poison/reset/reconfig hardware campaign | YES for production |
| energy measurement | provider-dependent measurement | ExecutableAdapter | counter accuracy/attribution | per provider |
| energy enforced cap | ModelOnly | EnforcedUpperBound if provider supports | physical power enforcement | YES until named provider |
| PCL | ModelOnly/StaticAdmission | StaticAdmission + optimization evidence | N/A for authority; supply-chain validation | optional |
| multi-host leases | ModelOnly | ModelOnly/simulation first | real multi-host/fabric fault campaign | YES |
| production security | not claimable for new v6 features as a whole | contour-specific | full platform + ops + supply-chain evidence | YES until closed |

## Promotion prohibition

No row may be promoted because a neighboring row reached a stronger level. Claims are per feature and exact tuple.
