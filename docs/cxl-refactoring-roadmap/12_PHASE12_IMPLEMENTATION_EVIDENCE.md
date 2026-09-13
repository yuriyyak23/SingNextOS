# Phase 12 Implementation Evidence

## Result

Complete for the provider-neutral SingNextOS boundary. No HybridCPU-v2, compiler/lowering, ISA, lane, GuardPlane, or replay-certificate implementation was changed.

## Stable external contracts

- `ExternalOperationContract.Version` independently versions the external lifecycle contract.
- `OperationAdmissionSnapshot` carries operation ID/generation, opaque service identity, region-use descriptors, effect class, publication policy, and cancellation support.
- `ExternalOperationReceiptStatus` and `ExternalOperationReceipts` distinguish Prepared, Admitted, Submitted, DeviceComplete, Visible, Published, Released, Failed, and Stale outcomes.
- `ExternalEffectClass` exposes the three provider-neutral effect classes required by the roadmap without prescribing HybridCPU replay representation.
- `ComputePlanningContract.Version`, `ComputeRegionCapabilityQuery`, and `ComputeRegionCapabilityReceipt` expose semantic device readability/writability, coherence, staging, visibility, and secure-compute readiness.
- Type-2 admission supplies only its opaque semantic provider identity. No raw endpoint, route, decoder, DPA/HPA, requester, PASID, or provider token enters these contracts.
- The Type-2 provider ABI carries an opaque `CxlFabricBindingRef` containing binding ID plus generation, so equal generations on two bindings of one endpoint remain distinct without exposing physical topology.
- `PlatformAuthorityStatus.NotAccepted` distinguishes a proven pre-effect rejection from ambiguous provider unavailability and is appended without renumbering existing status values.
- Effect-creating CXL provider contracts normatively reserve `NotAccepted` for proven zero-effect rejection, treat `Unavailable` as acceptance-ambiguous, and require failures to be returned rather than thrown. Runtime boundaries still convert provider exceptions into quarantine.

## Negative evidence

- Stale provider generations fail before a capability receipt is issued.
- Missing region authority produces false read/write capability even when provider capability exists.
- Secure readiness is reported unavailable without evidence capability.
- Reflection tests reject physical CXL/PCIe identity names from public external-operation and compute contracts.
- A model security provider reports `ModelOnly` assurance and cannot satisfy `RequireHardwareAttestation`.

## Dependency direction

The implementation remains:

```text
SingNextOS authority + lifecycle + provider semantics
        -> versioned provider-neutral contracts
        -> optional later HybridCPU-v2/compiler consumers
```

Superseded qualification after fabric-exactness Phase 16 on 2026-09-13 passed 869/869 tests with zero failures and zero skips after a forced no-cache restore.
