# CXL Type-3 Boot Backend

Owner: `SingPlus.Platform.HybridCpu.Boot`.

It provides pre-kernel boot transport and temporary mapping only. It must never create normal runtime memory ownership.

## Required bounded pipeline

1. Initialize early DMA isolation. Device DMA is denied by default.
2. Enumerate only configured PCI segments/buses/functions within explicit maxima.
3. Walk standard/extended capability chains with a visited-offset set or iteration bound. Loop => reject the device.
4. Parse CXL DVSEC structures with exact length/version checks.
5. Identify only the supported Type-3 endpoint/topology for the first profile.
6. Establish bounded CCI/mailbox operation with deadline and retry ceiling.
7. Invalidate pending mailbox/transport operations on reset/link loss.
8. Read optional LSA only if the selected device/profile requires it.
9. Discover BootVolume from primary metadata and documented fallback path.
10. Duplicate/ambiguous BootVolume candidates => fail closed.
11. Reserve/prepare one temporary HPA aperture.
12. Program root/switch/endpoint decoder chain as a transaction:
    - validate;
    - stage;
    - commit;
    - read back;
    - compare exact programmed state.
13. On failure, compensate in reverse order.
14. If compensation/readback state is ambiguous, classify mapping `Quarantined`.
15. Read persistent capacity with explicit ordering/cache policy.
16. Copy source to normal RAM with source bounds, destination bounds and overlap checks.
17. Hash the **destination** and compare with the verified manifest hash.
18. Emit BootInfo mapping evidence only; do not publish runtime authority.

## Required negative/fault cases

- malformed PCI config access;
- PCI capability loop;
- malformed DVSEC;
- unsupported Type-3 profile;
- duplicate BootVolume;
- candidate permutation;
- mailbox timeout;
- link loss;
- reset during discovery;
- decoder resource conflict;
- partial HDM commit;
- decoder readback mismatch;
- reset during mapping;
- reset during copy;
- destination hash mismatch;
- pre-IOMMU DMA attempt.

## Ordering/cache rule

The platform adapter must document and qualify the exact ordering primitives used around:

- decoder programming;
- commit/readback;
- persistent-capacity reads;
- copy completion before kernel handoff.

Model/ISE behavior does not automatically prove hardware ordering.

## Separation from runtime CXL

Boot CXL transport ends at evidence + temporary mapping lifecycle. Runtime provider discovery, generations, RegionAuthority, ownership and release occur after kernel admission in their existing runtime layers.
