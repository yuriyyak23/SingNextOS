# Cross-Project integration requirements

Target integration areas below are not missing-code assertions or layer-edit authorization. Current queue and scoped implementation exits: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md).

## SingNextOS changes

Target capabilities to reuse/complete per actual consumer (not an absent-code checklist):

1. Additive semantic extension envelope/canonicalization and digest binding around V1 contracts.
2. P01 memory sidecar and staged reference semantics.
3. P04 generation-bound DMA correlation and final revalidation.
4. P05 trace/refinement evaluator and differential harness.
5. Qualification/claim guards and architecture tests preventing duplicate owners/authority leakage.
6. QV1 end-to-end staged heterogeneous contour.

Later/optional changes:

- P02 persistence sidecars/recovery freshness;
- P03 temporal resource dimensions;
- P08 preemption lifecycle;
- P06 protected IFC;
- P07 locality evidence/planning;
- P09 trust predicates;
- P10 RAS subrange consequences;
- P12 energy dimensions;
- P11 lease protocol after single-host closure.

## HybridCPU ExternalRuntime changes

Prefer additive changes only:

- expose exact provider/session/manifest generation needed by v6 binding;
- expose translation invalidation/reset/containment evidence for selected DMA provider;
- expose visibility/publication-relevant provider evidence without claiming SingNext publication;
- optionally expose memory/temporal/preemption/trust/RAS guarantee sidecars for concrete adapters;
- preserve `CPU guard != provider admission != SingNext authority`.

No SingNext capability objects enter ExternalRuntime authority decisions.

## HybridCPU ISE/runtime boundary

ISE legality, retire, fences, memory model, pipeline and machine state are independent contracts to consume and validate. Current user authorization also permits implementation/modification of the external HybridCPU compute provider with DMA/coherence and virtualization, SecureCompute contracts, IPlatformAuthorityProvider and VirtualDomain/SecureCompute extensions for concrete producing consumers. Current authorization includes compiler, HybridCPU_ExternalRuntime.Contracts, HybridCPU_ExternalRuntime.Tests, HybridCPU_ExternalRuntime, Tools and other external tools in a new folder or HybridCPU_ISE/NonRTL. NonRTL is a permitted external-tool location, not authorization to change opcodes/encoding/register state, memory model, pipeline/retire, firmware/reset or machine legality. The table below classifies target integration, not permission to edit machine internals. A gap requiring ISE/ISA changes needs separate authorization and ADR. Software evidence cannot supply missing machine permission.

## Compiler changes

Compiler changes are not a prerequisite for local root routing, publication, pins or owner/interlock fixes. A selected QV1 executable artifact still needs its actual compiler/toolchain inputs and runtime compatibility; requiring an existing compiler is distinct from requiring compiler modifications.

Optional later work:

- PCL evidence sidecar generation;
- footprint/alias/order/safe-point facts;
- compiler contract v6 compatibility preserved until an actual breaking compiler/runtime change is required.

## Current implementation dependency boundary

SingNextOS local ownership/cleanup guards can progress on resolved contracts without changing the sibling HybridCPU project. Optional exact artifact release already has an adopted producer/consumer bounded exit; preserve incapable/history-loss and independent backing pins. Remaining unknown original-invocation containment needs a real producer contract and actual consumer before its pins can be discharged. Restart and production provider execution need actual provider consumers; callbacks/models do not establish them. These are implementation gaps, not requests for additional reports.

Resolve package versions from actual projects/assets. Sibling source edits do not update SingNextOS PackageReferences automatically. Production admission/legality remain independent; Tools/compiler become dependencies only for a selected actual artifact/execution consumer.

## ISA changes

**None required by the corrected roadmap at this time.**

Per-phase classification:

| Phase | Impact |
|---|---|
| P01 | RUNTIME_ONLY / SIDEBAND_CONTRACT |
| P02 | SIDEBAND_CONTRACT |
| P03 | RUNTIME_ONLY / SIDEBAND_CONTRACT |
| P04 | SIDEBAND_CONTRACT / RUNTIME_ONLY |
| P05 | RUNTIME_ONLY instrumentation |
| P06 | SIDEBAND_CONTRACT optional |
| P07 | SIDEBAND_CONTRACT optional |
| P08 | RUNTIME_ONLY / SIDEBAND_CONTRACT; COMPILER_CONTRACT optional safe points |
| P09 | SIDEBAND_CONTRACT |
| P10 | SIDEBAND_CONTRACT |
| P11 | SIDEBAND_CONTRACT |
| P12 | SIDEBAND_CONTRACT |
| PCL | COMPILER_CONTRACT |

No row is `ISA_EXTENSION_REQUIRED`. Any future proposal requires a separate ADR showing why runtime legality, existing fences, sideband, provider contracts and generation checks are insufficient.
