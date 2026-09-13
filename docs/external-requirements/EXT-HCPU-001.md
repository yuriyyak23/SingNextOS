# EXT-HCPU-001

**Status:** ExternalBlocked at `ManagedAssemblyToHybridCpuAot`

## Audited qualification outcome — 2026-09-04

The qualification subject and external source baseline are pinned independently:

| Input | Exact identity |
|---|---|
| SingNextOS iteration base before qualification changes | `108195c7383bb7d105ee43a4f7087cf2157e021e` |
| Qualified SingNextOS revision | exact clean workflow `HEAD`, recorded per run as `Inputs.SingNextOsRevision` and bound to `github.sha` |
| HybridCPU-v2 source baseline | `9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9` |
| HybridCPU-v2 audited tree | `e810667c30a6534a0c2228b46d6f9d1eee373c5f` |
| SingNextOS .NET SDK | exact `10.0.204` from the qualification changeset's `SingNextOS/global.json`, with SDK roll-forward disabled |
| HybridCPU-v2 .NET SDK | `10.0.201` from `HybridCPU-v2/global.json` |
| HybridCPU compiler/runtime contract | `CompilerContract.Version = 6` |

The deterministic qualification job runs on GitHub Actions
`ubuntu-24.04`. Its log captures the resolved runner image and `dotnet --info`;
the workflow log is the execution receipt for the checked-in qualification
script. Checked-out commit identities, the compiler/runtime contract version,
generated digests and the canonical substantive restore/build/admission recipe
are captured in the per-run report:

```text
artifacts/hybridcpu-aot-qualification/SingPlusHybridCpuQualificationV1.json
```

The same artifact directory contains `SHA256SUMS`, including the digest of the
report itself and both copies of every compared local artifact. The directory is
uploaded as:

```text
hybridcpu-aot-qualification-${{ github.sha }}
```

After the pinned HybridCPU checkout is placed next to the SingNextOS checkout,
the CI job invokes the exact qualification entry point from the SingNextOS
repository root:

```sh
bash eng/qualify-hybridcpu-aot.sh ../HybridCPU-v2
```

The checked-in script pins the expected HybridCPU revision, requires the active
SDK to equal the committed SingNextOS SDK pin, records HybridCPU's requested SDK,
performs the recorded restores followed by two clean Release build/admission
passes, and requires the kernel assembly, host boot assembly and admission proof
bytes to match before recording them. The report contains the
`ReproductionCommands` argv recipe for those steps, each artifact's SHA-256,
the embedded `AssemblyDigest` / `ProofDigest` of
`SingPlusAdmissionProofV1`, and the exact admission root and profile. The
recorder also validates that the two PE inputs are managed assemblies named
`SingPlus.Kernel` and `SingPlus.Boot`, then reruns the current
`AdmissionVerifier` against the kernel and requires its canonical proof to equal
the supplied proof byte-for-byte. It independently observes `dotnet --version`
from the exact SingNextOS worktree and refuses a supplied or committed SDK
mismatch. Generated values are
deliberately not copied into this document: the per-run report binds
them to the actual checked-out `github.sha` without creating a commit
self-reference. Because the repository has no `packages.lock.json`, matching
builds on the recorded runner and SDK are practical reproducibility evidence,
not a claim of a hermetic long-term supply-chain build.

`ReproductionCommands` is a deterministic recipe, not proof that a command ran.
The workflow log supplies that execution evidence, `SHA256SUMS` binds the emitted
files, and the recorder's independent validation binds their structure and
admission semantics. The JSON report, a digest or a successful parse is evidence
only; none is a capability, platform grant, AOT authority or ISE execution
receipt.

Successful recorder-side validation is represented explicitly without claiming
that the report itself observed process execution:

```text
Reproducibility.ComparedArtifactSets = 2
Reproducibility.Comparison           = ByteIdentical
Stages[LocalArtifacts].Outcome = Validated
Stages[LocalAdmissionProof].Outcome = Validated
Stages[LocalArtifactComparison].Outcome = Validated
```

The pinned HybridCPU-v2 source contains a canonical compiler that consumes an
already constructed `ReadOnlySpan<VLIW_Instruction>` and serializes lowered
bundles into a 256-byte-per-bundle `ProgramImage`. It does not expose a command,
SDK entry point or project that consumes an ECMA-335 SingNextOS assembly. Its
in-process `EmitProgram` path writes an already compiled VLIW image to ISE main
memory; descriptor-bearing contours may also require separately published
`VliwBundleAnnotations`, so the raw byte array is not a proven standalone image
package. This is not a managed AOT frontend or an external image loader.

The live release audit found no published prebuilt SDK/CLI/toolchain. The only
visible release is a draft `VLIW` release with no assets. No externally supplied
assembly-consuming tool was available to this qualification run. The resulting
stage record is therefore:

| Stage | Result | Evidence boundary |
|---|---|---|
| local command execution | succeeded in workflow log | The report does not self-attest that build commands ran. |
| `LocalArtifacts` | `Validated` | Both exact managed PE names, digests and byte-identical build copies are checked. |
| `LocalAdmissionProof` | `Validated` | The current verifier reruns and reproduces the supplied canonical proof. |
| `LocalArtifactComparison` | `Validated` | Two supplied artifact sets are byte-identical; only the workflow log proves they came from two clean builds. |
| `ManagedAssemblyToHybridCpuAot` | `ExternalBlocked` | Toolchain identity and invocation command are `null`; no compatible external interface is present or supplied. |
| HybridCPU image | `NotProduced` | Image path and SHA-256 digest are `null`, not a fabricated host artifact. |
| ISE load/execute | `NotAttempted` | There is no qualified image to load; this is not recorded as ISE rejection. |

The machine-readable negative fields are exactly:

```text
ToolchainIdentity = null
AotCommand        = null
ImagePath         = null
ImageDigest       = null
IseCommand        = null
IseResult         = null
```

`SingPlus.Kernel.dll` is the managed kernel candidate whose admission root is
`SingPlus.Kernel.KernelEntryPoint::Run`. `SingPlus.Boot.dll` is not a HybridCPU
boot image: it is a `User` / `ManagedGc` host smoke harness and directly depends
on `SingPlus.Kernel.Hal.Host`. Passing that harness to a guessed compiler or
renaming it as a platform image would be a false success path.

This closes the Phase-6 qualification attempt by producing a reproducible,
fail-closed result. It does not close this external requirement.

## Required external capability

A released or otherwise externally supplied HybridCPU toolchain must accept the
admitted SingNextOS managed kernel assembly and its dependency closure, lower it
to the documented HybridCPU image format, and provide a loader/execution path
that reaches the named entry point in the existing ISE.

## Why SingNextOS needs it

The local SingNextOS Definition of Done establishes architecture, manifests,
runtime authorities, ownership, IPC, HAL boundaries, admission proofs,
deterministic artifacts, tests and CI. Proving the later end-to-end path
`SingNextOS -> HybridCPU AOT -> HybridCPU image -> ISE` requires capabilities
owned by the external HybridCPU toolchain.

## Existing interface expected

The missing external interface must specify, without changing the SingNextOS
public API:

- an exact toolchain identity and obtainable binary/package digest;
- an invocation that accepts the exact managed kernel assembly and dependency
  closure with `SingPlus.Kernel.KernelEntryPoint::Run` as entry point;
- the produced image format, load address/entry mapping and image digest;
- an exact ISE loader/execution command and terminal result;
- failures classified as local admission, AOT/lowering, packaging/loading,
  runtime admission or execution.

The current HybridCPU `ProgramImage`, compiler metadata, parser acceptance or
compiler/runtime agreement evidence cannot substitute for this interface or for
runtime execution authority.

## Minimal reproduction

1. Check out the exact SingNextOS and HybridCPU-v2 revisions recorded above.
2. Run the exact local build/admission commands and retain the per-run
   `SingPlusHybridCpuQualificationV1` report.
3. Verify that the report binds the candidate assembly and admission proof to
   their SHA-256 digests.
4. Supply the released external AOT toolchain identity and its documented
   managed-assembly command. With the audited baselines this input is absent, so
   qualification stops at `ManagedAssemblyToHybridCpuAot`.
5. Only after a positive AOT result, hash the produced HybridCPU image and invoke
   the documented ISE loader/execution command.
6. Record whether the named entry point was accepted and executed. Do not infer
   this result from successful local admission or VLIW compiler tests.

No external repository modification is part of this reproduction.

## SingNextOS component blocked

Only external HybridCPU AOT/image/ISE integration qualification. No SingNextOS
architecture, runtime authority, contract, ownership, IPC, analyzer, generator,
admission, HAL, driver abstraction or local CI guarantee is blocked by this
requirement.

## Fallback/mock used

Host implementations of the SingNextOS HAL plus metadata/CIL admission tests and
local runtime/integration tests validate SingNextOS-owned guarantees. The host
boot smoke harness and HybridCPU's VLIW-input compiler/runtime tests are retained
as separate evidence classes; neither is reported as managed AOT, a produced
HybridCPU image or ISE execution of SingNextOS.
