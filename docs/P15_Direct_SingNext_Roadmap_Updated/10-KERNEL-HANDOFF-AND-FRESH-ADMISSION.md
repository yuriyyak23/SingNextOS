# Kernel Handoff and Fresh Admission

## Capsule responsibility

The capsule writes a bounded `HybridBootInfo` buffer in normal RAM.

Allowed contents are evidence/physical facts such as:

- image identity/hash/generation;
- BootVolume identity evidence;
- observed BDF/DSN/route where needed;
- temporary mapping evidence;
- reset reason;
- versioned lengths and flags.

Forbidden contents:

- `OwnedRegion`;
- `RegionUse`;
- runtime provider handles;
- capability tokens;
- reusable provider generations;
- implicit “trust this mapping as runtime authority” flags.

## Kernel importer

At `KernelEntryPoint` or the repository's authoritative equivalent:

1. validate `KernelEntryAbiV1`;
2. validate BootInfo pointer, alignment, length, version and checksum;
3. validate every nested offset/range with overflow-safe arithmetic;
4. copy BootInfo into kernel-owned memory;
5. reject aliasing/overlap with disallowed regions;
6. normalize evidence into kernel-owned immutable structures;
7. discard raw pointer authority assumptions.

## Fresh admission sequence

After import:

1. invoke the existing runtime CXL discovery/provider path;
2. extend it with `IFreshCxlBootDiscovery` only if no adequate authoritative hook exists;
3. revalidate endpoint liveness;
4. revalidate identity correspondence;
5. revalidate current backend/provider generation;
6. mint/create a **new** runtime provider generation through the existing owner;
7. request retirement of the boot-only aperture;
8. classify retirement as `Released`, `Stale` or `Quarantined`;
9. only after normal runtime admission may existing `RegionAuthority` create/authorize `OwnedRegion` / `RegionUse`.

## Trusted evidence reuse optimization

Fresh physical rediscovery may be replaced by trusted evidence reuse only when all of the following are proved:

- evidence source is authenticated/integrity-protected;
- liveness is revalidated;
- generation/epoch is revalidated;
- no runtime authority is inherited;
- a new runtime provider generation is created.

## Fail-closed cases

- endpoint disappears at handoff;
- identity mismatch;
- provider refusal;
- stale generation;
- ambiguous retirement;
- temporary aperture cannot be safely retired;
- malformed BootInfo.

In these cases ownership publication is blocked and mapping state is `Stale`, `Quarantined` or `ReclaimBlocked` as appropriate.
