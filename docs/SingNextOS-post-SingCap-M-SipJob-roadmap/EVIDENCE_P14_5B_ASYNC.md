# P14-5B evidence — async containment admission

## Repeat-audit remediation (2026-09-20)

The repeat audit re-captured baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c` and preserved the complete pre-existing dirty worktree. It found that `ClosedValue` accepted the prefix-only identity `schema:`. That string did not identify any exact closed value contract, so it could not support the claimed closed-world suspended-state admission. The verifier now requires a nonempty ordinal suffix after `schema:`; `ClosedValueRequiresAnExactNonemptySchemaIdentity` covers prefix-only, open `object`, and wrong-domain `region:` identities.

A subsequent malformed-descriptor pass found that a `null` entry in `Values` was dereferenced during version validation. The verifier now rejects it as `Malformed` before version, kind, schema, or owner evaluation; `NullSuspendedValueFailsClosedBeforeVersionOrKindEvaluation` is the regression test. Accepted state kinds and owners are unchanged.

Focused containment, ordinary cancellation, P14-2 direct-contour, default-gate, and closure tests passed 29/29. Runtime and full solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1338 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58. `FG-ASYNC-STAGE` remains OFF, and no executable async continuation contour is claimed.

Disposition: `StaticAdmission` for suspended-state containment; executable async Job contour is `FutureGatedRequiresCore`. `FG-ASYNC-STAGE` remains OFF.

Baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c`; Windows x64 JIT; SDK `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`. HEAD/status were captured before the phase and the dirty worktree was preserved.

Live generated runtime sentries return synchronous `GeneratedSipSentryResult<T>`. There is no generated TCB-private async continuation contract capable of keeping a service-created Task/ValueTask/awaiter/state machine inside the sentry while exposing only closed completion state. Enabling async Job execution would therefore lack the required containment proof.

Added a closed version-1 `SipJobAsyncSuspensionVerifier`. Accepted state is limited to closed schema values and opaque identities naming the existing invocation, cancellation, session, Region, and capability lifecycle owners. Exact gates are `FG-JOB-LINEAR` plus `FG-ASYNC-STAGE`. Unknown version/gate/kind/owner, duplicate IDs, and non-canonical values fail closed. The descriptor/result surface contains no Task, ValueTask, awaiter, delegate, service locator, implementation reference, raw Region backing, permission result, or authority-bearing handle.

Executed evidence:

- focused containment, ordinary cancellation, and P14-2 direct-contour regressions: 17 passed, 0 failed;
- Runtime build and full solution build: both succeeded with 0 warnings and 0 errors;
- full non-GUI suite: main assembly 1301 passed, 8 unchanged unrelated failures, 2 skipped (1311 total); other assemblies 90/90, 58/58, and 60/60 passed. The failures remain seven absent historical JSON artifacts and one pre-existing security-profile inventory mismatch; no P14 test failed;
- architecture/default-gate policy: 7 passed, 0 failed;
- changed P14 JSON parsed successfully; `git diff --check` exited 0 with only LF-to-CRLF notices and no whitespace error.

FutureGated owner: SIP generator plus runtime sentry/invocation owners. Missing prerequisite/evidence: generated async sentry continuation ABI; proof that service state machines never enter Job state; heap-safe lease/use lifetime across suspension; cancel-before-start, pre-suspend, suspended, completion race, session close/restart, Region reclaim, provider completion-versus-visibility tests; and exact runtime-mode qualification. NativeAOT remains independently FutureGated and cannot inherit the JIT claim.

Ordinary SIP remains the only async runtime path and semantic oracle. Plans/handles/caches/suspension metadata confer no authority. No raw ManagedCap reference crosses a Job boundary. No HybridCPU core/ISE/ISA/compiler/scheduler-legality/microarchitecture code changed; QEMU, firmware, CXL boot, hardware, NativeAOT, and acceleration remain unclaimed.
