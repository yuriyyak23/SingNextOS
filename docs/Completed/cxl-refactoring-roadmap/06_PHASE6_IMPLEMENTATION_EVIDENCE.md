# Phase 6 — Type-2 Accelerator Service Evidence

## Implemented scope

**Complete for the staged model-provider scope.** The new narrow
`ICxlType2AcceleratorProvider` accepts provider-neutral compute operation,
publication path, exact RegionUse descriptors and opaque endpoint/binding
generations. It accepts no decoder, address, route, register, mailbox, opcode,
lane or PASID fields.

`CxlType2AcceleratorService` composes `ComputePlan` validation, existing device
authority, CXL fabric revalidation and the Phase-2 lifecycle. A successful
operation traverses Prepared, Admitted, Submitted, DeviceComplete, Visible,
Published and Released. The publication callback runs only after exact provider
completion, visibility and a second authority/generation validation.

`CxlType2ModelAccelerator` is a deterministic staged provider. It has no ISA,
compiler or L7-SDC dependency. Accelerator authority is independent of the
ordinary platform device lease role, allowing the same semantic device to keep
its existing `DeviceResourceSet` path.

Direct coherent plans are rejected unless an explicit Phase-7 policy admission
is supplied; the staged-only model still rejects them at its own capability
boundary. Reset/generation change after submit fails before publication and the
staged result is never committed.

## Executable evidence

Focused Type-2, planner, common-lifecycle and driver-resource tests prove:

- staged execution traverses every common lifecycle state before publication;
- publication changes output only after visibility;
- conflicting/stale region authority causes zero provider submissions;
- device rebind during execution prevents publication;
- direct output is Phase-7 gated;
- accelerator and ordinary device authority remain separate roles;
- the public descriptor contains no physical CXL transport controls;
- stale operation/binding completion rejection remains covered by the common
  lifecycle suite.

Final Phase 6 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused Type-2/planner/lifecycle/driver-resource tests
  PASS — 30/30

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 807/807 total
    677 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Claims and Phase 7 entry

This proves the Sing-side model composition, not a universal CXL accelerator
command ABI or hardware execution. Phase 7 must define the explicit effect,
visibility and replay contract required before direct coherent output can be
admitted.
