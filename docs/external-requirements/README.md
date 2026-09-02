# SingNextOS external requirements

This directory records capabilities that SingNextOS may require from external toolchains or platform integrations while keeping all Sing+ architecture, contracts, admission rules, runtime semantics, tests, and CI changes inside `yuriyyak23/SingNextOS`.

An item in this directory is **External Blocked** only for the named external integration outcome. It must not be interpreted as permission or a requirement to modify HybridCPU-v2, HybridCPU ISE, compiler/backend/linker/loader/runtime/GC/EH, SDK packs, NativeAOT/ILCompiler, Roslyn, or the .NET runtime.

Each requirement uses this format:

- **ID**
- **Required external capability**
- **Why SingNextOS needs it**
- **Existing interface expected**
- **Minimal reproduction**
- **SingNextOS component blocked**
- **Fallback/mock used**

## Current requirements

| ID | Scope |
|---|---|
| [`EXT-HCPU-001`](EXT-HCPU-001.md) | external HybridCPU AOT/image/ISE qualification |
| [`EXT-HCPU-002`](EXT-HCPU-002.md) | platform console/timer/MMIO/IRQ/DMA HAL bindings |
| [`EXT-HCPU-003`](EXT-HCPU-003.md) | neutral execution/memory/I/O domain binding |
| [`EXT-HCPU-004`](EXT-HCPU-004.md) | owned-region mapping, revocation and DMA/direct-access binding |
| [`EXT-HCPU-005`](EXT-HCPU-005.md) | scoped MatrixTile/DSC1/L7 compute provider bindings |
| [`EXT-HCPU-006`](EXT-HCPU-006.md) | virtualization/nested/evidence/SecureCompute feature discovery |

The architecture rationale for `EXT-HCPU-003` through `EXT-HCPU-006` is documented in `docs/whitebook/hybridcpu-ise/`.

Local Definition of Done remains limited to SingNextOS architecture + contracts + verifier + runtime semantics. End-to-end `SingNextOS -> HybridCPU AOT -> HybridCPU image -> ISE` qualification is a separate integration stage.
