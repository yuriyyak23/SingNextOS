# SingNextOS v6 — Architectural Refactoring Roadmap

**Status:** proposed cross-project architecture/refactoring package.  
**Research baseline:** 2026-09-23.  
**SingNextOS master:** `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`.  
**HybridCPU-v2 master:** `794c4a53494f503855ac8cf209efab23fde083b2`.  
**Scope:** SingNextOS + HybridCPU-v2/ISE/compiler co-design after the completed SingCap-M, SipJob, vNext resource model, CXL, virtualization/SecureCompute, semantic co-design and Direct SingNext Boot roadmaps.

## 1. Architectural thesis

v6 does **not** create another authority universe. It extends the existing composition model so that properties which are currently implicit, provider-local, deferred, or only weakly correlated become explicit semantic contracts.

The core equation remains:

```text
AuthorizedBySingNext
AND ResourceCapacityLive
AND DataOwnershipAllowed
AND ProviderAdmission
AND GuaranteesRefineObligations
AND HybridCpuRuntimeLegal
    -> execution may become eligible
```

v6 adds missing dimensions to the refinement relation:

```text
memory ordering / atomicity / coherence
persistence / durability
translation / DMA address-space binding
temporal guarantees / preemption
information-flow constraints
locality / data-movement cost
device trust / attestation predicates
RAS / partial-failure state
multi-host delegated leases
energy / thermal envelopes
compiler-produced proof evidence
```

None of these descriptors, receipts, proofs, measurements or certificates become SingNext authority.

## 2. Evolution target

```text
SingNextOS 5
  semantic obligations
  x provider guarantees
  x resource envelopes
  x HybridCPU runtime legality

        |
        v

SingNextOS 6
  + unified heterogeneous memory semantics
  + durability/persistent-state semantics
  + temporal execution contracts
  + exact DMA/translation composition
  + machine-checkable refinement
  + information-flow policy
  + locality/data-motion planning
  + preemptible heterogeneous execution
  + device trust/attestation composition
  + RAS/failure-domain algebra
  + multi-host authority leases
  + energy/thermal resource semantics
  + proof-carrying lowering
```

The intended end state is a **capability-native, contract-driven heterogeneous machine**: SingNextOS owns semantic/effect authority; providers expose enforceable guarantees; HybridCPU owns machine/runtime legality; compiler output supplies non-authoritative proof evidence; qualification proves the refinements for an exact source/runtime/provider tuple.

## 3. Package map

| File | Purpose |
|---|---|
| `01-TECHNICAL-SPECIFICATION.md` | normative v6 technical specification |
| `02-AUTHORITY-OWNER-MAP.md` | one-fact/one-owner map and forbidden authority inversions |
| `03-PHASE-DAG-AND-MIGRATION.md` | dependency graph, rollout and migration order |
| `04-NORMATIVE-INVARIANTS.md` | cross-cutting invariants for every phase |
| `05-FEATURE-GATES-AND-CLAIMS.md` | claim levels, gates and promotion rules |
| `10-P01-UNIFIED-MEMORY-SEMANTICS.md` | ordering/coherence/atomicity/visibility model |
| `11-P02-DURABILITY-PERSISTENT-MEMORY.md` | persistence, crash consistency and durable state |
| `12-P03-TEMPORAL-EXECUTION-CONTRACTS.md` | temporal leases, interference and schedulability |
| `13-P04-TRANSLATION-DMA-AUTHORITY.md` | IOMMU/SVA/PASID-style exact DMA binding |
| `14-P05-MACHINE-CHECKABLE-REFINEMENT.md` | executable refinement/specification framework |
| `15-P06-INFORMATION-FLOW-CONTROL.md` | IFC labels and explicit declassification |
| `16-P07-LOCALITY-DATA-MOTION.md` | semantic locality and placement cost model |
| `17-P08-PREEMPTIBLE-HETEROGENEOUS-EXECUTION.md` | suspend/resume/safe-point provider contracts |
| `18-P09-DEVICE-TRUST-ATTESTATION.md` | device trust evidence composition |
| `19-P10-RAS-FAILURE-DOMAINS.md` | poison/ECC/partial-failure/reconfiguration semantics |
| `20-P11-MULTIHOST-AUTHORITY-LEASES.md` | fabric-scale delegated lease model |
| `21-P12-ENERGY-THERMAL-RESOURCES.md` | energy/power/thermal dimensions |
| `22-PROOF-CARRYING-LOWERING.md` | compiler proof/evidence pipeline |
| `30-CROSS-PROJECT-CONTRACTS.md` | SingNextOS ↔ HybridCPU-v2 ABI/contracts |
| `31-QUALIFICATION-AND-TEST-MATRIX.md` | executable proof and negative-test plan |
| `32-TRACEABILITY-MATRIX.md` | requirement → owner → phase → evidence mapping |
| `33-PR-SLICING-AND-CUTOVER.md` | implementation slices and rollback discipline |
| `34-NON-GOALS-AND-OPEN-QUESTIONS.md` | boundaries and intentionally deferred claims |

## 4. Recommended implementation order

Critical path:

```text
P00 baseline/freeze
 -> P01 unified memory semantics
 -> P04 translation/DMA authority
 -> P05 machine-checkable refinement
 -> P02 durability
 -> P03 temporal contracts
 -> P08 preemptible execution
 -> P09 trust/attestation
 -> P10 RAS
 -> qualification vertical
```

Parallel branches after P01/P05:

```text
P06 IFC
P07 locality/data-motion
P12 energy/thermal
Proof-Carrying Lowering
```

P11 multi-host is intentionally last. It must consume the already-qualified single-host semantics rather than redesigning local authority to look distributed.

## 5. First qualification vertical

v6 SHOULD qualify one bounded end-to-end contour before broadening the model:

```text
ManagedCap/SIP request
 -> OperationObligationsV2
 -> 3 Region operands
 -> shared-read + staged-output memory semantics
 -> provider selection between CPU/MatrixTile/Lane6/Lane7
 -> exact translation/DMA binding when required
 -> semantic refinement
 -> HybridCPU legality
 -> execution/retire measurement
 -> visibility/publication
 -> budget settlement
 -> optional durable-output barrier
```

A matrix/tile workload remains a good reference vertical because the repository already contains MatrixTile, DmaStreamCompute and external-accelerator execution surfaces, but v6 must not encode MatrixTile-specific policy into generic contracts.

## 6. Non-negotiable philosophy

```text
evidence != authority
identity != authority
mapping != ownership
coherence != ownership
completion != visibility
visibility != publication
publication != durability
reservation != permission
compiler proof != runtime permission
attestation != authority
scheduler choice != legality
topology != authority
replay certificate != permission
provider loss != closure
```

Every v6 phase exists to make one of these distinctions executable rather than merely documentary.
