# SingNextOS v6 — Normative Technical Specification

**Status:** normative design specification for implementation planning.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`; 2026-09-23.  
**Keywords:** MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are normative.

## 1. Purpose

SingNextOS v6 extends the live semantic co-design architecture from generic operation obligations and execution guarantees to explicit **memory, persistence, time, translation, information-flow, trust, failure and energy semantics** while preserving the existing one-fact/one-owner authority model.

v6 SHALL reuse the current production owners and live semantic surfaces, including:

```text
CapabilityAuthority
RegionAuthority
ResourceBudgetAuthority
EndpointSessionRegistry / invocation owners
SealedObjectAuthority
ExternalOperationAuthority
publication/response owners
PlatformAuthorityBridge / provider adapters
OperationObligationsV1
ExecutionGuaranteesV1
SemanticExecutionBindingV1
AdmissionVerifier / SingPlusAdmissionProofV1
SipJob plan/verifier/barrier machinery
HybridCPU ExternalRuntime admission/publication contracts
HybridCPU IRuntimeLegalityService / LegalityDecision / GuardPlane
```

No phase may introduce a parallel capability ledger, Region ledger, budget ledger, publication truth source, runtime-legality authority, or provider-independent global hardware-state owner.

## 2. v6 semantic contract

The target contract is:

```text
OperationObligationsV2
    = existing semantic/effect obligations
    + MemoryObligations
    + DurabilityObligations
    + TemporalObligations
    + Translation/DMA requirements
    + InformationFlow requirements
    + Trust requirements
    + Failure/RAS requirements
    + Energy/thermal constraints

ExecutionGuaranteesV2
    = existing provider/runtime guarantees
    + MemoryExecutionGuarantees
    + DurabilityGuarantees
    + TemporalExecutionGuarantees
    + Translation/DMA guarantees
    + Isolation/trust evidence class
    + Failure containment/recovery guarantees
    + Energy/performance envelope
```

Eligibility requires typed refinement plus all existing live-authority and runtime-legality gates:

```text
Eligible =
    SingAuthorityCommitted
AND ResourceReservationLive
AND RegionUsesLive
AND ExactGenerationsLive
AND ProviderAdmissionAllowed
AND GuaranteesRefineObligations
AND HybridCpuRuntimeLegal
```

A refinement result is not authority. It is a deterministic semantic check over immutable snapshots/evidence correlated to a live admission attempt.

## 3. Memory semantic model

v6 MUST distinguish at least:

```text
Ownership      : who may mutate/use bytes
Access mode    : read / write / atomic / staged-output
Ordering       : relaxed / acquire / release / acq_rel / sequential
Atomicity      : none / scalar-width / provider-declared bounded unit
Coherence      : none / explicit-sync / coherent-domain
Visibility     : CPU / device / guest / system publication prerequisite
Persistence    : volatile / persist-required
```

No `Coherent=true` bit may imply ownership, alias exclusion, publication, atomicity or persistence.

Shared mutable/atomic access MAY be enabled only when `RegionAuthority` has an explicit qualified use mode and the provider/runtime supplies a refining memory guarantee.

## 4. Durability model

v6 MUST add a distinct durable-state chain:

```text
Submitted -> DeviceComplete -> Visible -> Published -> Persisted -> Durable/Confirmed
```

A provider that cannot prove persistence returns a weaker guarantee; SingNextOS MUST NOT infer durability from completion, coherence, cache flush invocation, transport acknowledgement, or persistent-media placement alone.

Durable data MUST NOT serialize live capability authority. Restore/reboot uses durable identity + policy + integrity evidence followed by fresh admission and fresh generations.

## 5. Temporal model

Temporal semantics SHALL build on `ResourceBudgetAuthority`; there is no `TemporalAuthority`.

A temporal request may include:

```text
budget
period/window
deadline
max burst
max non-preemptible interval
interference class
priority/donation ceiling
```

A reservation is not a guarantee. A `GuaranteedReservation` claim requires provider/runtime enforcement evidence for the exact contour. Hard-real-time/WCET claims remain off unless independently qualified.

## 6. Translation/DMA model

v6 SHALL model device memory access as exact composition of existing owners rather than raw pointer authority:

```text
RegionUse
+ Process/AddressSpaceGeneration
+ DeviceLease
+ Mapping/TranslationGeneration
+ provider admission
= DmaExecutionBinding (non-authoritative correlation object)
```

Virtual address, IOVA, PASID/process ID, IOMMU domain ID and page-table identity are never application authority. Stale translation or mapping generations fail closed before the next device effect or before publication/release if the effect may already have happened.

## 7. IFC model

IFC is additive policy over existing capabilities. A data label is not authority. A label constrains legal flows; explicit declassification/relabel operations require existing capability/effect authority plus a dedicated policy capability/permission if the chosen contour needs one.

Unknown labels, producers, transforms or declassification policy versions fail closed for protected flows.

## 8. Locality model

Topology identities remain provider-private. SingNext-facing planning uses semantic locality/cost evidence only:

```text
latency class/range
bandwidth class/range
copy/migration cost
contention class
NUMA/fabric proximity class
energy cost
```

Locality evidence influences planning; it never authorizes memory use, device access or execution.

## 9. Preemption/resume model

Providers MAY expose:

```text
NonPreemptible
RestartOnly
SafePointPreemptible
StateCapturePreemptible
```

with bounded or unbounded latency classes. Captured provider state is not a SingNext capability and cannot be resumed without fresh authority/generation/provider checks.

## 10. Trust and attestation model

Device/platform measurements and SPDM/TDISP/IDE-like evidence remain evidence. Trust requirements become predicates in `OperationObligationsV2`; provider guarantees identify producer class, assurance level, measurement/policy generation and freshness.

No attestation token may be accepted as a capability, Region handle, provider lease, session token or runtime-legality verdict.

## 11. RAS model

Provider health MUST support partial/degraded states rather than only available/unavailable. v6 SHOULD model:

```text
Healthy
Degraded
PartiallyUnavailable
PoisonedRange
Draining
Contained
Reconfigured
Closed
Quarantined
```

`RegionAuthority` remains the memory owner. RAS evidence may cause region subranges to be denied/quarantined/remapped, but provider evidence cannot mutate ownership without an explicit SingNext transition.

## 12. Multi-host model

v6 MUST NOT begin with distributed consensus around every authority lookup. The preferred model is one logical owner plus epoch-bound delegated leases/escrow where required.

A remote lease:

- is strictly narrower than the parent authority;
- has exact owner/realm/epoch/generation;
- expires or becomes unusable when renewal/closure cannot be proven;
- cannot be resurrected from stale durable state;
- does not make fabric routing identity an authority token.

## 13. Energy/thermal model

Energy/power/thermal values are resource dimensions in `ResourceBudgetAuthority` or provider guarantee evidence as appropriate; they are not capabilities.

Planning MAY consider:

```text
energy budget
average/peak power envelope
thermal class/headroom
performance-state envelope
energy-per-operation evidence
```

No performance or energy claim is promoted from telemetry alone without a qualified measurement/enforcement contract.

## 14. Proof-Carrying Lowering

Compiler output MAY carry a `CompilerSemanticProofV1`/certificate containing non-authoritative claims such as read/write footprint, alias facts, ordering requirements, numeric semantics, lowering class and resource estimates.

The runtime MUST validate the proof against exact compiler/toolchain/schema identities and current operation obligations. Invalid, stale, unknown or incomplete proof data is rejected or ignored according to the declared optional/mandatory class. It never bypasses `IRuntimeLegalityService`, provider admission or SingNext authority revalidation.

## 15. Formal refinement requirement

For each qualified contour there SHOULD exist a projection relation:

```text
project(provider/HybridCPU trace) -> SingNext semantic trace
```

and executable evidence that every accepted lower-level trace maps to an allowed upper-level trace for the finite qualified state space/scenario set. TLA+/PlusCal, Alloy/property exploration and differential trace testing are preferred before considering interactive theorem proving.

## 16. Claim vocabulary

v6 uses the existing disciplined levels and extends them only when necessary:

```text
ModelOnly
StaticAdmission
RuntimeEnforced
ExecutableAdapter
EnforcedUpperBound
GuaranteedReservation
HardwareValidated
ProductionQualified
```

No level implies the next.

## 17. ISA policy

Default v6 rule: **ISA impact NONE**.

HybridCPU ISE/runtime/compiler changes SHOULD remain inside existing legality, typed-slot, execution, measurement, fence, replay, retire and external-operation seams. New instructions are permitted only if a later evidence-backed phase proves that an essential semantic cannot be expressed or enforced using the existing machine model. Such a change requires a separate ISA ADR and is outside automatic v6 scope.
