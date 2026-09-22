# Reset Semantics

P15 has two distinct reset domains that MUST NOT be conflated.

## Architectural CPU/platform reset

Architectural reset transfers control to immutable ROM and starts a new boot instance / `BootCapsuleGeneration`.

Physical memory/media may survive. Authority generations do not survive by implication.

```text
warm-reset physical survival != generation survival
```

## Runtime backend reset

Existing runtime platform-authority semantics — for example `ObservePlatformBackendReset()`, `BackendEpoch`, stale-generation quarantine/reclaim blocking, or their current equivalent — invalidate outstanding backend work and authority from the old epoch.

P15 must reuse the existing owner instead of adding a boot-specific runtime epoch authority.

## Required transition rules

- reset while PCI/CXL op pending -> result `ResetObserved`;
- late completion from old reset epoch -> ignored/rejected;
- reset during HDM transaction -> reverse-compensate if provably safe, otherwise `Quarantined`;
- reset after verified copy but before kernel admission -> BootInfo remains evidence only;
- warm reset with same BDF/DSN/HPA -> no authority continuity;
- runtime backend reset -> provider generation stale; reclaim/ownership publication follows existing runtime policy.

## Explicit non-equivalence

```text
ArchitecturalResetToRom != ObservePlatformBackendReset
```

The first restarts boot. The second is a runtime backend lifecycle/authority event.

Any roadmap/code path treating them as interchangeable is a Critical defect.
