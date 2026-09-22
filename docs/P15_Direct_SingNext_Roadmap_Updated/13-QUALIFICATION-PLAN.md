# Qualification Plan

## Evidence lanes

1. **Contract lane** — ABI layout, serialization, project policy.
2. **Model lane** — deterministic Core state machines, property tests, model/oracle differential tests.
3. **Adapter lane** — executable SingNext-owned adapters with deterministic fake transports/protocol simulator.
4. **ISE lane** — complete supported HybridCPU ISE Direct SingNext path.
5. **QEMU protocol lane** — optional PCI/CXL protocol/fault coverage when QEMU models the required behavior.
6. **Hardware lane** — direct evidence for the exact named hardware/profile.

No lane may silently promote evidence from another environment.

## Mandatory negative/fault scenarios

Every scenario must have a stable test ID, expected terminal state and maximum evidence lane:

- corrupt capsule;
- capsule rollback;
- corrupt boot manifest;
- image rollback;
- duplicate BootVolume;
- split brain;
- candidate permutation;
- link loss;
- mailbox timeout;
- PCI capability loop;
- partial HDM commit;
- decoder readback mismatch;
- reset during mapping;
- reset during copy;
- destination hash mismatch;
- malformed BootInfo;
- endpoint disappearing at handoff;
- provider refusal;
- ambiguous aperture retirement;
- protected-state torn write;
- trial-attempt exhaustion;
- confirmed-image failure;
- local recovery failure;
- pre-IOMMU DMA attempt;
- late completion from old reset epoch.

## Additional required properties

- candidate-selection permutation invariance where ordering is not semantically meaningful;
- fuzzing for all externally controlled lengths/counts/offsets;
- differential vectors against retained adapter models;
- generation non-inheritance tests;
- same numerical mapping after reset does not regain authority;
- fail-closed behavior on unknown external feature capability.

## Qualification artifact

`DirectSingNextBootQualificationV1.json` records:

- SingNextOS SHA;
- HybridCPU external qualified baseline/pins;
- evidence lane;
- platform/profile;
- test IDs/results;
- artifact hashes;
- external gates and their status;
- maximum justified claim;
- reason for any blocked promotion.
