# P01 — Authority algebra and semantic resource taxonomy

## Goal

Define the semantics before introducing handles. Extend the SingCap-M monotonic constraint algebra to **resource/time authority** without merging it with effect capabilities.

## Core distinction

```text
EffectCapability      = may perform semantic effect
RegionAuthority       = may read/write/own exact data
ResourceCapability    = may consume bounded resource envelope
ProviderAdmission     = platform accepts exact execution
```

All four are independently required where applicable.

## Resource classes v1

Keep the initial set deliberately small and provider-neutral:

```text
GeneralCpuTime
ManagedRuntimeTime
ComputeTime
MatrixComputeTime
VectorComputeTime
MemoryBandwidthBytes
DmaBandwidthBytes
FabricBandwidthBytes
DeviceMemoryBytes
ConcurrentOperations
```

Do **not** expose HybridCPU lanes or cycles as the common unit. Resource-specific canonical units:

- time: nanoseconds + period/window;
- throughput: bytes per window;
- occupancy: count/bytes for a lifetime;
- concurrency: maximum active instances.

Energy remains FutureGated.

## Constraint algebra

Every resource constraint family implements:

```text
Canonicalize(x)
IsSubset(child, parent)
SerializeCanonical(x)
Validate(x)
```

Required monotonic relations:

```text
child.amount <= parent.amount
child.validity subset parent.validity
child.resourceClass subset parent.resourceClass
child.providerSet subset parent.providerSet
child.assurance <= parent.assurance
child.maxConcurrency <= parent.maxConcurrency
```

Sibling union is forbidden unless a privileged owner holds the union authority.

## Assurance taxonomy

```text
AccountingOnly
EnforcedUpperBound
GuaranteedReservation
```

`GuaranteedReservation` is not implementable in this phase; define semantics only.

## CHERI-derived principles allowed here

Only software principles are adopted:

- monotonic narrowing;
- provenance-preserving derivation;
- explicit sealing/opaque IDs;
- fail-closed unknown constraint kinds;
- checked arithmetic.

No tagged pointer/memory semantics.

## Tests

- property tests for transitivity/reflexivity of `IsSubset`;
- overflow/zero/wrap tests;
- sibling-union amplification negatives;
- provider-set narrowing tests;
- unknown enum/version fail-closed.

## Exit criteria

A versioned pure contract/model exists with no runtime minting and no provider integration.
