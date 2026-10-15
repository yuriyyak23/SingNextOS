# 04. Fault, Reconfiguration, Teardown And Reclaim

## Goal

Join currently separate CXL, Virtualization and SecureCompute fault domains into one fail-closed orchestration path without creating a new global authority root.

## Reconfiguration behavior

On CXL hot-remove, Fabric Manager drain, backing generation change, security reset or device reset affecting a secure/virtualized workload:

1. stop new admissions for the affected exact binding;
2. mark dependent virtual/secure execution bindings Draining/Parked or Quarantined as appropriate;
3. close or contain provider effects;
4. suppress guest-visible publication/events until revalidation succeeds;
5. close secure guest overlays;
6. close guest mappings / VirtualIo as required;
7. release CXL backing/fabric/device authority only after exact closure;
8. permit local region/domain reclaim only after all dependencies are terminal.

## Fault projection

Guest-visible faults are typed virtual-domain events (`MemoryFault`, `DeviceOrIoFault`, termination requirement, etc.). Raw CXL/FM/HDM/provider diagnostics remain host/platform evidence unless explicitly projected by policy.

## Teardown order

```text
stop new composed admissions
 -> close secure virtualized compute/provider effects
 -> close secure guest-region protection
 -> close VirtualIo
 -> close guest mappings
 -> drain/revoke SecureExecutionBinding
 -> close SecureDomain
 -> close VirtualDomain/provider child
 -> close CXL coherent/memory/fabric bindings
 -> release backing/mapping reservations
 -> local RegionAuthority reclaim
```

Where existing implementation order differs, preserve the invariant that no lower authority is reclaimed while a higher external effect can still reference it.

## Ambiguity rule

Any ambiguous close transitions the affected composed context to quarantine and blocks reclaim. Retrying teardown may observe/recover exact closure; it must not reinterpret unavailability as containment.

## Required tests

- Type-3 hot-remove with live secure guest mapping;
- security reset while VM running and after DeviceComplete before Published;
- FM reconfiguration with live guest accelerator work;
- device reset with live VirtualIo;
- process termination with secure guest + Type-3 + Type-2 resources;
- every injected ambiguous close leaves authority pinned;
- recovery after exact terminal receipt permits reclaim exactly once.
