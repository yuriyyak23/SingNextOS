# Boot Capsule Security Profile

## Decision

Create a dedicated `BootCapsule` admission profile by extending the repository's existing Admission/SingCap security-profile mechanism.

Do **not** create a second analyzer framework.

A separate `BootCapsuleNoHeap` profile is justified only if the existing `KernelNoHeap` machinery cannot express the capsule's constraints without weakening kernel policy.

## Security principle

```text
NativeAOT = evidence / attack-surface reduction
NativeAOT != authority
NativeAOT != isolation
```

The security boundary is the capability/authority model plus admitted code closure, not the AOT compiler mode.

## Allowed dependency closure

The final capsule executable may reference only:

- `HybridCpu.Boot.Contracts`;
- `SingNext.Boot.Core`;
- `SingPlus.Platform.HybridCpu.Boot`;
- explicitly approved low-level primitives already admitted by repository policy.

## Deny by default

Reject at static admission:

- `SingPlus.Runtime` shortcuts;
- direct `RegionAuthority` or capability-minting implementation dependencies;
- Host implementation;
- ExecutableAdapter models;
- reflection/dynamic code loading;
- runtime assembly loading;
- process/thread creation;
- environment/filesystem/network APIs;
- arbitrary `DllImport`/PInvoke;
- service locator/global mutable authority;
- culture-dependent parsing in boot-critical paths;
- unbounded collection growth;
- unsupported packages.

## Heap/GC decision

The roadmap does not predeclare “GC forbidden” unless current runtime/toolchain evidence supports it.

`P15-09` must establish one of:

1. a proven no-heap closure using existing admission rules + NativeAOT evidence; or
2. a bounded allocator/runtime profile with a quantified maximum live-memory budget and fail-closed OOM semantics.

Until one is demonstrated, the capsule may be `AdapterQualified` but not promoted to production hardware readiness.

## Required negative fixtures

CI must contain fixtures proving rejection of:

- forbidden runtime reference;
- direct capability minting;
- direct `RegionAuthority` use;
- ExecutableAdapter model reference;
- Host dependency;
- reflection/dynamic load;
- unauthorized PInvoke;
- unbounded allocation path;
- forbidden package;
- non-deterministic wall-clock trust.

## Static admission root

The admission root is the final `SingNext.Boot.Capsule` executable closure, not only `SingNext.Boot.Core`.
