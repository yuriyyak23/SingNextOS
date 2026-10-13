# Reset Semantics

## Architectural reset

Architectural CPU/platform reset transfers control to immutable ROM and starts a new boot instance / `BootCapsuleGeneration`. Memory/media survival does not imply authority survival.

## Runtime backend reset

The current runtime already implements backend-epoch invalidation through `RuntimeKernel.PlatformBackendReset.cs::ObservePlatformBackendReset()` and `PlatformAuthority.BackendEpoch` semantics.

P15 must reuse this runtime owner for runtime provider/backend invalidation. It must not use this method as an implementation of architectural reset-to-ROM.

```text
ArchitecturalResetToRom != ObservePlatformBackendReset
```

## Required behavior

- pending boot PCI/CXL op + architectural/platform reset observation -> `ResetObserved`;
- HDM state uncertain after reset -> `Quarantined`;
- verified copied bytes may survive but mapping/provider/Region generations do not;
- late runtime completion from an old backend epoch is rejected;
- same BDF/DSN/HPA after reset does not restore authority;
- runtime backend reset continues to use existing quarantine/reclaim-block rules.

Any implementation that maps architectural reset directly onto runtime backend-epoch APIs is a Critical defect.
