# R6 Implementation Evidence — Temporary HDM Boot Aperture Model

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; the pre-existing dirty worktree was recorded and preserved. Before this phase the reset/map contracts and adapter tests were audited; no HybridCPU core, ISE, ISA, compiler, scheduler, pipeline, register, fetch, load/store, retire, or memory-controller source was changed.

Implemented an architecture-neutral, deterministic temporary-aperture model with checked HPA/DPA range arithmetic, alignment and granularity validation, single-target/non-interleaved enforcement, capacity validation, collision checks against RAM/MMIO/reserved ranges, transactional decoder-chain programming, semantic-operation fault injection, reverse-order compensation, explicit destroy, and reset-generation staleness. `TemporaryApertureDescriptor` is evidence only: it is not an `OwnedRegion`, `RegionUse`, provider lease, live decoder handle, or reusable OS mapping.

Classification: contract and model behavior is `ModelOnly`; production HDM programming is `FutureGatedRequiresHardware`. The owning subsystem is the platform CXL/firmware backend. Missing prerequisites are a qualified decoder-chain transaction API, target-specific aperture-placement policy, reset/watchdog ownership, and hardware link/read-fault semantics. Real mapping, DMA/IOMMU isolation, and silicon behavior are explicitly excluded.

Failure semantics are fail-closed: unavailable decoder, overlap, inadequate capacity, overflow, link loss, timeout, read fault, or injected partial failure yields no usable descriptor. Every programmed hop is compensated in reverse order. A descriptor whose reset generation differs from the current generation is stale and cannot be read or reused; reset never preserves software mapping authority.

Focused tests cover successful create/read/destroy, invalid geometry and overlap, capacity/link/read/timeout failures, exact reverse rollback for every partial decoder-chain failure, and reset-generation staleness. Qualification results: focused 6 passed/0 failed; adapter regression 53/0; solution build 0 warnings/errors; full tests adapter 53/0, neutral runtime 58/0, HybridCPU platform 60/0, main 1200 passed/0 failed/2 skipped; `git diff --check` exit 0 (line-ending warnings only).

Changed files: `tools/HybridCpu_ExecutableAdapter/Boot/TemporaryApertureModel.cs`, `tools/HybridCpu_ExecutableAdapter.Tests/TemporaryApertureModelTests.cs`, this evidence, and the traceability matrix. Claim level: `ModelValidated`.

