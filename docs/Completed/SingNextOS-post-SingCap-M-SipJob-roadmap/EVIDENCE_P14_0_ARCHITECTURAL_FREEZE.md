# P14-0 executable evidence — architectural freeze

## Repeat-audit note (2026-09-20)

The latest strict-cycle snapshot recorded current qualification HEAD `6227ea7cf258ef6ffce52001d4d2ffee07355b35` while retaining `52ccf45c05498a9143a599bb54499919a2cbcf8c` as the audit baseline. The intervening `386e247e37f4e252d1bf4fda80a57b49858fb788` contains the audited SipJob remediation and `6227ea7cf258ef6ffce52001d4d2ffee07355b35` adds only the separate vNext roadmap. The remaining dirty P14-7/P14-8 changes were preserved. A repeat gate-contract sweep found that failed `TryResolve` calls returned enum default `Linear` through the out parameter even though the boolean result was false. `IsEnabled` was not bypassed, but this was an avoidable valid-contour ambiguity for a buggy consumer. Failure now returns an undefined sentinel; all null/empty/unknown/case/whitespace inputs prove both resolution failure and absence of any defined gate value.

Post-remediation P14-0/P14-8 tests passed 8/8. Runtime and full solution builds succeeded with 0 warnings and 0 errors. The full non-GUI suite recorded 1347 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58. All production gates remain OFF and there is no privileged production Job executor.

The repeat audit started at baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c` with an already dirty worktree. The pre-existing changes comprised the modified generator/runtime/test files and the untracked SipJob runtime, SDK, tests, evidence, traceability, and tuple files reported by `git status --short`; they are user-owned parallel work and were preserved. Accordingly, the historical statement below that the worktree was empty describes the start of the original P14 implementation pass only and is not the state at the start of this repeat audit. Later-phase SipJob metadata and test-only/owner-primitive contours now exist in that dirty worktree, so the original P14-0 sentence saying that no plan/cache contour exists must likewise be read as historical, not as a current-tree inventory.

Current revalidation executed `dotnet --info`, the P14-0 focused tests, both roadmap JSON parsers, artifact hashing, and `git diff --check`. The tuple remains Windows x64 JIT with SDK `11.0.100-rc.1.26425.128` and runtime `11.0.0-rc.1.26425.128`; all seven P14-0 tests passed; both JSON files parsed; and `git diff --check` exited successfully with line-ending conversion notices only. The initial seven artifact hashes matched before the later P14-1 remediation; the final runtime hash is superseded by the updated qualification tuple and P14-8 evidence. No production SipJob feature gate is enabled, and there is still no privileged production Job executor. Plans, handles, traces, caches, and tuple metadata remain non-authoritative; ordinary SIP remains the executable fallback and semantic oracle.

## Disposition

P14-0 is complete at `ModelOnly` for the exact tuple in `P14_QUALIFICATION_TUPLE.json`. No SipJob executor, fused dispatch, cache, plan, handle, provider hint, or authority path exists. Every P14 gate is default OFF and unknown/non-canonical names fail closed.

Baseline HEAD was `52ccf45c05498a9143a599bb54499919a2cbcf8c`. `git status --short` was empty before P14-0; therefore all files listed below are P14-0 changes rather than pre-existing worktree changes. The roadmap snapshot was `0f152a5502c58546eb0458b4be3e7cbce6c5ef3e`.

## Exact tuple and drift

The executed runtime is Windows x64 JIT with .NET SDK `11.0.100-rc.1.26425.128` and runtime `11.0.0-rc.1.26425.128`. NativeAOT was not executed. The local package lock pins `HybridCPU.ExternalRuntime.Contracts` `1.14.0` with the content hash recorded in the tuple; no independent HybridCPU-v2 source checkout or hardware/provider execution was available, so provider execution is unqualified.

`git diff --name-status 0f152a…HEAD` over the permitted code/test areas returned no production source changes; the delta was confined to the SipJob roadmap documents and deletion of their historical checksum list. Classification: normative roadmap edits are `SecurityRelevantButCompatible`; live authority/runtime semantics have `NoSemanticImpact` relative to that snapshot. The missing independently pinned HybridCPU source is `RequiresRequalification` for P14-7 acceleration only, and does not block the managed P14-0 model. No `BlocksP14` delta was found for P14-0.

## Reviewed live owners and ordinary linearizations

| Semantic fact | Live owner/source | Completed linearization |
|---|---|---|
| process generation | `ProcessRegistry.Resolve`, `src/Runtime/SingPlus.Runtime/Processes/ProcessRegistry.cs:29` | exact live process generation is resolved or stale fails |
| session prepare/pin | `EndpointSessionRegistry.AcquirePin`, line 132 | active exact session and parties are checked before pin count increments |
| session final validation | `EndpointSessionRegistry.RevalidatePin`, line 150 | exact active record and nonzero pin are rechecked |
| session close/release | `EndpointSessionRegistry.ReleasePin`, line 160 | final pin release completes a requested draining close |
| authority commit | `CapabilityAuthority.AcquireOperationAuthority`, line 420 | quota is consumed and one-shot transitions to Consumed while owner lock is held |
| authority release | `CapabilityAuthority.ReleaseOperationAuthority`, line 475 | only the lease ID is removed; quota/one-shot is not restored |
| composed effect admission | `RuntimeKernel.EffectAdmission`, lines 31-55 | session pin → capability commit → session revalidate; failures release pins/lease IDs without fabricated compensation |
| sealed object | `SealedObjectAuthority.AcquirePin/Revalidate/Release`, lines 98/117/166 | exact service/session/object generation and active state own liveness |
| Region BORROW/use | `RegionAuthority.ValidateBorrowLease/AcquireBorrowUse/ReturnLoan`, lines 190/490/257 | owner, borrower, Region and borrow generations remain owner-controlled |
| Region MOVE | `RegionAuthority.Transfer`, line 334 | validates owner/state, rejects active uses, increments generation once, installs target owner |
| request MOVE | `ChannelRegistry`, lines 175-192 and 243-272 | transfer occurs sender→receiver during ordinary send materialization |
| response MOVE/publication | `ResponseRegistry.Publish`, lines 181-235 | response payload validates, Region transfers responder→requester, then response completes |
| invocation/cancellation/publication | `EndpointSessionInvocationRegistry`, lines 93-277 | registry owns request, acceptance, cancellation and terminal publication state |
| response waiter | `ResponseRegistry.WaitAsync`, line 289 | a correlated waiter is single-owner and completion removes materialized response state |
| external effect lifecycle | `ExternalOperationAuthority`, lines 27-349 | Prepared→Admitted→Submitted→DeviceComplete→Visible→Published→Released remain distinct |
| platform/provider admission | `PlatformAuthorityBridge*` and `RuntimeKernel.Platform*` | local authority and provider legality remain separate; no Job mapping exists |

Ordinary ownership response materializes the intermediate caller ownership state. A future fused A→B composition therefore cannot replace responder→caller→receiver with direct A→B absent a separately versioned contract.

## Authority map for proposed fields

| Field class | Visible/reference-bearing | Live validator and revalidation | Cache rule / why not authority |
|---|---|---|---|
| plan/stage/edge IDs and versions | ManagedCap-visible, value only | verifier; owner again at each run | canonical metadata only; possession grants nothing |
| process/service/session identities | opaque values | `ProcessRegistry` / `EndpointSessionRegistry` every run | route to owner, never cache success |
| capability identity/lineage/generation | opaque value | `CapabilityAuthority` at stage commit | may locate record only; revoke/quota/one-shot stay live |
| seal identity/generation | opaque value | `SealedObjectAuthority` pin/revalidate | no object reference in plan/frame/cache |
| Region/borrow/use identities | opaque value | `RegionAuthority` for every use/transfer/settlement | no cached owner field is authoritative |
| contract/schema/thunk digest | value metadata | admission/verifier and finite generated catalog | selects a precompiled route, never permission |
| barrier/execution-class hint | closed value | conservative verifier/platform bridge | unknown fails closed; hint is not placement authority |
| Job handle/digest/cache key | opaque/value metadata | all applicable owners revalidate each run | lookup convenience only; never stores `authorized=true` |
| TCB binding | not ManagedCap-visible, may reference implementation | trusted runtime/catalog | must never enter plan/frame/edge/projected cache state |
| trace/completion summary | diagnostic value only | no validation authority | historical evidence cannot authorize replay/effect |

## Test-only trace vocabulary

`tests/SingPlus.Tests/SipJobs/SipJobSemanticTraceVocabulary.cs` defines completed owner-event classes only. Its event payload is restricted to enum, opaque string correlation, generation and outcome; the reflection test proves this shape contains no authority/runtime handles. It has no callback, admission decision, replay permission, or production owner role. Future differential tests must populate it after owner calls complete, never for speculative intent.

## Defects and remediation

The repository had no P14 gate implementation, so a misspelled/unknown gate had no executable fail-closed proof. `SipJobFeatureGates` now uses a closed exact-name table, exposes no enablement/configuration path, and returns false for every known or unknown gate. Focused tests cover all declared gates plus null, empty, unknown, case-changed and whitespace-mutated names.

No parser/schema/digest exists at P14-0. This is recorded as absent rather than synthesized. Parser/version/digest negative behavior remains a P14-1 prerequisite.

## Commands actually executed

- `git rev-parse HEAD` → `52ccf45c05498a9143a599bb54499919a2cbcf8c`.
- `git status --short` → empty before edits.
- `dotnet --info` → SDK `11.0.100-rc.1.26425.128`, runtime `11.0.0-rc.1.26425.128`, win-x64.
- `git diff --stat 0f152a…HEAD` and scoped `git diff --name-status` → roadmap-only delta, no scoped runtime/source delta.
- `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase140ArchitecturalFreezeTests" --verbosity minimal` → passed 7, failed 0, skipped 0; compiler-extension target-framework warnings RS1041 remain pre-existing.
- `Get-FileHash -Algorithm SHA256` over built generator/analyzer/admission/runtime/SIP/contracts/platform-contract DLLs → hashes recorded in the tuple.

- `dotnet build SingNextOS.slnx --no-restore --verbosity minimal` → succeeded, 0 warnings, 0 errors.
- architecture filter → 45 passed, 8 failed. Seven failures are missing historical evidence at old paths (`docs/SingCap-Refactoring/**` and `docs/HybridCPU-v2-Boot-Reset-CXL-Boot-ABI-Roadmap/**`) after those artifacts were moved under `docs/Completed`; one pre-existing profile inventory failure reports the DebugHost/NeutralRuntime project-set mismatch. None executes or references P14 code, and they were not hidden or rewritten.
- required full non-GUI command → 1199 passed, 8 failed, 2 skipped, total 1209. The same eight unrelated architecture failures are the only failures.

## Claims, exclusions and FutureGated work

Enabled gates: none. Claim level: `ModelOnly`. Ordinary SIP remains the only executable path, semantic oracle and fallback.

- `FutureGatedRequiresProviderContract`: P14-7 semantic hints that cannot map losslessly to `HybridCPU.ExternalRuntime.Contracts` 1.14.0. Owner: platform/provider contract maintainers. Missing: versioned provider-neutral hint contract and conformance implementation.
- `FutureGatedRequiresHardware`: provider acceleration/hardware execution. Owner: provider/HybridCPU qualification. Missing: exact provider source/binary/hardware execution evidence.
- `FutureGatedRequiresCore`: NativeIsolated/confidential fusion and shared-mutable DAG. Owner: corresponding isolation/ownership core owners. Missing: explicit authority/isolation/conflict contracts and executable enforcement.
- `FutureGated`: NativeAOT. Owner: P14 qualification. Missing: NativeAOT build and contour-specific execution evidence.

No HybridCPU core, ISE, ISA/opcode, compiler-to-ISE, physical-register, frontend/pipeline/replay, memory-controller, retire-coordinator, scheduler-legality, or microarchitecture implementation was changed. No QEMU, firmware, CXL boot, hardware execution, zero-copy, transactional, confidential, acceleration, NativeAOT, production-readiness, plan-authority, handle-authority, or cache-authority claim is made.
