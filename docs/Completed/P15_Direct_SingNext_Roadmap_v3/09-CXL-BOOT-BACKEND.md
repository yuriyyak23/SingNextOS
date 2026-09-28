# CXL Type-3 Boot Backend

Owner: `SingPlus.Platform.HybridCpu.Boot`.

The boot backend is transport + temporary mapping. It never owns runtime memory or capabilities.

## Required bounded pipeline

1. Early DMA isolation is active before enabling an untrusted device path.
2. Enumerate configured PCI segments/buses/functions within hard maxima.
3. Capability walks are bounded and loop-detecting.
4. Parse CXL DVSEC with exact version/length/range validation.
5. Accept only the first supported Type-3 topology/profile.
6. CCI/mailbox operations have deadlines, bounded retries, and reset/link-loss invalidation.
7. Optional LSA is read only when the selected supported profile requires it.
8. BootVolume discovery has one primary path plus explicitly specified fallback; ambiguity fails closed.
9. Reserve one temporary HPA aperture.
10. Program decoder chain transactionally: validate -> stage -> commit -> readback -> exact compare.
11. Failure compensates in reverse order; uncertain compensation => `Quarantined`.
12. Persistent-capacity reads use documented cache/order primitives.
13. Copy to normal RAM with source/destination bounds and overlap exclusion.
14. Hash the destination and compare to authenticated manifest hash.
15. Emit evidence only.

## Mandatory faults

Malformed config, PCI capability loop, malformed DVSEC, unsupported profile, duplicate BootVolume, candidate permutation, mailbox timeout, link loss, reset during discovery/mapping/copy, decoder conflict, partial commit, readback mismatch, destination hash mismatch, pre-IOMMU DMA attempt.

## Separation from runtime CXL

Do not call `CxlAuthorityBridge`, `CxlType3MemoryAuthority`, or `RegionAuthority` from the capsule/backend. Those owners are used only after kernel/runtime admission.

## Hardware evidence boundary

Adapter/model/ISE success does not prove hardware ordering, decoder side effects, IOMMU effectiveness, or persistent-memory visibility. Those remain P15-14 gates.
