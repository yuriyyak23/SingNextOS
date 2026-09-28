# Qualification Plan

## Evidence lanes

`ContractOnly` -> `ModelValidated` -> `AdapterQualified` -> `IseValidated` and independently `QemuProtocolValidated` -> `HardwareValidated`.

A higher lane requires direct evidence from that environment; evidence is never promoted by wording.

## Mandatory fault matrix

Every row gets a stable test ID, owner, environment and expected terminal state:

1. corrupt capsule;
2. capsule rollback;
3. corrupt boot manifest;
4. image rollback;
5. duplicate BootVolume;
6. split brain;
7. candidate permutation;
8. link loss;
9. mailbox timeout;
10. PCI capability loop;
11. partial HDM commit;
12. decoder readback mismatch;
13. reset during mapping;
14. reset during copy;
15. destination hash mismatch;
16. malformed BootInfo;
17. endpoint disappearing at handoff;
18. provider refusal;
19. ambiguous aperture retirement;
20. protected-state torn write;
21. trial-attempt exhaustion;
22. confirmed-image failure;
23. local recovery failure;
24. pre-IOMMU DMA attempt;
25. late completion from old reset epoch.

## Additional mandatory properties

- BootInfo fields cannot create `OwnedRegion`, `RegionUse`, capability, provider lease or candidate endpoint;
- same numeric BDF/DSN/HPA/generation after reset does not imply continuity;
- allocation profile is qualified for the final capsule root;
- allocation-safe BootInfo writer is byte-for-byte compatible with existing V1 codec vectors;
- production fresh discovery and retirement adapters have negative-path coverage, not only test fakes;
- external-gate absence is fail closed;
- retained models and production semantics have differential coverage.

## Qualification artifacts

`DirectSingNextBootQualificationV1.json` must record:

- SingNextOS SHA;
- HybridCPU **qualified** SHA and separately observed current SHA;
- compiler contract version;
- capsule memory/admission profile;
- test IDs/results/environment;
- artifact hashes;
- external gate states;
- maximum justified claim;
- blocked-promotion reason.

Pin drift without a new successful qualification must produce a blocked result, not a rewritten “pass”.
