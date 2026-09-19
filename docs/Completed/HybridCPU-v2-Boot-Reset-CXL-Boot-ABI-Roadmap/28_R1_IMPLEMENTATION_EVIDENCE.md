# R1 Implementation Evidence — Reset / Memory Map Adapter Profile

Baseline HEAD remained `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; the complete dirty status was captured before R1 and preserved. R0 artifacts and all unrelated user changes remained intact.

## Classification and scope

- `ExecutableInScope`: adapter-only deterministic reset snapshot, physical-map range/permission model, reset-generation staleness, and direct-core dependency guard.
- `ModelOnly`: PC=`0x00000000FFFC0000`, zero-register, interrupt-mask, empty pipeline/replay/retire, quiesced external-effect, and parked-secondary assertions.
- `FutureGatedRequiresCore`: actual architectural PC/register initialization, pipeline/replay/memory completion quiesce, frontend ROM fetch, and reset atomicity. Owner: HybridCPU core/reset owner. Missing prerequisite: approved reset hook and executable core backend.
- `FutureGatedRequiresHardware`: electrical/link/decoder reset retention. Owner: platform/SoC backend.

## Implementation

`ResetAndMemoryMapModel.cs` defines the reference v1 constants, an overflow/overlap/range checked physical map, immutable-ROM read/execute permissions, a deterministic reset snapshot for each modeled cause, and a reset-generation check. The types are internal adapter model types so they do not enlarge the adapter public authority surface.

No snapshot, address, or reset generation is an OS capability. Physical link preservation is not modeled as authority preservation. A later reset invalidates every earlier model generation.

## Tests and actual results

- Focused R1 tests: passed 8, failed 0.
- Adapter regression: passed 30, failed 0. An initial regression detected that public model names widened the existing adapter public surface; the model types were made internal and the regression then passed.
- Adapter build: succeeded, 0 warnings, 0 errors.
- Full solution build: succeeded, 0 warnings, 0 errors.
- Full solution tests: adapter 30/0; neutral runtime 58/0; platform 60/0; main 1200 passed/0 failed/2 intentionally skipped.
- `git diff --check`: exit 0 with line-ending notices only.

Claim level: `ModelValidated`. This is not evidence that HybridCPU resets registers, fetches ROM, or quiesces silicon/runtime pipelines.

Changed production file: `tools/HybridCpu_ExecutableAdapter/Boot/ResetAndMemoryMapModel.cs`. Changed test file: `tools/HybridCpu_ExecutableAdapter.Tests/ResetMemoryMapModelTests.cs`.

No HybridCPU core/ISE/ISA/opcode/compiler/scheduler/runtime legality/fetch/pipeline/replay/memory-controller/retire/physical-register file was modified.
