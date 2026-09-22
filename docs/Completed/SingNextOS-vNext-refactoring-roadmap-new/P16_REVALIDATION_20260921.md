# P16 revalidation — executable references and evidence integrity

## Provenance and disposition

Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`. Entry HEAD: `1890a8e921cfe903b5b44857e4661168bf7bbceb`; entry `git status --short` was empty. The preceding P12-P16 changes arrived in the external commit `vnext upd4`; this iteration did not create that commit. Earlier evidence and tuples remain historical records. The additive tuple pins this iteration's reviewed sources and tests by their actual byte hashes, independently of later Git commits.

P16 remains **OPEN**. Passing metadata validation is `StaticAdmission` for that validator, not proof that every VNX invariant, required lane or prior phase is complete. P17 has not started. No feature gate is enabled.

## Confirmed defects and corrections

1. Traceability named `VNextPhase01ResourceAlgebraTests`, `VNextPhase08ComputePlanTests` and `HybridCpuExecutableAdapterContractTests`, none of which exists locally. The old test accepted any non-empty test name. A new reference-resolution regression failed against the old data. Every current reference now resolves to a fully qualified xUnit method, including theories.
2. Markdown synchronization previously checked only invariant IDs. It accepted different phases, owners, tests, claims and exclusions. The validator now checks the full generated projection, allowing only line-ending and terminal-newline differences.
3. Tuple/evidence references previously needed only to exist. Rows now bind both hashes, baseline and phase. The additive tuple also hashes the implementation and test sources, local package and CI entry point. Mutation tests cover changed hashes/lengths, missing and duplicate inputs, malformed/null/unknown fields and invalid paths.
4. Claim wording exceeded some test scopes. The current coverage index records the SipJob resource barrier classifier as `StaticAdmission`, sibling declarative algebra as `ModelOnly`, and the CPU legality test as a host seam with a test implementation. These are not an executed fused-resource differential contour, complete live sibling algebra, or real HybridCPU legality proof.

The JSON schema is now version 2, rejects unknown/duplicate fields and IDs, and names an executable local CI entry point: `eng/qualify-vnext.ps1`. This entry point uses `--no-restore`, runs the focused lane and mandatory full non-GUI suite, and fails if either fails. No hosted-CI execution is claimed.

## Requirement disposition

| P16 requirement | Current disposition |
|---|---|
| Exact source/package/runtime/test tuple | Local reviewed inputs are byte-pinned; HybridCPU source SHA is unverified |
| CI evidence validation and claim negatives | Implemented with falsification tests; execution recorded below |
| Unit/property/race/restart/fault/ambiguity | Existing host tests are explicitly referenced; full coverage remains subject to their contour exclusions |
| Resource-aware ordinary/fused differential | Incomplete; a barrier classifier cannot prove authoritative trace equivalence |
| Provider adapter resource contour | FutureGated; no exact local executable resource contract/usage evidence seam |
| Temporal upper bound/guarantee | FutureGated; no measurement/preemption/minimum-capacity proof |
| Performance and contention across thread/domain/SMT/provider counts | Partial: `P16_RESOURCE_BUDGET_PERFORMANCE_20260922.md` executes host/JIT thread and process-domain counts plus quarantine pressure; controlled SMT and provider counts remain absent |
| Gate rollback smoke tests | Default-OFF checks exist; OFF is not evidence of an ON-to-OFF rollback under live work |
| Durable restart/quarantine | FutureGated; existing in-process/component tests do not prove crash-journal recovery |
| Production qualification | Withheld; required contours and full-suite closure remain incomplete |

All VNX-001 through VNX-028 rows were rechecked for referenced owner/source/test existence and precise evidence scope. This is a traceability audit, not a replacement semantic audit of every owner. Focused implementation behavior comes only from the named executed tests. Evidence/receipt/plan/cache/telemetry remain non-authoritative; the validator has no runtime owner reference or transition API.

## Local provider artifact

At this P16 revalidation, `.packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg` is locally present (74244 bytes), SHA-256 `b96e99bda066ee585b26a11cbfa7b68ce6bf44fc0006679483ccc1a4eeb678c2`. This re-pins the artifact only. Roadmap HybridCPU SHA `794c4a53494f503855ac8cf209efab23fde083b2` is not locally verified. No network, remote Git or remote package operation was used.

## Qualification results

Results are populated from executed commands in the additive tuple and the final verification below. The deliberately failing old-reference regression is separate from final qualification. Initial policy-development runs found and corrected exception handling and terminal-newline comparison in the new validator; they are not production defects.

Executed `.\eng\qualify-vnext.ps1` at the recorded HEAD plus this iteration's dirty files:

```text
dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  Exit 0; 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~VNext|FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~Phase06DeterministicTracingTests|FullyQualifiedName~HybridCpuExternalOperationProviderTests" --verbosity minimal
  Exit 0; Passed 146, Failed 0, Skipped 0

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Exit 1; Passed 1688, Failed 9, Skipped 2
  SingPlus.Tests: 1480 passed, 9 failed, 2 skipped
  Other projects: 90 + 58 + 60 passed

git diff --check
  Exit 0; LF/CRLF informational warnings only
```

The CI entry point exited 1 on the full-suite failure, as required. Eight failures open old historical roadmap paths; every referenced JSON was found under `docs/Completed`. The ninth is security-profile inventory drift: `tools/SingPlus.Boot.DebugHost/SingPlus.Boot.DebugHost.csproj` has no inventory row. Their exact test IDs are recorded in the additive tuple. The SipJob failure is now a missing old path after the concurrent move, not the earlier HEAD mismatch. No failed test, old consumer or historical artifact was changed, skipped or reclassified as a pass.

After recording final results and refreshing the manifest, `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~VNextPhase16QualificationClosureTests|FullyQualifiedName~VNextPhase00ArchitecturalFreezeTests" --verbosity minimal` passed 40/40 (zero failures/skips). This checks all 28 current rows, all manifest entries and all 43 source/test/package artifacts in the additive tuple.

Concurrent changes observed after the clean entry: 28 deletions under the former SipJob roadmap path plus its new `docs/Completed` directory, and a new semantic co-design documentation directory. They are recorded in `observedDirtyWorktree` and remain owned by the parallel work.

## Remaining boundaries

The exact missing owners/evidence remain: provider/runtime adapter for resource usage reconciliation and provider-count performance; runtime/provider enforcement for temporal guarantees and throughput/occupancy; an execution environment that can control and report SMT topology; existing budget/checkpoint owners plus authenticated durable journal for cold-process restart; SipJob/ordinary owners for fused-resource equivalence; gate/configuration owners for live ON-to-OFF rollback. Bypassing any gap would turn static or host evidence into a stronger claim.

Ordinary SIP/Compute/ExternalOperation production code is unchanged by this iteration. No production readiness, NativeAOT, realtime guarantee, guaranteed capacity, confidential execution, hardware acceleration, QEMU, firmware or CXL boot claim is made. No HybridCPU ISA or microarchitecture work was performed.

## Subsequent remediation — 2026-09-22

`P16_FULL_SUITE_REMEDIATION_20260922.md` records the additive correction of all nine failures above. Consumers now resolve the parallel-moved artifacts under `docs/Completed`, the DebugHost project has an explicit `BuildTool/TestOnly` inventory row, and the historical SipJob tuple is no longer compared with current HEAD or current mutable build-output hashes. The final canonical lane passed 1703 tests with zero failures and two unchanged explicit skips. This supersedes only the earlier full-suite failure status; all contour gaps and claim exclusions remain.
