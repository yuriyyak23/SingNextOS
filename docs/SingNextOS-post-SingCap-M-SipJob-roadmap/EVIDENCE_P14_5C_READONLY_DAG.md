# P14-5C evidence — deterministic read-only DAG admission

## Repeat-audit remediation (2026-09-20)

The repeat audit revalidated baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c` and preserved the complete pre-existing dirty worktree. It found that the verifier rejected MOVE only when a producer had multiple outgoing edges; a single linear MOVE edge could therefore be admitted despite this evidence's claim that the P14-5C contour accepts only closed-copy and read-only-BORROW edges. The verifier now rejects every MOVE edge in this contour, with an explicit message that linear MOVE remains separately qualified under P14-3. `SingleLinearMoveIsOutsideReadOnlyDagContour` is the regression test. No gate or authority owner changed.

The same pass found that edge uniqueness covered only `EdgeId`, permitting duplicate semantic `(from,to)` dependencies under different IDs. Endpoint pairs are now unique as well; `DuplicateSemanticEdgeWithDifferentIdentityFailsClosed` proves rejection before topology/lifetime use.

The closed-value pass also found that `SchemaId` was only checked for nonblank canonical text. That let a `ClosedCopy` claim an open `object`-like identity and let a `ReadOnlyBorrow` claim a non-Region schema. Edge-kind/schema pairing is now closed: copied values require a nonempty `schema:` identity and BORROW requires a nonempty `region:` identity. `EdgeKindCannotClaimAnOpenOrMismatchedSchema` covers open, empty-suffix, and cross-kind identities. This is descriptor admission only; neither prefix creates authority or substitutes owner revalidation.

A subsequent malformed-descriptor pass found that default `ImmutableArray` collections and `null` node/edge entries could be enumerated or dereferenced before a typed verifier result. The verifier now returns `Malformed` before version, identity, topology, or lifetime analysis. `DefaultCollectionsAndNullGraphEntriesFailClosedBeforeTopologyAnalysis` covers default nodes and null node/edge entries. The accepted DAG contour is unchanged.

Final post-remediation focused P14-5C/P14-5D/P14-0/P14-8 tests passed 28/28. Runtime and full solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1339 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58. JSON parsing and `git diff --check` succeeded.

Disposition: `StaticAdmission`; `FG-READONLY-DAG` remains OFF. No DAG executor is enabled.

Baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c`; Windows x64 JIT; SDK `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`. HEAD/status were captured before the phase and existing dirty state was preserved.

Added a closed version-1 bounded DAG verifier (2..8 stages) with a single logical entry, canonical unique nodes/edges, complete reachability, deterministic ordinal topological order, and the sole join policy `AllSuccessByStageOrder`. Only closed-copy and read-only-BORROW edges are admitted. BORROW fan-out must name one common dominating lifetime scope. MOVE fan-out, every mutable edge, unknown edge/join/version/gate, cycles, orphans, and lifetime mismatch fail closed.

Join fault/cancel selection is a pure function of semantic stage ID and outcome class, independent of completion/worker order: the ordinal-first fault wins, otherwise ordinal-first cancellation, otherwise success. It accepts no callback/delegate and carries no authority.

Executed evidence:

- focused verifier, linear-plan, and owner-backed Region BORROW regressions: 52 passed, 0 failed;
- Runtime and full solution builds: 0 warnings, 0 errors;
- full non-GUI suite: main assembly 1306 passed, 8 unchanged unrelated failures, 2 skipped (1316 total); other assemblies 90/90, 58/58, and 60/60 passed. The failures remain seven absent historical JSON artifacts and one pre-existing security-profile inventory mismatch; no P14 test failed;
- architecture/default-gate policy: 7 passed, 0 failed;
- changed JSON parsed; `git diff --check` exited 0 with only LF-to-CRLF notices.

Exact claim is static DAG admission and deterministic join selection only. FutureGated owner/prerequisite: runtime/generated-sentry and Region owners; missing evidence includes a serial executor entering every branch through generated sentries, one shared owner-issued read-use whose lifetime dominates all dependent branches, exact branch settlement/cleanup, cancellation/fault/reclaim properties, and ordinary-versus-DAG traces. Shared-mutable DAG remains `FutureGated`; MOVE fan-out is rejected.

Ordinary SIP remains fallback/oracle. DAG metadata and join results are not authority. No raw ManagedCap references cross the boundary. No HybridCPU core/ISE/ISA/compiler/scheduler-legality/microarchitecture code changed; QEMU, firmware, CXL boot, hardware, NativeAOT, acceleration, and production readiness remain unclaimed.
