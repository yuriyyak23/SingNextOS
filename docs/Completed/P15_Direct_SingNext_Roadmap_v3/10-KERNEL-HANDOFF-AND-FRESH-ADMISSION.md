# Kernel Handoff and Fresh Admission

## Existing production seam

`src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs` is the existing authoritative handoff/import seam. P15 must extend/wire it rather than inventing a parallel importer.

It already:

- parses `HybridBootInfo` through the existing codec;
- requires security evidence;
- rejects misclassified physical/temporary evidence;
- obtains endpoint candidates only from `IFreshCxlBootDiscovery`;
- re-queries `ICxlDiscoveryProvider`;
- never converts BootInfo values into SingNext authority;
- returns aperture disposition only as `Absent/Released/Stale/Quarantined`.

## Actual missing production pieces

At the observed master, concrete implementations of:

- `IFreshCxlBootDiscovery`;
- `IFirmwareApertureRetirement`;

were found in tests, but not as production adapters. P15-10 must provide/wire production implementations through existing platform/runtime owners.

## Handoff sequence

1. Capsule emits bounded existing `HybridBootInfoV1` wire bytes into normal RAM.
2. Entry adapter validates pointer/alignment/length against allowed RAM and copies bytes into kernel/runtime-owned storage before reuse.
3. Existing importer validates the wire structure and evidence classification.
4. Production fresh discovery enumerates current endpoints without using BootInfo identity as the candidate source.
5. Existing `ICxlDiscoveryProvider` re-queries liveness/current `DeviceGeneration`/features.
6. The runtime uses the current authoritative provider generation semantics. The importer does not mint a generation.
7. Production retirement adapter attempts release/invalidate; uncertainty becomes `Stale` or `Quarantined`.
8. Only existing runtime CXL/Region authority paths may later create backing/uses/ownership.

## BootInfo contents

Allowed: physical/evidence facts, reset evidence, image identity/hash/generation, mapping evidence, diagnostics.

Forbidden: `OwnedRegion`, `RegionUse`, capability tokens, provider leases, provider-private handles, any flag meaning “adopt this as runtime authority”.

## Negative cases

Malformed BootInfo, unsupported required record, missing/bad security evidence, endpoint disappears, provider refusal, same numeric generation collision, BootInfo BDF/DSN/HPA points at a different current endpoint, ambiguous retirement.

All must block ownership publication.
