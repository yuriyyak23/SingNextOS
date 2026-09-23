# Boot Capsule Security Profile

## Existing mechanisms

Current repository evidence includes:

- `SingPlus.Admission/AdmissionVerifier` with `KernelNoHeap` and `ManagedCap` policies;
- `SingPlusAnalyzer` no-heap diagnostics;
- `eng/singcap-security-profiles-v1.json` project inventory;
- `KernelEntryPoint::Run` admitted under `KernelNoHeap`;
- existing `SingPlus.Boot` is `ManagedGc` host-debug and therefore cannot serve as the capsule security boundary.

## Decision

Extend the existing AdmissionVerifier/profile infrastructure. Do not create a second verifier/analyzer framework.

Create a dedicated `BootCapsule` policy only because the capsule has a distinct dependency allowlist and pre-kernel API contour. Implement it by composing/reusing existing no-heap/forbidden-API machinery, not by cloning it.

A separate `BootCapsuleNoHeap` name is unnecessary unless the verifier requires distinct policy identifiers for a proven no-heap closure.

## Memory policy must be decided by evidence

Two legal profiles:

1. **No-heap capsule** — no reachable managed allocation; BootInfo construction uses fixed/bounded buffers/Span; no GC dependency.
2. **Bounded managed capsule** — only if the selected HybridCPU bootstrap explicitly supports the required GC/type/runtime services and P15 proves a hard live-memory budget plus fail-closed OOM behavior.

No plan text may state “GC forbidden” or “GC allowed” before P15-08 records the selected profile and qualification evidence.

## Important existing-code consequence

`HybridBootInfoCodec.Encode` currently allocates arrays/collections and uses LINQ. Therefore a no-heap capsule must not call it directly. Add a bounded writer that emits the **same** `HybridBootInfoV1` bytes and prove equivalence using golden vectors. Runtime parsing may continue using the existing codec.

## Dependency allowlist

Allowed capsule closure:

- existing `HybridCpu.Boot.Contracts`;
- `SingNext.Boot.Core`;
- `SingPlus.Platform.HybridCpu.Boot`;
- explicitly reviewed framework primitives required by the chosen memory profile.

Explicitly forbidden: `SingPlus.Runtime`, `RegionAuthority`, `CapabilityAuthority`, Host HAL/debug boot, ExecutableAdapter models, reflection/dynamic load, filesystem/network/process/thread creation, ambient service locator, arbitrary P/Invoke, unbounded collection growth.

## Required negative fixtures

- direct Runtime reference;
- direct RegionAuthority/capability authority use;
- ExecutableAdapter model reference;
- Host HAL/debug dependency;
- reflection/dynamic assembly load;
- unauthorized P/Invoke;
- forbidden package;
- unbounded allocation path;
- wall-clock trust;
- direct use of an allocation-heavy codec in a selected no-heap root.

## NativeAOT rule

```text
NativeAOT = build/attack-surface evidence
NativeAOT != authority
NativeAOT != isolation
```
