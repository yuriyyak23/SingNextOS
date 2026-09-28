# Phase 3 — Provider Selection, Second Engine and Dependency Model

Status: complete for provider-neutral semantic planning, mock provider
selection and staged/direct dependency graphs. See
[03_PHASE3_IMPLEMENTATION_EVIDENCE.md](03_PHASE3_IMPLEMENTATION_EVIDENCE.md).

## Goal

Complete the planned heterogeneous-compute foundation needed before CXL-specific providers: provider selection must be policy-driven, operation dependencies explicit, and execution engines replaceable without leaking transport identity into compute intent.

## Why this is required for CXL

A Type-2 accelerator may support CXL.io, CXL.cache and CXL.mem simultaneously. A Type-3 device is a memory provider, not an accelerator. A CXL.io-only function should remain on the ordinary device path. Therefore `CXL == provider` is too coarse.

## Provider-neutral model

Recommended decomposition:

```text
ComputeIntent
  -> ComputePlanner
     -> ProviderSelectionPolicy
     -> Region/placement requirements
     -> dependency DAG
     -> ExternalOperation lifecycle
```

Provider capabilities are facts used by selection, not authority grants.

### Capability dimensions

Examples:

```text
MemoryPlacementCapability
CoherentHostAccessCapability
DeviceLocalMemoryCapability
DmaCapability
AcceleratorExecutionCapability
StagedPublicationCapability
DirectCoherentOutputCapability
SecureComputeEvidenceCapability
```

CXL-specific features may populate these dimensions but should not replace them.

## Dependency DAG

The DAG must express at least:

- input preparation/ownership transfer;
- memory placement/binding;
- device submission;
- completion;
- visibility/acquire;
- publication;
- downstream consumer readiness;
- release/reclaim.

Do not use CXL fabric topology as a dependency graph for application execution. The provider may use topology internally to decide feasibility/cost.

## Provider selection policy

Selection may consider:

- required access mode and `RegionUse` compatibility;
- latency/bandwidth/NUMA placement class;
- memory capacity/availability;
- staging cost;
- coherent access support;
- security evidence requirements;
- fault/reclaim state;
- virtualization/domain constraints;
- explicit caller policy.

Selection must not use device attestation/security evidence as memory authority.

## Invariants

- compute intent never names raw CXL route/HDM/DPA identities;
- provider capability does not create region/device authority;
- choosing a provider does not bypass `Admitted` state;
- fallback from coherent to staged path is allowed only if semantics are unchanged;
- fallback from staged to direct coherent write is forbidden unless the direct path has separately satisfied its stronger contract.

## Negative tests

- provider advertises coherence but lacks compatible region use -> not selectable;
- provider disappears after planning -> re-plan before submit, never reuse stale binding;
- security evidence fails -> secure-required intent rejects or chooses another provider;
- provider requests CXL-specific identity from application-facing descriptor -> API review/test failure;
- DAG attempts to consume `DeviceComplete` output before `Visible/Published` -> reject dependency edge.

## Acceptance criteria

- mock second provider can be selected without changing compute intent;
- dependency DAG can represent staged and direct paths distinctly;
- capability taxonomy has no monolithic `IsCxl` shortcut as the primary policy mechanism;
- provider selection remains testable without hardware.

## External blockers

None. HybridCPU integration is explicitly not required in this phase.
