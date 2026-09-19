# Phase 1 — Cross-project Runtime Contract

## Goal

Define the stable HybridCPU-facing contract by which `HybridCPU_ExternalRuntime` consumes SingNextOS external-operation services without importing CXL topology or SingNextOS internal authority types into the CPU core.

## Contract shape

Introduce provider-neutral contract types in `HybridCPU_ExternalRuntime.Contracts` that mirror only stable semantics:

```text
ExternalOperationRequest
ExternalOperationAdmissionReceipt
ExternalOperationCompletionReceipt
ExternalOperationPublicationReceipt
ExternalOperationReleaseReceipt
ExternalEffectClass
ExternalVisibilityRequirement
ExternalCancellationMode
ExternalGenerationSet
```

The exact type names may change, but the semantic split must remain.

## Request semantics

A request may carry:

- operation kind / semantic provider requirements;
- opaque region handles already established through runtime integration;
- read/write/execute intent;
- staged-output requirement;
- coherent-access requirement;
- cancellation/idempotence requirements;
- owner/domain correlation needed by HybridCPU guards;
- descriptor identity/hash needed to correlate completion with the exact submitted work.

It must not carry raw CXL topology identities.

## Admission receipt

Must be opaque to HybridCPU except for stable semantic fields:

- operation ID and operation generation;
- admission accepted/rejected;
- immutable request/descriptor correlation identity;
- effect class;
- visibility requirement;
- publication policy;
- cancellation support;
- generation snapshot bundle;
- provider capability summary needed for CPU replay/barrier policy.

SingNextOS authority remains authoritative. HybridCPU must not synthesize an admission receipt locally.

## Lifecycle mapping

HybridCPU must be able to distinguish at least:

```text
Prepared
Admitted
Submitted
DeviceComplete
Visible
Published
Released
Failed/Stale
```

The CPU runtime may use fewer internal states, but it must not collapse `DeviceComplete`, `Visible`, and `Published` into one success bit.

## Generation contract

`ExternalGenerationSet` is an opaque equality/invalidation carrier. It may represent SingNextOS region mutation, mapping, device, fabric-binding, or operation generations, but HybridCPU must not inspect provider-specific components.

Required operations:

- snapshot at admission;
- compare/revalidate before submit when required;
- compare/revalidate before publication;
- mark stale on asynchronous reset/reconfiguration notification;
- attach generation identity to telemetry and replay decisions.

## Versioning

Add an explicit contract version independent of CXL 3.x/4.x. Provider capability records may grow compatibly without changing instruction encoding.

## Negative rules

Reject designs where:

- HybridCPU fabricates OS authority from compiler metadata;
- a CXL link-up/device-discovered bit is treated as operation admission;
- completion lacks descriptor/operation correlation;
- a generation mismatch can still reach publication;
- SingNextOS-specific C# implementation types leak into `HybridCPU_ISE`.

## Tests

Add contract serialization/equality tests, stale receipt tests, wrong-operation completion tests, duplicate completion tests, and version compatibility tests.

## Exit criteria

A fake/in-memory SingNextOS adapter can drive the full lifecycle through `HybridCPU_ExternalRuntime.Tests` without any CXL-specific identity appearing above the adapter boundary.