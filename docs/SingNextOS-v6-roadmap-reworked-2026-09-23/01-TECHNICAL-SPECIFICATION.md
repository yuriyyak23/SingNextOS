# SingNextOS v6 — Corrected Technical Specification

**Normative baseline:** SingNextOS `690913e3500956aeb18d9823b35e9e64d40e42d7`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`; 2026-09-23.

## 1. Architectural objective

v6 extends semantic expressiveness for memory, translation, time, failure, trust, locality, heterogeneous execution, durability and optional distributed delegation without changing the fundamental authority topology.

The architecture SHALL remain:

```text
capability-native
+ ownership-oriented
+ resource-conserving
+ provider-neutral
+ contract-driven
+ heterogeneous
+ machine-checkable
+ fail-closed
```

## 2. Non-negotiable ownership rule

Each mutable logical fact SHALL have exactly one authoritative owner. Evidence, correlation objects, descriptors and projections SHALL NOT acquire mutation authority merely because they summarize multiple owners.

The implementation SHALL reuse the live owners:

```text
CapabilityAuthority
RegionAuthority
ResourceBudgetAuthority
ProcessRegistry / process incarnation owner
EndpointSessionRegistry
invocation lifecycle owners
SealedObjectAuthority
ExternalOperationAuthority
publication / response owners
PlatformAuthorityBridge and concrete provider adapters
HybridCPU IRuntimeLegalityService / GuardPlane
provider-local admission/runtime
```

A new owner is permitted only after an ADR demonstrates that no existing owner can represent the fact without violating its current invariant set.

## 3. Independent gates

For any heterogeneous effect, the following gates remain logically independent:

```text
AuthorizedBySingNext
ProviderAdmission
GuaranteesRefineObligations
HybridCpuRuntimeLegal
```

No compiler proof, attestation result, mapping handle, completion event, topology identifier, scheduler selection, reservation, cache state, replay certificate or provider receipt may substitute for any missing gate.

## 4. Contract evolution strategy

The default implementation strategy is **V1 + additive versioned sidecars**, not a blanket V2 rewrite.

The existing `OperationObligationsV1`, `ExecutionGuaranteesV1` and `SemanticExecutionBindingV1` remain the stable base. New semantic dimensions SHALL be carried in dedicated, canonical, digest-bound extension records correlated with the V1 operation/binding.

A new V2 family is justified only if at least one of the following is proven:

- a required semantic cannot be represented by an additive sidecar without ambiguity;
- canonicalization of old and new forms cannot be made injective;
- the base record contains a semantic whose meaning must change, not merely extend;
- downgrade rules cannot be expressed safely while retaining V1 wire compatibility.

Until then, `OperationObligationsV2`, `ExecutionGuaranteesV2`, and `SemanticExecutionBindingV2` are `DEFERRED`.

Unknown mandatory extension classes SHALL fail closed. Unknown optional classes MAY be ignored only when the operation contract explicitly declares that absence preserves the requested semantics.

## 5. Generation discipline

Every binding that depends on mutable owners SHALL carry or be reconstructibly bound to the exact relevant generations/epochs. At minimum, depending on contour:

```text
process/session/invocation generation
RegionUse / MutationEpoch
budget lease generation
provider manifest/session generation
external operation generation
address-space incarnation
translation/IOMMU mapping generation
device lease/reset generation
trust/measurement generation
secure/virtual domain generation
resume/capture generation
remote lease epoch
```

A stale generation is never "repaired" by treating an old receipt as authority. The operation is denied, quarantined, restarted, or freshly re-admitted according to the phase contract.

## 6. Memory semantic decomposition

v6 SHALL model separately:

```text
ownership
access
ordering
atomicity
coherence
visibility
publication
persistence
```

The following implications are forbidden:

```text
mapping -> ownership
coherence -> ownership
ordering -> ownership
completion -> visibility
visibility -> publication
publication -> persistence/durability
atomicity -> authority
```

Shared mutable/atomic contours remain disabled until Region use mode, alias rules, provider guarantees, HybridCPU legality and refinement are all executable for the exact contour.

## 7. Translation and DMA

DMA legality SHALL be an exact composition of independent facts:

```text
RegionUse
+ AddressSpaceGeneration
+ DeviceLeaseGeneration
+ TranslationGeneration
+ ProviderAdmission
+ current operation/session generations
```

A non-authoritative correlation object such as `DmaExecutionBindingV1` MAY be introduced. It may name generations and provider operation correlation IDs, but it owns no mapping and grants no access.

VA, IOVA, PASID, IOMMU domain IDs and physical addresses are never capability substitutes.

## 8. Temporal and resource semantics

Temporal semantics SHALL extend the existing resource algebra and `ResourceBudgetAuthority`. There is no `TemporalAuthority`.

The contracts SHALL distinguish:

```text
requested budget
reserved capacity
enforced upper bound
provider-local admission
minimum service
period/deadline
WCET assumption
global schedulability result
```

`reservation != guarantee`. Hard-real-time and WCET claims are `UNSUPPORTED` until a named provider/runtime contour supplies executable enforcement and schedulability evidence.

## 9. Failure, cancellation and preemption

The lifecycle vocabulary is normative and non-interchangeable:

```text
cancel
preempt
drain
capture
suspend
resume
restart
contain
close
quarantine
```

`cancel != proof of no effect`; `preempt != cancel`; `suspend != resumable`; `provider loss != closure`.

Already possible external effects must be reconciled or quarantined before Region reclaim, publication rollback or budget settlement can claim closure.

## 10. Durability

The ordered semantic chain is:

```text
Complete -> Visible -> Published -> Persisted -> Durable
```

Each arrow requires an explicit owner/guarantee/evidence rule. Persistent media does not preserve ephemeral authority. Reboot creates fresh process/session/provider/Region admission state.

## 11. Trust and SecureCompute

Attestation and measurement are predicates/evidence. They SHALL NOT become capabilities, provider leases, Region handles or runtime legality decisions. VirtualDomain and SecureDomain remain independent domains; neither is promoted to a universal authority root.

## 12. IFC

IFC is optional and starts only on protected contours. `DataLabel != capability`. Declassification and endorsement require existing authority to authorize the effect; labels themselves do not grant it.

## 13. Locality and topology

Provider topology is evidence/private implementation detail. Planner decisions are policy. Physical topology IDs SHALL NOT appear in application/capability authority ABI.

## 14. Energy and thermal semantics

Energy/power dimensions MAY extend `ResourceBudgetAuthority` where they represent committed quantitative budgets. Provider telemetry remains measurement evidence. Thermal/DVFS state remains provider/runtime state. No `PowerAuthority` is introduced.

## 15. Multi-host

Multi-host delegation uses:

```text
single logical owner
+ epoch-bound monotonic delegated lease
+ optional resource escrow/sub-allocation
```

A distributed capability database is a non-goal. Consensus is required only for facts that truly need a single distributed linearization point; leases and ownership-transfer protocols are preferred otherwise.

## 16. Compiler evidence / Proof-Carrying Lowering

Compiler evidence may describe bounded immutable facts such as:

```text
read/write footprint
alias/disjointness facts
ordering preservation
numeric/lowering mode
safe-point map
bounded static resource estimates
binary/bundle digest
```

Proof validation can accelerate repeated structural checks but cannot replace live authority, provider admission or HybridCPU runtime legality.

## 17. ISA policy

All phases default to `ISA impact = NONE`. Runtime-only, sideband-contract or compiler-contract changes are preferred. A real ISA extension requires a separate ADR proving an enforcement gap that cannot be closed by current runtime legality, sideband metadata, provider contracts, fences, generation checks, ExternalRuntime or compiler/runtime contracts.

At this freeze, no such gap is proven.

## 18. Claim discipline

Allowed claim levels are independent:

```text
ModelOnly
StaticAdmission
RuntimeEnforced
ExecutableAdapter
EnforcedUpperBound
GuaranteedReservation
ProductionQualified
FutureGated
```

A claim SHALL name its exact source/package/toolchain/provider/hardware tuple and the executable evidence that supports it.
