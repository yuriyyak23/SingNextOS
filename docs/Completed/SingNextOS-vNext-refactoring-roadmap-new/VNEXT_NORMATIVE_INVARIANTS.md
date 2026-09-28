# SingNextOS vNext normative invariants — corrected

Keywords **MUST**, **MUST NOT**, **SHOULD**, **MAY** are normative.

## VNX-001 — No second capability or resource ledger

vNext MUST reuse the existing `CapabilityAuthority` and `ResourceBudgetAuthority`. It MUST NOT introduce an independent `TemporalResourceAuthority`, duplicated capability ledger, duplicated budget ledger, or replicated publication truth.

## VNX-002 — Accounting is not permission

Budget accounts, reservations, snapshots, pressure metrics and configured limits MUST NOT by themselves authorize semantic effects or resource use. Public constructible DTOs are evidence/descriptors only.

## VNX-003 — Resource-use permission is a capability constraint, not a new universe

Permission to consume a resource class is represented by a narrow grant in the existing `CapabilityAuthority` constraint algebra. It MUST be independent from the effect capability and MUST NOT authorize DMA/network/compute merely because budget exists.

## VNX-004 — Resource conservation has one quantitative owner

`ResourceBudgetAuthority` is the only local source of truth for admitted limits, reserved amount, charged/consumed amount, settlement and release. No cache, planner, session, SipJob or provider receipt may duplicate this truth.

## VNX-005 — Monotonic derivation

For every capability derivation:

```text
Authority(child) subset-of Authority(parent)
```

Resource-use constraints include class, maximum envelope, validity, subject, provider semantic scope if any, assurance ceiling and delegation depth. Unknown constraint versions fail closed.

## VNX-006 — No sibling-union amplification

Two derived grants MUST NOT be recombined to exceed the parent. Capability union is not permitted unless the existing authoritative parent still owns the union and a privileged operation explicitly derives it.

## VNX-007 — No double spend

One exact budget reservation/lease generation MUST have a single linearization winner for bind, consume, settle and release. Duplicate structs/receipts do not duplicate capacity.

## VNX-008 — Exact generations everywhere

Capability realm, subject/process generation, session/invocation generation, budget reservation generation, Region generations, ExternalOperation generation and provider generation MUST be matched at their respective boundaries. Generation mismatch is stale/fail-closed.

## VNX-009 — Donation cannot amplify or launder

Request-scoped donation MUST preserve provenance and may only narrow resource class, amount, validity, assurance, priority ceiling, provider semantic scope and delegation depth. It MUST NOT convert server-owned capacity into caller-attributed capacity or vice versa silently.

## VNX-010 — No double charge / no double refund

Every reservation has one charging lineage and one terminal settlement. Nested calls and retries MUST NOT charge both parent and child for the same committed quantity unless the contract explicitly models distinct consumption. Refund/release is idempotent.

## VNX-011 — Evidence never becomes authority

Provider receipts, certificates, completion records, telemetry, planner output, caches and manifest admission results are evidence. Only exact authoritative owner transitions change local authority/accounting state.

## VNX-012 — Provider ambiguity fails closed

After provider submission may have occurred, timeout/disconnect/restart MUST NOT imply no consumption and MUST NOT automatically refund a lease. The lease remains charged/quarantined until exact reconciliation, trusted containment, or a predeclared conservative worst-case charge closes it.

## VNX-013 — Resource authority != effect authority

A resource-use grant plus budget lease cannot authorize the semantic effect. An effect capability without required resource admission cannot authorize resource-consuming execution.

## VNX-014 — Region safety is independent

Budget settlement/release MUST NOT release `RegionUse`, transfer ownership, mark data Visible/Published, or authorize reclaim. `RegionAuthority` and ExternalOperation/publication owners retain their transitions.

## VNX-015 — Completion != visibility != publication != release

Provider/CPU completion cannot substitute for memory visibility; visibility cannot substitute for OS publication; publication cannot substitute for Region/resource release.

## VNX-016 — SipJob is never an authority owner

SipJob plans, caches, execution classes and fused metadata MUST NOT own capability, budget, lease, Region, provider or publication truth. Fusion may eliminate transport only when authoritative transition traces remain equivalent.

## VNX-017 — Planner/scheduler/provider agents are policy/evidence only

They MAY select candidates or cache topology/load evidence but MUST NOT cache `authorized=true`, lease liveness, ownership truth, provider authority, or publication state.

## VNX-018 — HybridCPU legality remains independent

Compiler metadata, resource grants and provider evidence MUST NOT override HybridCPU runtime legality. Execution requires independent CPU/runtime legality and provider admission where applicable.

## VNX-019 — No hardware-private ABI leakage

Application/SIP/ManagedCap contracts MUST NOT expose lane ID, raw opcode, slot index, DSC/L7 private token, queue ID, IOMMU handle, CXL HDM/DPA/topology, physical address, VMCS-like identifiers, or other provider-private placement facts as authority.

## VNX-020 — No CHERI/ISA requirement

No vNext phase may require tagged pointers/memory, capability registers, changed pointer width, VLIW format, register model, capability-aware loads/stores/fetches, or other HybridCPU ISA changes.

## VNX-021 — Cross-owner prepare/commit discipline

A multi-owner admission MUST validate/pre-reserve reversible state, revalidate exact generations immediately before irreversible submit, and define compensation for every pre-submit partial failure. No service/provider code runs under authority locks.

## VNX-022 — Resource settlement is independent from publication

Resource usage may become irreversible before OS publication. Settlement MUST neither imply publication nor wait for publication when the resource owner can safely close quantitative truth. Region reclaim remains separately gated.

## VNX-023 — Claim separation

The implementation and documentation MUST distinguish:

```text
AccountingOnly
RuntimeEnforced reservation/accounting
EnforcedUpperBound
GuaranteedReservation
```

No promotion occurs without contour-specific executable proof.

## VNX-024 — Dimensional correctness

Constraint algebra MUST reject arithmetic or merge/split across incompatible resource families/units. Integer arithmetic MUST be checked for overflow, underflow, zero/maximum sentinels, period multiplication, rounding and wrap.

## VNX-025 — Restart does not resurrect authority

Checkpoint/restart MUST NOT deserialize a capability, lease, session donation or provider receipt into live authority. Fresh generation-bound admission/reconciliation is required.

## VNX-026 — Replay evidence is not permission to resubmit

A replay/certificate/receipt may explain prior execution but MUST NOT authorize a new provider submission or new budget charge.

## VNX-027 — Exact correlation

Every resource-consuming external operation SHOULD be traceable:

```text
capability lineage
 -> budget reservation/lease generation
 -> session/invocation
 -> ExternalOperation generation
 -> provider request/generation
 -> usage evidence
 -> local settlement
 -> publication/release outcomes
```

Correlation is evidence only.

## VNX-028 — Terminal policy completeness

Every authority state machine MUST define terminal handling for success, cancel, fault, stale, provider loss, process/session loss, restart, timeout ambiguity and duplicate/reordered evidence.
