# Phase 16 — Fabric Exactness and Generic Effect Containment

Status: **Complete for staged single-host software/model scope.**

## Verified audit findings

The external follow-up audit identified four real gaps in the current source:

1. pool release and reconfiguration completion removed provider records before
   validating the complete supplied identity;
2. a reconfiguration ticket could outlive an endpoint generation change and
   overwrite the newer fabric generation;
3. provider-neutral Fabric Manager tracking permitted post-submit operations
   without a provider closure proof;
4. Type-3 and pool creation failures did not distinguish proven pre-effect
   rejection from ambiguous acceptance.

All four were reproduced from the implementation and corrected.

## Implemented closure

- `ReleasePoolCapacity` performs lookup and full equality validation before
  removing the current assignment or changing pool capacity.
- `CompleteReconfiguration` validates the exact current ticket, exact previous
  binding, draining state and monotonic replacement generation before consuming
  the ticket. Endpoint hot-remove/rebind invalidates affected tickets and clears
  their obsolete draining state, preventing rollback and ABA.
- `TrackOperation` rejects `Submitted`, `DeviceComplete` and `Visible`
  operations without a provider closure/containment callback. Reconfiguration
  passes `ProviderResourcesClosed=true` only after that callback succeeds.
- Effect-creating fabric, memory and pool APIs use `NotAccepted` only for proven
  zero-effect rejection. `Unavailable` is acceptance-ambiguous. Runtime bridges
  retain dependent backing/use authority in quarantine and block process reclaim.
- Provider contracts require failures to be returned rather than thrown.
  Runtime entry points additionally contain Type-2, fabric, memory, pool and
  reconfiguration exceptions after the effect boundary as
  `ExternalEffectUncontained` quarantine.

## Added negative evidence

- `StalePoolRelease_DoesNotDeleteCurrentAssignmentOrChangeCapacity`
- `StaleReconfigurationTicket_DoesNotConsumeCurrentTicket`
- `EndpointGenerationChangeDuringReconfiguration_DoesNotRollbackGeneration`
- `PostSubmitTrackingWithoutProviderClosureProofIsRejected`
- ambiguous fabric/memory/pool acceptance retains pins and blocks reclaim
- provider exceptions after Type-2/fabric/memory acceptance are contained
- pre-effect model rejections use `NotAccepted` and create no provider effect

## Executable qualification

```text
focused Phase 11/14/15/16 matrix: 111/111 passed
fresh forced no-cache restore: passed for 26 projects
full solution: 869/869 passed
  739 SingPlus.Tests
   60 SingPlus.Platform.HybridCpu.Tests
   58 HybridCPU_NeutralRuntime.Tests
   12 HybridCpu_ExecutableAdapter.Tests
failures: 0
skipped: 0
```

`DirectCoherentWrite` remains `FutureGated`. QEMU, FPGA and physical-hardware
claims remain outside this qualification.
