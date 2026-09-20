# P14-5D evidence — parallel DAG eligibility

## Repeat-audit remediation (2026-09-20)

The repeat audit revalidated baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c` and preserved the complete pre-existing dirty worktree. It found that `AvailableProcessorCount` was trusted from the descriptor itself, so a caller could inflate the recorded topology and make an otherwise unsupported worker count appear eligible. The verifier now requires an exact match with `Environment.ProcessorCount` before applying the closed worker matrix. `CallerCannotInflateRecordedProcessorTopology` is the regression test. This is still static eligibility only: it grants no authority and introduces no worker scheduler.

A later repeat pass found that worker `ClosedStateSchemaId` used only a `schema:` prefix test. Prefix-only, trailing-whitespace, open-object, and wrong-domain identities could therefore enter otherwise authority-free worker metadata. The verifier now requires a canonical nonempty `schema:` identity. `WorkerClosedStateRequiresCanonicalNonemptySchemaIdentity` covers all four negative forms. No executable scheduler, authority carrier, or new gate was added.

A further malformed-input sweep found that default `ImmutableArray` work-item/gate collections and null work-item entries could throw during enumeration instead of producing a closed verifier disposition. The verifier now rejects default work items and null entries as `MalformedWorkItem`, and a default gate set as `UnsupportedGate`, before version/topology/schema analysis. `DefaultCollectionsAndNullWorkerItemsFailClosedBeforeTopologyAnalysis` is the regression test; it adds no executable scheduler or authority path.

Post-remediation focused P14-5D/P14-5C/P14-0/P14-8 tests passed 29/29. Runtime and full solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1340 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58. JSON parsing and `git diff --check` succeeded.

Disposition: `StaticAdmission`; executable parallel scheduler is `FutureGatedRequiresCore`; `FG-DAG-PARALLEL` remains OFF.

Baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c`; Windows x64 JIT; SDK `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`. HEAD/status were captured before the phase and the existing dirty worktree was preserved. Actual logical processor count reported by `[Environment]::ProcessorCount` was 16.

Added a closed version-1 eligibility verifier for branch counts 2/4/8 and requested workers 1/2/4/8/16/32. A tuple is eligible only when workers do not exceed the recorded available processor count, branch work-item count is exact, and the exact prerequisite gate vocabulary is declared. On this host the static matrix admits worker counts 1/2/4/8/16 and rejects 32.

Worker-item metadata contains only semantic stage ID, opaque invocation correlation ID, and closed-state schema ID. It contains no capability, authority lease, service/implementation reference, delegate, mutable object payload, provider handle, lane/opcode, or physical scheduling selector. Unknown version/gate/topology and duplicate stage/correlation IDs fail closed. Stage ordering is deterministic and inherits P14-5C join semantics.

Executed evidence:

- focused P14-5C/P14-5D tests: 8 passed, 0 failed;
- Runtime and full solution builds: 0 warnings, 0 errors;
- full non-GUI suite: main assembly 1309 passed, 8 unchanged unrelated failures, 2 skipped (1319 total); other assemblies 90/90, 58/58, and 60/60 passed. The failures remain seven absent historical JSON artifacts and one pre-existing security-profile inventory mismatch; no P14 test failed;
- architecture/default-gate policy: 7 passed, 0 failed;
- changed JSON parsed; `git diff --check` exited 0 with only LF-to-CRLF notices.

Exact claim is static topology/work-item eligibility only, not scheduler execution or performance. FutureGated owner/prerequisite: DAG runtime and scheduler owners. Missing evidence: qualified serial P14-5C executor; branch admission through generated sentries; no authority consumption for unstarted branches; shared-use lifetime domination; cleanup under fault/cancel/restart/reclaim; property tests across actual schedules; and performance characterization. Worker 32 was not executed because this environment reports 16 processors. Shared mutable DAG remains FutureGated.

Ordinary SIP remains fallback/oracle; workers do not receive ambient authority because no worker runtime was introduced. Metadata does not confer authority. No HybridCPU core/ISE/ISA/compiler/scheduler-legality/microarchitecture code changed; QEMU, firmware, CXL boot, hardware, NativeAOT, acceleration, and production readiness remain unclaimed.
