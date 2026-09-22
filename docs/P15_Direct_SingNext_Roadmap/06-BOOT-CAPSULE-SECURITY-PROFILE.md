# P15.5 — Boot Capsule security and TCB profile

## Goal

The Direct SingNext capsule is privileged OS code executing before the normal capability graph exists. It therefore needs a stricter profile than ordinary managed services and a different profile from the current host-debug `SingPlus.Boot` application.

## Proposed build/admission properties

```xml
<SingPlusProfile>BootCapsule</SingPlusProfile>
<SingPlusMemoryProfile>BootCapsuleNoHeap</SingPlusMemoryProfile>
<SingPlusAdmissionRoot>SingNext.Boot.Capsule.CapsuleEntryPoint::Run</SingPlusAdmissionRoot>
<Deterministic>true</Deterministic>
```

If a bounded managed heap is eventually required, add a separate explicit profile such as `BootCapsuleBoundedHeap`; do not silently broaden `BootCapsuleNoHeap`.

## Allowed dependency classes

- `HybridCpu.Boot.Contracts`;
- `SingNext.Boot.Core`;
- `SingPlus.Platform.HybridCpu.Boot`;
- approved deterministic crypto primitives;
- minimal span/buffer/numeric primitives;
- compiler/runtime support required by the exact HybridCPU managed ABI.

## Forbidden by default

- reflection and dynamic assembly loading;
- host filesystem APIs;
- arbitrary environment variables;
- process creation;
- networking/TLS in baseline capsule;
- thread pool / background tasks;
- timers not supplied by boot platform contract;
- arbitrary P/Invoke;
- runtime CXL provider objects;
- `RegionAuthority`, `CapabilityAuthority`, `ProcessHandle`, SIP lifecycle;
- generic DMA enablement;
- implicit fallback from ambiguous external effect to success.

## Memory discipline

Preferred MVP:

```text
stack + fixed scratch + explicit boot RAM ranges
```

All parser and loader allocations must have static or policy-defined maxima. BootInfo size, manifest count, component count, extent count and PCI discovery budget already have bounded contract values and should remain enforceable by static/dynamic tests.

## Crypto profile

Model HMAC verification remains test-only. Production capsule/ROM profile MUST support only a small, fixed allow-list of algorithms already represented by the boot ABI, e.g. Ed25519 and/or ECDSA-P384 for the selected platform profile.

Crypto implementation requirements:

- explicit key IDs;
- trust epoch;
- key revocation state;
- no algorithm negotiation from attacker-controlled strings;
- known-answer tests;
- malformed-signature corpus;
- constant-time compare and primitive use where applicable.

## Early DMA rule

Before full OS IOMMU/device authority:

```text
PCI bus mastering = disabled by default
```

If a platform requires pre-kernel DMA, it MUST be a separate profile with:

- one or more exact requester IDs;
- bounded bounce buffer;
- explicit IOMMU mapping;
- no protected-state / kernel-image / BootInfo write access;
- negative tests showing unlisted requesters fail.

## Capsule TCB budget

Track at least:

- executable + readonly bytes;
- reachable methods;
- dependency count/digest;
- allowed native/runtime imports;
- parser state-space limits;
- maximum scratch RAM.

A size regression beyond the agreed TCB budget is a review gate, not an informational metric.
