# Phase 0 — Baseline, Gap Matrix, and Frozen Decisions

## Purpose

Freeze the current HybridCPU-v2/Compiler behavior before adding SingNextOS/CXL integration so implementation PRs cannot silently redefine legality, replay, lane placement, or publication semantics.

## Code-grounded baseline

Treat executable code and tests as authority before documentation. Audit and pin behavior for:

- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/*`;
- `LegalityDecision`, `LegalityAuthoritySource`, `RejectKind`;
- GuardPlane owner/domain/boundary checks and guard-before-reuse paths;
- replay-phase certificates, structural certificates, `LoopBuffer`, `ReplayToken`;
- lane6 `DmaStreamCompute/*` descriptors, validators, token store, runtime, retire/publication;
- lane7 external accelerator runtime, descriptor guards, token lifecycle, fences, staging and `AcceleratorCommitCoordinator`;
- `HybridCPU_ExternalRuntime.Contracts`, `HybridCPU_ExternalRuntime`, and tests;
- compiler IR model, resource/slot classes, admission and bundle builder;
- compiler platform/runtime bridge projects under `Compilers/`.

## Gap matrix

### Reuse as-is conceptually

- fixed typed-slot topology and existing lane aliases;
- `LegalityDecision` as CPU scheduling/runtime legality result;
- GuardPlane as CPU-side owner/domain/runtime guard authority;
- replay/structural certificate separation;
- staged commit model for external accelerator writes;
- compiler typed-slot resource accounting.

### Extend

- external-operation contracts to consume opaque SingNextOS admission/completion/publication receipts;
- generation snapshots and stale-result rejection;
- replay effect taxonomy for OS-mediated external effects;
- L7-SDC backend abstraction to route through SingNextOS rather than device-specific/fake backends;
- DSC backend/binding model where actual DMA/external execution is OS-mediated;
- compiler semantic intent and lowering metadata;
- diagnostics for OS rejection, stale generation, visibility failure, reset/reconfiguration.

### Do not implement

- `Cxl` lane, `RemoteLane`, or new slot class solely for CXL;
- `Cxl` as a CPU legality authority source;
- raw HDM/DPA/port/switch/FM IDs in ISA, descriptors, compiler IR, or application ABI;
- replay certificate as permission to re-submit an external operation;
- `DeviceComplete` as architectural commit;
- automatic direct coherent writes or zero-copy;
- assumption that CXL.mem CPU access is an IOMMU mapping;
- assumption that all CXL.cache traffic is IOMMU-authorized.

## Frozen architecture decisions

1. **Provider neutrality above ExternalRuntime.** HybridCPU core and compiler express semantics; SingNextOS chooses PCIe/CXL/provider details.
2. **No new legality plane.** SingNextOS admission is checked by a dedicated external-operation guard/binding layer; it does not replace or masquerade as GuardPlane/certificate authority.
3. **Existing lanes remain architectural carriers.** Lane7 L7-SDC remains the primary external accelerator command carrier; lane6 DSC remains its distinct DMA-stream compute contour.
4. **Staged publication first.** Direct coherent publication is a later opt-in path requiring explicit proof and replay policy.
5. **Opaque generations.** HybridCPU stores/compares SingNextOS-provided generation receipts without interpreting CXL topology.
6. **Version contracts independently of CXL revision.** CXL 3.x/4.x differences normally appear as provider capabilities, not ISA versions.

## Deliverables

- baseline test inventory;
- mapping from every planned change to current source/tests;
- explicit non-goals checked into roadmap review checklist;
- no production behavior change in this phase.

## Exit criteria

No Phase 1 implementation starts until reviewers can answer where CPU legality, OS authority, replay evidence, visibility, publication, and ownership return are independently decided.