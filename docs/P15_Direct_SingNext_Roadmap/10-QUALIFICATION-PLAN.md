# P15.9 — Qualification and test plan

## Evidence classes

Use only explicit claim levels:

```text
ContractOnly
ModelValidated
AdapterQualified
IseValidated
QemuProtocolValidated
HardwareValidated
```

A higher level never follows automatically from a lower level.

## Test layers

### 1. Contract/golden-vector tests

For all fixed-width codecs:

- endian/UUID canonical form;
- CRC/hash vectors;
- truncation;
- overflow;
- duplicate required fields;
- unknown required flags/types;
- maxima/budget enforcement;
- before/after relocation byte identity.

### 2. Boot Core property tests

Properties:

- candidate permutation does not change semantic selection;
- BDF/route/DSN changes do not change logical identity unless physical pinning policy explicitly requires it;
- same generation/different image is split brain;
- rollback floor is monotonic;
- trial confirmation alone advances floor;
- failed verified load publishes no executable component;
- range overlap and arithmetic overflow always fail.

### 3. Differential model tests

Run the same generated inputs against:

```text
old Boot model oracle
vs
new SingNext.Boot.Core implementation
```

Required for selector, A/B, rollback, range checks and temporary-aperture state machine before old model code is retired.

### 4. Boot adapter tests

Inject:

- malformed PCI capability chain;
- duplicate capabilities;
- mailbox timeout;
- unsupported LSA;
- link loss;
- partial decoder commit;
- decoder readback mismatch;
- stale reset sequence;
- protected-store torn write;
- local recovery corruption;
- pre-IOMMU DMA request.

### 5. Capsule admission/TCB tests

- forbidden API fixtures;
- dependency graph allow-list;
- reachable-method budget;
- no runtime authority types referenced;
- deterministic two-build artifact digest.

### 6. ISE end-to-end positive scenario

```text
ROM surrogate
 -> signed capsule
 -> PCI/CXL Type-3
 -> boot volume
 -> temporary aperture
 -> verified kernel load
 -> BootInfo
 -> kernel entry
 -> fresh CXL admission
 -> aperture retirement
 -> normal Region-backed Type-3 use
```

### 7. ISE negative matrix

At minimum:

- capsule signature bad;
- capsule rollback rejected;
- no CXL endpoint;
- malicious enumeration order;
- BootVolume duplicate/split brain;
- manifest bad signature;
- image generation below floor;
- HDM partial failure;
- link loss during copy;
- destination hash mismatch;
- malformed BootInfo;
- endpoint disappears after kernel entry;
- aperture retirement ambiguous;
- trial attempts exhausted;
- confirmed image corrupt;
- local recovery selected.

### 8. QEMU companion lane

Use for CXL protocol realism and PCI/HDM behavior if supported. `QemuProtocolValidated` is not hardware proof.

### 9. Hardware lane

Independent evidence required for:

- reset vector and ROM execution;
- actual HybridCPU instruction execution;
- PCI config transport;
- HDM programming and teardown;
- cache/ordering requirements;
- protected monotonic store;
- IOMMU/DMA containment;
- watchdog and local recovery;
- physical CXL endpoint behavior.

## Qualification artifact

Create one canonical report, e.g. `DirectSingNextBootQualificationV1.json`, containing exact revisions/toolchain identities, each stage outcome, image digests, test evidence, and the highest justified claim per requirement.
