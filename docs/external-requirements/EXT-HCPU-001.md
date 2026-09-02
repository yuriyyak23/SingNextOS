# EXT-HCPU-001

**Status:** External Blocked

## Required external capability

A prebuilt HybridCPU toolchain must accept the SingNextOS kernel entry assembly produced by this repository and provide the existing AOT/image path needed to reach a HybridCPU image suitable for execution in the existing ISE.

## Why SingNextOS needs it

The local SingNextOS Definition of Done establishes architecture, manifests, runtime authorities, ownership, IPC, HAL boundaries, admission proofs, deterministic artifacts, tests, and CI. Proving the later end-to-end path `SingNextOS -> HybridCPU AOT -> HybridCPU image -> ISE` requires capabilities owned by the external HybridCPU toolchain.

## Existing interface expected

An already released or otherwise externally supplied HybridCPU SDK/CLI/toolchain interface that can consume a compiled SingNextOS kernel assembly. This requirement intentionally does not prescribe a new CLI, compiler option, ISA feature, linker change, loader change, runtime change, SDK-pack change, or publish workstream.

## Minimal reproduction

1. Build the SingNextOS kernel/boot assemblies inside this repository.
2. Run the local SingPlus admission verifier and retain its `SingPlusAdmissionProofV1` output.
3. Pass the resulting kernel entry assembly to the existing external HybridCPU toolchain as a black box.
4. Observe whether the existing toolchain can produce its normal image artifact and whether that artifact can be accepted by the existing ISE.

No external repository modification is part of this reproduction.

## SingNextOS component blocked

Only external HybridCPU AOT/image/ISE integration qualification. No SingNextOS architecture, runtime authority, contract, ownership, IPC, analyzer, generator, admission, HAL, driver abstraction, or local CI guarantee is blocked by this requirement.

## Fallback/mock used

Host implementations of the SingNextOS HAL plus metadata/CIL admission tests and local runtime/integration tests. These validate SingNextOS-owned guarantees without claiming HybridCPU compiler qualification.
