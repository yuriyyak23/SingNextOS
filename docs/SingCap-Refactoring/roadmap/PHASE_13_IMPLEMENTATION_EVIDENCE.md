# Phase 13 Implementation Evidence

## 1. Local baseline and preservation

- SingNextOS HEAD before and after P13: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- The pre-existing dirty worktree, including unrelated deleted roadmap trees and all P03-P12 work, was preserved. No reset, checkout, clean, commit, push or destructive filesystem operation was used.
- The local HybridCPU-v2 checkout remained at `794c4a53494f503855ac8cf209efab23fde083b2`.

## 2. Audited dependencies and reused types

- Reused the existing `CapabilityAuthority`, `RegionAuthority`, `EndpointSessionRegistry`, `SealedObjectAuthority`, `ExternalOperationAuthority`, exact effect-admission leases and existing service/provider lifecycles.
- Reused the P01 pinned .NET/NativeAOT toolchain and P09/P10 `AdmissionVerifier` and deterministic proof pipeline.
- Reused exact HybridCPU package/source identities recorded in `P13_TCB_AND_SUPPLY_CHAIN_REVIEW.md`; no new provider dependency was introduced.

## 3. Selected design decisions

- P13 is a qualification/closure phase, not a new authority layer. Stress and measurement code is a friend qualification executable and is not a public runtime API.
- Contention characterization covers 1/2/4/8/16/32 workers for shared and unrelated authority, with throughput, median, p95, p99 and maximum observed operation latency. The latter is explicitly the closest available runtime contention metric, not a separately instrumented lock-wait claim.
- Claim closure is conservative: `QualifiedManaged` is the maximum release claim; `ProductionCandidate` and hardware-capability claims are withheld.
- NativeAOT verification uses an isolated per-run output root. RID-aware admission-tool resolution is deterministic. Only native assets declared by the pinned Microsoft runtime pack in the digest-bound `.deps.json` are classified as runtime assets; unrelated native files continue to fail closed.
- Cross-assembly overloads are resolved by exact metadata signature. Name-only overload selection remains forbidden.

## 4. Authority / identity / evidence / provider split

`P13_CLAIM_EVIDENCE_MATRIX.json` and `P13_SECURITY_QUALIFICATION_MATRIX.json` bind claims to executable evidence. Capability, service/session, sealed-object, Region and external-operation registries remain the live authority owners. Performance reports, manifests, audit records, provider receipts, package hashes and test reports are evidence only. Provider generation/admission/publication/cancellation is an additional gate and never a SingNext capability.

## 5. Single-ledger and non-duplication proof

- No capability, Region, quota, sealing, admission, completion or publication registry was added.
- The qualification executable invokes internal existing authorities through `InternalsVisibleTo`; it does not persist or resolve authority independently.
- P13 tests assert existing cleanup counters/pins and permitted race winners rather than mirroring lifecycle truth.

## 6. Lifecycle guarantees and linearization

- Deadlock stress exercises capability/session/seal/Region/external-operation gates with 32 workers and bounded completion.
- Admission versus Region MOVE has one permitted winner; stale submission is rejected and pins are released.
- Provider completion versus process restart cannot fabricate closure or retain Region uses.
- Publication versus capability revoke and Region reclaim preserves the closed `AdmissionOnly` policy: new admissions fail, the already-admitted publication linearizes once, and reclaim remains blocked until exact external release.
- Existing cancellation, quarantine and reclaim semantics remain authoritative; no completion, visibility, publication or release transition is inferred from revocation or provider loss.

## 7. Public/SIP and non-leak status

- No public/SIP API was added for qualification, generic resolution or authority enumeration.
- P13 exercises generated exact SIP sentries and representative network/file/process service migrations from prior phases.
- Performance and claim artifacts contain aggregate evidence only; opaque capability identity, resources, rights, quota state, sealed resources and provider tokens are not exported.

## 8. Changed files/projects

- `tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj` and `Program.cs`
- `tests/SingPlus.Tests/Architecture/SingCapPhase13ClosureTests.cs`
- `tests/SingPlus.Tests/Runtime/SingCapPhase13CrossAuthorityRaceTests.cs`
- `eng/qualify-singcap-p13.ps1`, `eng/qualify-singcap-p01.ps1`, `eng/singcap-security-profiles-v1.json`, `eng/singcap-toolchain-v1.json`
- `Directory.Build.targets`, `SingNextOS.slnx`, `src/Runtime/SingPlus.Runtime/AssemblyInfo.cs`
- `tools/SingPlus.Admission/AdmissionVerifier.cs` and `tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs`
- `P13_PERFORMANCE_BASELINE.json`, `P13_SECURITY_QUALIFICATION_MATRIX.json`, `P13_CLAIM_EVIDENCE_MATRIX.json`, `P13_TCB_AND_SUPPLY_CHAIN_REVIEW.md`

## 9. Commands and actual results

- Focused P13 race suite -> 4 passed, 0 failed, 0 skipped.
- Focused `AdmissionVerifierTests` after signature-qualified resolution -> 52 passed, 0 failed, 0 skipped.
- `eng/qualify-singcap-p01.ps1` -> 5 toolchain/profile tests passed; default and preview builds succeeded; the known five RS1041 compiler-extension warnings were observed; ManagedCap admission and `win-x64` NativeAOT generation succeeded.
- `eng/qualify-singcap-p13.ps1` -> locked restore succeeded; solution build succeeded; full tests: 12 adapter + 58 neutral runtime + 60 platform + 1186 main = 1316 passed, 2 explicitly skipped, 0 failed; nested P01 lane succeeded; performance artifact/matrix validation succeeded; `git diff --check` exited 0.
- Final independent `dotnet build "SingNextOS.slnx"` -> succeeded, 5 known RS1041 warnings, 0 errors.
- Final independent `dotnet test "SingNextOS.slnx" --no-build` -> 1316 passed, 2 explicitly skipped, 0 failed.
- Final `git rev-parse HEAD` -> `a67eea1aafc72054d22f1586b62c6883cdc71681`.

## 10. Claim level, limitations and FutureGated items

- Maximum claim: `QualifiedManaged` for the locally qualified managed/runtime contours. Individual runtime authority features remain `RuntimeEnforced` as mapped; manifest/audit is `StaticAdmission`; diagnostic performance and hardware enforcement are `ModelOnly`.
- `ProductionCandidate` is not granted. Physical hardware behavior, independent process/VM isolation, separately instrumented lock-wait, multi-host behavior, production SLOs, real-time and side-channel guarantees remain FutureGated.
- `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent; its content is neither inferred nor claimed.
- The compiler-extension projects retain five known RS1041 warnings under the current net11 toolchain.
- CapRef<T>, SharedArena, an unbounded capability heap and general shared mutable CLR graphs remain deferred.

## 11. HybridCPU boundary confirmation

No HybridCPU core, ISE, ISA/opcode, architectural register, load/store, retire, scheduler, compiler, runtime-legality, microarchitecture or architecture file was changed. Qualification remained inside SingNextOS and its provider-neutral executable-adapter/package boundary. No CHERI-equivalent or hardware-capability enforcement claim is made.

## Definition-of-Done audit

All sixteen items in Technical Specification section 22 map to P02-P13 executable evidence: one ledger and replay/exhaustion hardening; monotonic constraints/shared quota; revoke/effect races; representative sealing; Region/SIP/ManagedCap enforcement; deterministic manifest/audit/provenance; filesystem/network/process migrations; provider-neutral HybridCPU integration with exact artifact/source identity; adversarial/property/concurrency/performance qualification; and conservative claim wording. Deferred section 23 items were not implemented.
