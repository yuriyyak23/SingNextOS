# R7 Implementation Evidence — Stage-1 and HybridBootInfo ABI

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; the recorded dirty worktree was preserved. The R0 `HybridBootInfoV1` wire contract and existing boot/runtime boundaries were audited before implementation.

Implemented a deterministic Stage-1 load model with bounded component count/size, checked aligned RAM ranges, overlap exclusion, destination SHA-384 verification, executable-entry containment, semantic copy fault injection, and all-or-nothing publication. On any later component failure, prior copied model buffers are zeroed and no component remains publishable. A fixed-width `KernelEntryAbiV1` descriptor validator checks ABI version, reserved fields, entry address, and BootInfo range. `HybridBootInfoCodec` remains immutable, length-bounded, SHA-384-digested and CRC32C-protected.

The `Stage0Handoff` concept remains adapter-internal. BootInfo contains evidence and temporary state only; it contains no capability, `OwnedRegion`, `RegionUse`, provider lease, or live authority handle. Image generation, mapping generation, provider generation, and region generation remain distinct domains. CXL loss after a completed, verified copy does not change the copied normal-RAM bytes.

Classification: codecs and validation are `ExecutableInScope`; loading and register handoff are `ModelOnly`. Actual architectural-register setup, CPU branch to the kernel entry, cache/coherency transition, and HybridCPU execution are `FutureGatedRequiresCore`, owned by the HybridCPU reset/frontend/runtime owner. Missing prerequisite: an approved executable entry-hook contract and actual core qualification.

Qualification: focused Stage-1 tests 5/0; adapter regression 58/0; solution build 0 warnings/errors; full tests adapter 58/0, neutral 58/0, platform 60/0, main 1200 passed/0 failed/2 skipped; `git diff --check` is included in phase closure. Tests cover overlap, bad hash, invalid entry, ABI mismatch, corrupted BootInfo, deterministic copy failure, and CXL loss after completed copy.

Changed files: `tools/HybridCpu_ExecutableAdapter/Boot/Stage1LoadModel.cs`, `tools/HybridCpu_ExecutableAdapter.Tests/Stage1LoadModelTests.cs`, this evidence, and the traceability matrix. Claim level: `ModelValidated` for load/handoff behavior and `ContractOnly` for the register entry ABI. No HybridCPU core/ISE/ISA/compiler/architecture implementation changed.

