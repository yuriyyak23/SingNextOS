# P14-5C evidence — deterministic read-only DAG admission

## Repeat-audit remediation (2026-09-20)

The repeat audit revalidated baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c` and preserved the complete pre-existing dirty worktree. It found that the verifier rejected MOVE only when a producer had multiple outgoing edges; a single linear MOVE edge could therefore be admitted despite this evidence's claim that the P14-5C contour accepts only closed-copy and read-only-BORROW edges. The verifier now rejects every MOVE edge in this contour, with an explicit message that linear MOVE remains separately qualified under P14-3. `SingleLinearMoveIsOutsideReadOnlyDagContour` is the regression test. No gate or authority owner changed.

The same pass found that edge uniqueness covered only `EdgeId`, permitting duplicate semantic `(from,to)` dependencies under different IDs. Endpoint pairs are now unique as well; `DuplicateSemanticEdgeWithDifferentIdentityFailsClosed` proves rejection before topology/lifetime use.

The closed-value pass also found that `SchemaId` was only checked for nonblank canonical text. That let a `ClosedCopy` claim an open `object`-like identity and let a `ReadOnlyBorrow` claim a non-Region schema. Edge-kind/schema pairing is now closed: copied values require a nonempty `schema:` identity and BORROW requires a nonempty `region:` identity. `EdgeKindCannotClaimAnOpenOrMismatchedSchema` covers open, empty-suffix, and cross-kind identities. This is descriptor admission only; neither prefix creates authority or substitutes owner revalidation.

A subsequent malformed-descriptor pass found that default `ImmutableArray` collections and `null` node/edge entries could be enumerated or dereferenced before a typed verifier result. The verifier now returns `Malformed` before version, identity, topology, or lifetime analysis. `DefaultCollectionsAndNullGraphEntriesFailClosedBeforeTopologyAnalysis` covers default nodes and null node/edge entries. The accepted DAG contour is unchanged.

Final post-remediation focused P14-5C/P14-5D/P14-0/P14-8 tests passed 28/28. Runtime and full solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1339 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58. JSON parsing and `git diff --check` succeeded.

The strict ordered re-audit at qualification HEAD `6227ea7cf258ef6ffce52001d4d2ffee07355b35` found a further fail-open join defect. `Join` accepted an empty outcome collection, duplicate/non-canonical stage outcomes, and undefined `SipJobDagOutcomeKind` values; an undefined outcome could therefore be ignored and returned as apparent success. It also accepted an arbitrary lazy `IEnumerable`, which was wider than the closed deterministic join contract. The join input is now an immutable outcome array, validates nonempty canonical unique stage identities and the closed outcome enum, and returns a typed `SipJobDagJoinError` before result selection. `MalformedOrUnknownJoinOutcomesCannotBecomeSuccess` covers default/empty, duplicate, non-canonical and unknown-kind inputs. This is fail-closed static admission; it adds no executor, callback, authority or scheduling behavior.

Post-fix focused P14-5C/P14-5D/P14-0/P14-8 tests passed 30/30. Direct DAG/Region/sentry regressions passed 51/51. Runtime and full solution builds both succeeded with 0 warnings and 0 errors. The exact full non-GUI command recorded 1351 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90 and 58/58. The first closure run correctly rejected the stale Runtime DLL hash after recompilation; the tuple was refreshed from the locally built artifact to `C42F2D43BD8CE11C14136761B9C7907454632ED0CAE510ECC1CEBD7655AD7DCA`, after which closure passed. Existing dirty state was preserved and no reset, checkout, clean, commit, push, remote operation or user-file deletion occurred.

Disposition: `StaticAdmission`; `FG-READONLY-DAG` remains OFF. No DAG executor is enabled.

Baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c`; Windows x64 JIT; SDK `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`. HEAD/status were captured before the phase and existing dirty state was preserved.

Added a closed version-1 bounded DAG verifier (2..8 stages) with a single logical entry, canonical unique nodes/edges, complete reachability, deterministic ordinal topological order, and the sole join policy `AllSuccessByStageOrder`. Only closed-copy and read-only-BORROW edges are admitted. BORROW fan-out must name one common dominating lifetime scope. MOVE fan-out, every mutable edge, unknown edge/join/version/gate, cycles, orphans, and lifetime mismatch fail closed.

Join fault/cancel selection is a pure function of a validated immutable set of canonical unique semantic stage outcomes, independent of completion/worker order: the ordinal-first fault wins, otherwise ordinal-first cancellation, otherwise success. Empty, duplicate, non-canonical or unknown outcome inputs fail with a typed join error. It accepts no callback/delegate or lazy policy enumerable and carries no authority.

Executed evidence:

- focused verifier, linear-plan, and owner-backed Region BORROW regressions: 52 passed, 0 failed;
- Runtime and full solution builds: 0 warnings, 0 errors;
- full non-GUI suite: main assembly 1306 passed, 8 unchanged unrelated failures, 2 skipped (1316 total); other assemblies 90/90, 58/58, and 60/60 passed. The failures remain seven absent historical JSON artifacts and one pre-existing security-profile inventory mismatch; no P14 test failed;
- architecture/default-gate policy: 7 passed, 0 failed;
- changed JSON parsed; `git diff --check` exited 0 with only LF-to-CRLF notices.

Exact claim is static DAG admission and deterministic join selection only. FutureGated owner/prerequisite: runtime/generated-sentry and Region owners; missing evidence includes a serial executor entering every branch through generated sentries, one shared owner-issued read-use whose lifetime dominates all dependent branches, exact branch settlement/cleanup, cancellation/fault/reclaim properties, and ordinary-versus-DAG traces. Shared-mutable DAG remains `FutureGated`; MOVE fan-out is rejected.

Ordinary SIP remains fallback/oracle. DAG metadata and join results are not authority. No raw ManagedCap references cross the boundary. No HybridCPU core/ISE/ISA/compiler/scheduler-legality/microarchitecture code changed; QEMU, firmware, CXL boot, hardware, NativeAOT, acceleration, and production readiness remain unclaimed.
