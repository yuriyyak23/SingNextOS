# SingNextOS vNext — corrected refactoring roadmap (audit-applied)

**Status:** corrected implementation roadmap derived from the vNext architecture audit. This document is a plan, not an executable/security/performance claim.

## Live baseline

- **SingNextOS `master`:** `6227ea7cf258ef6ffce52001d4d2ffee07355b35` (`vnext plan1 upd`, 2026-09-20T13:54:11Z).
- **HybridCPU-v2 `master`:** `794c4a53494f503855ac8cf209efab23fde083b2` (2026-09-18T21:05:22Z).
- **Research/update date:** 2026-09-20.
- **Known exact ExternalRuntime package contour from existing SingNextOS evidence:** `HybridCPU.ExternalRuntime.Contracts/1.14.0`; P16 requires re-pinning this package and digest at qualification time.

The superseded roadmap was pinned to SingNextOS `386e247e37f4e252d1bf4fda80a57b49858fb788`. The live tree advanced to `6227ea7cf258ef6ffce52001d4d2ffee07355b35`. The most important live confirmation is that `ResourceBudgetAuthority` remains the **single accounting ledger** and its reservations admit capacity but do not authorize effects.

## Source-of-truth order

```text
live code + executable tests
    > current normative specifications
    > current implementation evidence
    > this roadmap
    > historical documents
```

Any contradiction with live code/tests blocks promotion of the affected phase.

## Architectural correction

The old plan introduced an independent `TemporalResourceAuthority`. The corrected design removes that second owner.

Resource control is instead a composition of existing owners:

```text
CapabilityAuthority
  -> semantic permission, including a narrow resource-use grant/constraint family

ResourceBudgetAuthority
  -> quantitative capacity, reservation, lease, consumption, settlement, quarantine

RegionAuthority
  -> memory ownership/use/reclaim

EndpointSession/Invocation owners
  -> request identity, generation, protocol state, donation binding

ExternalOperationAuthority
  -> external-effect lifecycle and exact operation generation

Publication owners
  -> visibility/publication truth

Provider / HybridCPU
  -> provider admission, runtime legality, execution/retire evidence
```

There is **no new universal authority ledger** and no second resource ledger.

The admission predicate for an external resource-consuming effect is conceptually:

```text
EffectAllowed
AND ResourceUseGrantAllowed
AND ResourceBudgetReservationLive
AND DataOwnershipAllowed
AND ExactSessionAndGenerationsLive
AND ProviderAdmissionAllowed
AND CpuRuntimeLegal
    -> submit may become eligible
```

The checks are logically independent. Their mutations are composed by explicit linearization/commit protocols; they are not one giant lock or one synthetic authority object.

## Resource-model correction

The following objects are not synonyms:

| Concept | Authority? | Accounting? | Reserves capacity? | Guarantees capacity? | Provider-bound? | Delegatable? | Consumable? |
|---|---:|---:|---:|---:|---:|---:|---:|
| Accounting budget/account | no effect authority | yes | no | no | no | hierarchy only | no |
| Resource-use grant in `CapabilityAuthority` | yes, only permission to consume class/envelope | no | no | no | semantic scope only | yes, monotonic | no |
| Reservation / lease in `ResourceBudgetAuthority` | quantitative admission token | yes | yes | no by default | may be bound later | split only by owner rules | yes |
| Provider binding | no SingNext authority | no | provider-specific only | no | yes | no | no |
| Usage evidence / receipt | no | evidence only | no | no | yes | no | no |
| Settlement | authoritative local transition | yes | releases/charges | no | correlated | no | closes consumption |
| Guarantee | not an object by itself | no | may rely on reservation | **only after separate qualification** | contour-specific | no | service property |

## Honest claim vocabulary

```text
ModelOnly
StaticAdmission
RuntimeEnforced
ExecutableAdapter
EnforcedUpperBound
GuaranteedReservation
ProductionQualified
```

No level implies the next. In particular:

```text
AccountingOnly != EnforcedUpperBound != GuaranteedReservation
```

The minimal vNext contour targets **one resource class** and at most `RuntimeEnforced`/`ExecutableAdapter`/`EnforcedUpperBound` where evidence exists. Hard realtime and guaranteed minimum capacity remain default-off future contours.

## Corrected phase graph

```text
P00  Live baseline + owner freeze
  |
P01  Resource model, dimensional algebra, normative invariants
  |
P02  Resource-use grants inside existing CapabilityAuthority
  |
P03  ResourceBudgetAuthority atomic reservation/lease/settlement core
  |
P04  Cross-owner admission transaction and commit protocol
  |
P05  SIP + manifest contracts and generated sentry integration
  |
P06  EndpointSession request-scoped donation/delegation
  |
P07  ExternalOperation binding, provider ambiguity, quarantine
  |
P08  ComputePlanning v2 with independent gates
  |
P09  HybridCPU/provider-neutral co-design contracts
  |
P10  ResourceScheduler + provider agents as policy/evidence only
  |
P11  Temporal upper-bound enforcement; guarantees remain separate/off
  |
P12  SipJob resource-aware composition with trace equivalence
  |
P13  Non-compute resource families: throughput and occupancy
  |
P14  Restart/checkpoint/reconciliation and ABA closure
  |
P15  Observability/audit with telemetry != authority
  |
P16  Qualification, performance, claim discipline
  |
P17  Additive migration, mixed-version cutover, cleanup
```

P00-P07 are security foundation. No dependent phase may promote while a prerequisite has an unresolved blocking invariant.

## Phase index

| Phase | File | Main result |
|---|---|---|
| P00 | `PHASE_VNEXT_00_ARCHITECTURAL_FREEZE_AND_LIVE_BASELINE.md` | live tuple, owner freeze, gates OFF |
| P01 | `PHASE_VNEXT_01_RESOURCE_MODEL_AND_DIMENSIONAL_ALGEBRA.md` | truth table + typed dimensions |
| P02 | `PHASE_VNEXT_02_RESOURCE_USE_GRANTS_IN_EXISTING_CAPABILITYAUTHORITY.md` | resource-use permission inside existing capability ledger |
| P03 | `PHASE_VNEXT_03_ATOMIC_RESOURCE_LEASE_AND_SETTLEMENT_CORE.md` | single quantitative lease/settlement owner |
| P04 | `PHASE_VNEXT_04_CROSS_OWNER_ADMISSION_COMMIT_PROTOCOL.md` | prepare/revalidate/commit/compensate protocol |
| P05 | `PHASE_VNEXT_05_SIP_AND_MANIFEST_RESOURCE_CONTRACTS.md` | generated sentry + manifest contracts |
| P06 | `PHASE_VNEXT_06_ENDPOINTSESSION_RESOURCE_DONATION.md` | non-laundering request-scoped donation |
| P07 | `PHASE_VNEXT_07_EXTERNALOPERATION_RESOURCE_BINDING.md` | external binding + ambiguity/quarantine |
| P08 | `PHASE_VNEXT_08_COMPUTEPLANNING_V2_INDEPENDENT_GATES.md` | non-authoritative semantic planning |
| P09 | `PHASE_VNEXT_09_HYBRIDCPU_PROVIDER_NEUTRAL_INTEGRATION.md` | provider-neutral HybridCPU integration |
| P10 | `PHASE_VNEXT_10_RESOURCE_SCHEDULER_AND_PROVIDER_AGENTS.md` | policy-only scheduler/agents |
| P11 | `PHASE_VNEXT_11_TEMPORAL_UPPER_BOUNDS_AND_GUARANTEE_DISCIPLINE.md` | upper bounds separated from guarantees |
| P12 | `PHASE_VNEXT_12_SIPJOB_RESOURCE_AWARE_COMPOSITION.md` | fused path trace equivalence |
| P13 | `PHASE_VNEXT_13_NON_COMPUTE_RESOURCE_FAMILIES.md` | throughput/occupancy classes |
| P14 | `PHASE_VNEXT_14_RESTART_CHECKPOINT_AND_RECONCILIATION.md` | no resurrection + reconciliation |
| P15 | `PHASE_VNEXT_15_OBSERVABILITY_AUDIT_AND_TELEMETRY.md` | telemetry remains evidence only |
| P16 | `PHASE_VNEXT_16_QUALIFICATION_PERFORMANCE_AND_CLAIMS.md` | exact evidence/claim closure |
| P17 | `PHASE_VNEXT_17_MIGRATION_CLEANUP_AND_CUTOVER.md` | mixed-version migration/cutover |

## Non-goals

This roadmap does **not** require or permit:

- CHERI pointers, capability registers, tagged pointers/memory/cache lines;
- ISA-visible memory capability metadata;
- HybridCPU pointer width, register model, VLIW format, typed-lane or ISA changes;
- lane ID/opcode/slot/queue/IOMMU/CXL topology in application or SIP authority ABI;
- treating DTOs, receipts, certificates, telemetry or cached plans as authority;
- hard realtime claims without provider-specific executable proof.

## Minimal safe first slice

1. One resource class: `ComputeTime`/CPU-runtime time in a single canonical unit.
2. One resource-use grant family in the existing `CapabilityAuthority` constraint algebra.
3. One `ResourceBudgetAuthority` reservation/lease/settlement path.
4. One request-scoped EndpointSession donation path.
5. One ExternalOperation binding.
6. One provider contour: host model first; HybridCPU executable adapter only after P09 qualification.
7. Ordinary SIP first; SipJob remains off until P12 differential proof.
8. No energy authority, universal CXL bandwidth, universal provider agents, all-resource scalar, or guaranteed realtime in the initial contour.

See `AUDIT_CORRECTIONS.md`, `AUTHORITY_OWNER_MAP.md`, `RESOURCE_MODEL.md`, `VNEXT_NORMATIVE_INVARIANTS.md`, `VNEXT_FEATURE_GATES.md`, and the P00-P17 phase files.
