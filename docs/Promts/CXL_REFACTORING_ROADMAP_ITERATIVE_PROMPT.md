# CXL Refactoring Roadmap — Iterative Execution Prompt

## Role

You are the lead SingNextOS architect and implementer for CXL provider
decomposition, memory ownership/use, external-operation lifecycle, device/fabric
binding, visibility/publication, and feature/evidence boundaries. Work only in:

```text
C:\Users\Yuriy Kurnosov\Desktop\SingNextOS
```

## Inputs and baseline

Read completely before acting:

```text
docs/cxl-refactoring-roadmap/README.md
docs/cxl-refactoring-roadmap/00-gap-matrix-and-decisions.md
docs/cxl-refactoring-roadmap/00_PHASE0_BASELINE_AUDIT.md
docs/cxl-refactoring-roadmap/01-mutation-epoch-and-region-use.md
docs/cxl-refactoring-roadmap/02-common-external-operation-lifecycle.md
docs/cxl-refactoring-roadmap/03-provider-selection-and-dependency-model.md
docs/cxl-refactoring-roadmap/04-cxl-authority-and-provider-decomposition.md
docs/cxl-refactoring-roadmap/05-cxl-type3-memory-provider.md
docs/cxl-refactoring-roadmap/06-cxl-type2-accelerator-service.md
docs/cxl-refactoring-roadmap/07-visibility-publication-and-replay-contract.md
docs/cxl-refactoring-roadmap/08-real-hardware-and-qemu-backend.md
docs/cxl-refactoring-roadmap/09-fabric-manager-pooling-and-reconfiguration.md
docs/cxl-refactoring-roadmap/10-security-securecompute-and-multihost.md
docs/cxl-refactoring-roadmap/11-validation-and-negative-test-matrix.md
docs/cxl-refactoring-roadmap/12-cross-project-contracts-and-non-goals.md
docs/external-requirements/*.md
```

Then inspect current RegionAuthority/OwnedRegion/OwnedBuffer/MOVE/borrow,
RuntimeKernel transitions, PlatformAuthorityBridge, mapping/device/DMA/MMIO/IRQ
contracts, PlatformMemoryVisibility, completion/reclaim, DeviceResourceSet,
compute, virtualization, evidence/SecureCompute, feature manifests, generated
SIP/session routes, public/dependency tests and current qualification status.

Phase 0 is already completed as a baseline audit. Start at Phase 1. Treat all
existing working-tree changes as user-owned unless they are clearly part of the
current CXL phase; preserve them and do not repair unrelated work.

## Non-negotiable architecture

- `RegionAuthority` is the only memory ownership root. CXL discovery, topology,
  security and provider records are facts or opaque lower-layer bindings, never
  a second capability system.
- `security evidence != authority`; `coherence != ownership`; `coherence !=
  publication`; `completion != visibility`; `visibility != ownership return`.
- MOVE and borrow retain their existing authority semantics. Completion/fences
  are evidence, not reclaim or capability authority.
- No raw CXL/PCIe/IOMMU identity (physical address, HPA/DPA, HDM decoder,
  route/port, requester ID, PASID, provider token, mailbox/register encoding)
  may enter public contracts, SIP, diagnostics, manifests or ordinary app APIs.
- CXL.io reuses DeviceLease/DeviceResourceSet and bounded MMIO/IRQ/DMA. CPU
  access to CXL.mem is normal memory access, not a fake IOMMU mapping.
- Default Type-2 output is staged. Direct coherent final-region writes are an
  optional stronger policy and may not be silently substituted for staging.
- Use narrow provider roles, never a monolithic `ICxlProvider`:
  discovery, CXL.io, memory, coherent access, fabric, security evidence.
- Do not modify HybridCPU-v2, HybridCPU ISA, compiler/lowering, lanes, replay
  internals, legality engine or external packages/specifications.
- Hardware/QEMU/mock facts never prove unavailable hardware coherence, IDE,
  isolation, Fabric Manager enforcement, zero-copy, security or multi-host
  writable sharing.

## Phase execution method

Work one phase at a time, in roadmap order. Before each phase, perform a delta
audit against current code/tests and previous phase evidence. Implement only
the smallest consistent vertical slice needed by that phase; do not prebuild
later CXL providers or invent compatibility transport. Update roadmap/evidence
documents when code changes invalidate plan assumptions or require a narrow,
explicit correction. Do not claim a phase complete until code, tests, teardown,
negative cases and documentation agree.

### Phase 1 requirements

Implement provider-neutral `MutationEpoch` and `RegionUse` in the existing
region authority path. Start whole-region conservative: current authority does
not prove range subdivision. Increment epochs only for authority-mediated
transitions; direct managed `Span` writes are not observable and must not be
claimed as automatically tracked. Prove stale use fails before submit/publish,
incompatible writers exclude, release is idempotent, MOVE/borrow/reclaim cannot
resurrect use, and no CXL identity appears publicly.

### Subsequent phases

- Phase 2: one provider-neutral seven-state external operation lifecycle;
  separate submit, completion, visibility, publication and release.
- Phase 3: semantic ComputeIntent/planner/provider policy/dependency DAG;
  provider capability is not authority and coherent-to-staged fallback must
  preserve semantics.
- Phase 4: introduce only narrow CXL provider bindings/evidence beneath the
  existing authority chain, with narrow generation invalidation.
- Phases 5–7: fake/model Type-3, staged Type-2 and replay/publication semantics
  before hardware. Preserve ordinary OwnedRegion and DeviceResourceSet APIs.
- Phases 8–10: QEMU/hardware, fabric manager and security/multi-host only with
  genuine provider proof. Keep unproven functions `Unavailable`, `FutureGated`
  or `ExternalBlocked`; writable multi-host sharing remains disabled absent a
  separately designed distributed ownership/fencing protocol.

## Required verification for every phase

Add focused positive and negative tests that prove the phase’s authority,
generation, cancellation/reset/reclaim and public-surface invariants. Reuse
generated SIP, EndpointSession, ownership and component admission contracts;
do not make special CXL transport.

At minimum, run fresh restore, relevant focused tests and:

```powershell
dotnet restore SingNextOS.slnx --force --no-cache
dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
```

If a dirty-worktree or unrelated failure prevents qualification, do not edit it
without scope. Report the exact file/error and still provide all successful
focused evidence.

## Final report per iteration

Report: phase and delta-audit result; decisions/plan corrections; changed files;
exact authority/lifecycle route; positive and negative evidence; tests and full
qualification results; current claims versus FutureGated/Unavailable/
ExternalBlocked features; hardware/external blockers; and precise entry criteria
for the next phase. State clearly whether the phase is complete, partial or
blocked. Never upgrade a model, fake, wrapper, receipt or documentation entry
into hardware/provider proof.
