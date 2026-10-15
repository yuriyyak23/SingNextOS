# R2 Implementation Evidence — Stage-0 / Local Recovery Model

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty worktree captured and preserved before the phase.

Classification: the deterministic host-side Stage-0 state machine, ROM size/dependency validator, local recovery copy/hash/entry checks, bounded trace, and semantic operation-ordinal fault injector are `ModelOnly` and executable in the adapter test harness. Real ROM fetch/instruction execution is `FutureGatedRequiresCore` (owner: HybridCPU frontend/core; missing contract: approved ROM fetch/reset execution backend). A physical immutable ROM/local flash backend is `FutureGatedRequiresHardware`.

The model accepts only a 256 KiB ROM envelope, a 128 KiB executable/read-only budget, at most 16 allowlisted dependencies, an 8 MiB recovery payload, checked RAM/entry ranges, 256-byte entry alignment, and a maximum operation budget. It hashes destination RAM bytes with SHA-384 after copy. Any partial-copy fault clears and withholds the destination; no failure result contains executable bytes. Faults address semantic operation ordinals, never wall-clock timing.

No boot identity or physical address is authority; the result is model evidence only. No Region/SIP/provider/capability object is produced.

Qualification:

- Focused R2: 4 passed, 0 failed.
- Adapter regression: 34 passed, 0 failed.
- Full solution build: succeeded, 0 warnings, 0 errors.
- Full solution tests: adapter 34/0; neutral runtime 58/0; platform 60/0; main 1200 passed/0 failed/2 intentionally skipped.
- `git diff --check`: exit 0 with line-ending notices only.

Claim level: `ModelValidated`; specifically not real Stage-0/ROM/HybridCPU execution.

Changed files: `tools/HybridCpu_ExecutableAdapter/Boot/Stage0RecoveryModel.cs`, `tools/HybridCpu_ExecutableAdapter.Tests/Stage0RecoveryModelTests.cs`.

No HybridCPU core/ISE/ISA/compiler/scheduler/fetch/pipeline/replay/memory-controller/retire/register implementation changed.
