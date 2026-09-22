# P15.7 — `SingPlus.Platform.HybridCpu.Boot` implementation plan

## Purpose

This project is the only pre-kernel layer that knows HybridCPU platform mechanisms and raw PCI/CXL/HDM details. It provides mechanism, not SingNext runtime authority.

## Required submodules

### Platform descriptor source

Produces validated immutable boot facts:

- ROM/BootRAM/system RAM;
- reserved ranges;
- PCI config roots;
- temporary CXL aperture;
- local capsule/recovery locations;
- reset sequence and platform identity.

### PCI configuration

Implement bounded configuration access and enumeration:

- checked BDF/function ranges;
- fixed bus/function budgets;
- bounded extended-capability traversal;
- duplicate/loop detection;
- deterministic timeout handling.

### CXL discovery

Implement protocol-only identification:

- CXL-capable device detection;
- Type-3 classification;
- mailbox/CCI transport where required;
- persistent capacity discovery;
- optional LSA locator as hint only;
- BootVolume header canonical fallback.

### Temporary decoder / HDM transaction

MVP constraints:

```text
one endpoint
one source range
one reserved HPA aperture
no interleave
one active boot mapping
```

Transaction requirements:

1. validate all ranges and reserved-memory exclusion;
2. validate every decoder hop before mutation;
3. program in defined order;
4. read back/confirm each accepted effect;
5. on partial failure, compensate in reverse order;
6. if closure is ambiguous, mark mapping quarantined and require recovery/reset policy;
7. increment mapping generation on create/destroy/reset/rebind.

### Persistent-capacity reader

Expose bounded streaming reads. It MUST not expose raw DPA/HPA as an authorization token to Boot Core.

### Protected boot state

Implement exact atomic transitions for:

- selected/confirmed image;
- trial image + nonce + attempts;
- rollback floor per rollback domain;
- capsule generation floor;
- trust epoch/key state as required by platform profile.

### Local images/recovery

Provide signed local:

- capsule A;
- capsule B or rollback capsule;
- recovery capsule/image.

Network recovery belongs inside a signed recovery environment, not the immutable root.

### Early DMA / IOMMU

Default implementation disables bus mastering. An optional profile may configure a minimal IOMMU domain before allowing an exact requester.

## Backend result discipline

Hardware-facing methods MUST NOT throw for expected device faults. They return explicit statuses and enough evidence to decide whether an external effect is known absent, known applied, or ambiguous.

## Runtime separation

`SingPlus.Platform.HybridCpu.Boot` MUST NOT reference:

- `SingPlus.Runtime`;
- `RegionAuthority`;
- `CxlAuthorityBridge`;
- SIP/process authority;
- runtime provider lease types.

`SingPlus.Platform.HybridCpu` runtime provider MUST NOT depend on the boot adapter implementation. Shared raw protocol codecs should move to a neutral small library only if both need them and they contain no authority semantics.
