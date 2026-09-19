# Phase 09 — ManagedCap Admission and Closed-World Enforcement

## Goal

Turn the existing `SingPlus.Admission` infrastructure into the final static enforcement pipeline for ManagedCap.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Preserve existing verifier

Refactor, do not replace:

```text
AdmissionVerifier
  -> ProfilePolicy (KernelNoHeap, ManagedCap, ...)
  -> MetadataScanner
  -> CIL scanner
  -> dependency/native closure
  -> static-state scanner
  -> deterministic proof/audit inputs
```

One final admission result owns static policy truth.

## Positive capability-safe framework surface

ManagedCap v1 is not secured by an ever-growing denylist alone. Define a versioned positive framework/API surface keyed to the exact SDK/runtime/policy tuple. Only explicitly classified primitives, selected numerics/collections, bounded generated value forms, approved async primitives and SingPlus contracts are referenceable.

An API/type/member introduced by a new BCL/runtime version is denied until explicitly classified. Namespace-only approval is insufficient; generic instantiations, member signatures, constructors and transitive base/interface/member types must remain within the admitted surface. The negative rules below remain defense in depth and provide precise diagnostics.

## ManagedCap v1 scan mode

Use full-module scan for forbidden metadata and bodies. Reachability from a declared root is insufficient for security because reflection/generated dispatch/dynamic metadata can expose code that is statically unreachable.

## Required denied surfaces

At minimum enforce the Technical Specification list for:

```text
P/Invoke / LibraryImport / unmanaged boundaries
calli/function pointer invocation
Unsafe memory access
NativeMemory / dangerous Marshal/MemoryMarshal/CollectionsMarshal
Reflection.Emit
unsafe non-public reflection
Assembly.Load / runtime path/byte loading
arbitrary AssemblyLoadContext
DLR/dynamic expansion where not closed
host filesystem/network/process/device APIs
undeclared native assets
arbitrary type materializing serializers
authority-bearing ambient mutable statics
```

## Static/global state policy

Do not reject every mutable static purely because it is mutable. Reject statics that can carry ambient authority, mutable cross-compartment references, host resources or untracked service/session state. A stricter deterministic profile may add broader rules separately.

## Dependency closure

- local managed dependency content digest;
- exact identity/version;
- native asset inventory;
- unknown/unresolved category => deny;
- runtime loading outside closure => deny for ManagedCap;
- policy result included in audit proof.

## Primary paths

```text
tools/SingPlus.Admission/AdmissionVerifier.cs
tools/SingPlus.Admission/Program.cs
sdk/SingPlus.Analyzers/
Directory.Build.props / targets
tests/SingPlus.Tests/Admission/
```

## PR slices

- **P09-1:** policy abstraction without weakening current KernelNoHeap tests.
- **P09-2:** full-module metadata/IL scan.
- **P09-3:** ManagedCap forbidden API/interop policy.
- **P09-4:** versioned positive capability-safe framework/member allowlist; unknown member fails closed.
- **P09-5:** static-state and deep dependency/native closure.
- **P09-6:** AOT evidence integration and compiler/runtime tuple.

## Adversarial fixtures

Include forbidden code hidden outside the root graph, module initializer, static constructor, malformed metadata if practical, local dependency containing unsafe call, hidden native dependency, reflection mutation, Assembly.Load(byte[]), calli and function pointers.

Also include a precompiled assembly referencing a harmless-looking but unclassified BCL type/member and a fixture compiled against a drifted framework surface. Both must fail closed until the exact members and toolchain tuple are admitted.

## Exit criteria

A precompiled hostile assembly cannot bypass the ManagedCap gate merely because source analyzers were not run. Newly available/unclassified framework API is denied by default, and the existing verifier remains the one static authority gate.
