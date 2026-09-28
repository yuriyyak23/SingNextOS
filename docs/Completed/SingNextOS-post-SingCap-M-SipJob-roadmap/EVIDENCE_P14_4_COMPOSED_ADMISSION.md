# P14-4 evidence — composed admission and segments

Disposition: `RuntimeEnforcedOwnerPrimitive` for the existing single-session capability-effect admission primitive; `FG-MULTI-SESSION-SEGMENT` remains OFF. Multi-session, seal-plus-Region composition remains `FutureGated`.

## Repeat-audit remediation (2026-09-20)

The repeat audit re-captured baseline HEAD/status and preserved the complete pre-existing dirty worktree. It found that an in-memory descriptor containing a `null` participant entry was dereferenced during version validation and escaped instead of returning a typed admission failure. The verifier now rejects null participants as `Malformed` before version, phase, or owner-protocol evaluation. `NullParticipantFailsClosedWithoutOwnerProtocolEvaluation` is the regression test. This does not change any admitted owner protocol, phase sequence, gate, or authority result.

Focused P14-4, capability-effect admission, P14-0, and P14-8 tests passed 28/28. Runtime and full solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1337 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58.

The strict ordered re-audit at current qualification HEAD `6227ea7cf258ef6ffce52001d4d2ffee07355b35` re-read the live effect-admission owner composition and repeated the P14-4/capability/P14-0/P14-8 lane: 28 passed, 0 failed. The immediately preceding exact full non-GUI run on the same rebuilt executable tree recorded 1350 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; Runtime and full solution builds were already 0-warning/0-error after the only runtime change in this tree. Artifact hashes, both roadmap JSON documents, tuple-to-HEAD binding, default-OFF gate policy and `git diff --check` were then revalidated. No additional P14-4 defect was confirmed, and no code or claim promotion was made in this pass.

## Candidate and dirty-worktree preservation

- Baseline HEAD: `52ccf45c05498a9143a599bb54499919a2cbcf8c`.
- Candidate: Windows x64 JIT, SDK `11.0.100-rc.1.26425.128`, runtime `11.0.0-rc.1.26425.128`.
- `git rev-parse HEAD` and `git status --short` were captured before this phase. Pre-existing dirty files were preserved; no reset, checkout, clean, commit, push, or user-file deletion occurred.

## Reviewed live owners and contracts

- `CapabilityAuthority` / `OperationAuthorityLease`: quota and one-shot state are consumed by `AcquireOperationAuthority`; lease disposal removes the active lease and does not refund consumption.
- `EndpointSessionRegistry`: `AcquirePin`, `RevalidatePin`, owner-defined deferred close, and final pin release.
- `SealedObjectAuthority`: owner-defined pin/revalidation surface reviewed, but not composed into the qualified contour.
- `RegionAuthority`: use acquisition/validation/release reviewed, but not composed into the qualified contour.
- Generated closed-value sentry and the P14-2/P14-3 ordinary-versus-inline qualification contours remain the only service-entry mechanism.

## Requirement disposition and remediation

The live baseline acquired `OperationAuthorityLease` before final session-pin revalidation. Because acquisition consumes quota/one-shot and disposal is not compensation, a close race could consume authority for an unreachable stage. This was a security-relevant ordering defect.

Remediation in `RuntimeKernel.EffectAdmission` now performs process resolution, reversible session pin, final session revalidation, just-in-time capability commit, then returns a TCB-private composite lease. Partial failures release in reverse acquisition order. Normal lease disposal releases capability lifecycle state before finalizing the session pin through the existing owner; it never refunds quota or one-shot state.

`SipJobSegmentAdmissionVerifier` adds a closed, immutable, non-authoritative vocabulary for `ProbeOnly`, `ReversiblePrepare`, `CommitConsumptive`, and `PostCommitSettlement`. It accepts only exact version-1 owner protocols and exact gate set, rejects unknown versions/kinds/gates, phase reordering, disguised consumptive acquisition, and more than one independent consumptive commit. Verified metadata contains identities and ordering only—no permission result and no owner handle.

Deterministic hooks cover close after prepare, quota loss and revoke after final revalidation, and close between final revalidation and commit. Tests prove close before final revalidation consumes neither quota nor one-shot; revoke/quota loss before commit produces no operation lease and releases the session pin; a concurrent close after final revalidation drains but cannot invalidate the live pin; concurrent one-shot commit has exactly one winner; and disposal never fabricates compensation. The admission primitive invokes no generated sentry, service, provider, reflection, continuation, or async code under an authority-owner lock.

## Commands actually run

- Focused P14-4 plus capability admission: `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase144ComposedAdmissionTests|FullyQualifiedName~SingCapPhase04EffectAdmissionTests" --verbosity minimal` — 19 passed, 0 failed.
- Relevant P14, capability, seal, Region-use, and session-cancellation regressions: `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase14|FullyQualifiedName~SingCapPhase04EffectAdmissionTests|FullyQualifiedName~SingCapPhase06SoftwareSealingTests|FullyQualifiedName~RegionUseTests|FullyQualifiedName~EndpointSessionCancellationTests" --verbosity minimal` — 122 passed, 0 failed.
- `dotnet build src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj --no-restore --verbosity minimal` — succeeded, 0 warnings, 0 errors.
- `dotnet build SingNextOS.slnx --no-restore --verbosity minimal` — succeeded, 0 warnings, 0 errors.
- `dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal` — changed project: 1291 passed, 8 failed, 2 skipped (1301 total); the other three test assemblies passed 90/90, 58/58, and 60/60. The eight failures are unchanged and unrelated: seven missing historical P11/P12/P13/Hybrid Boot JSON evidence files and one pre-existing security-profile inventory mismatch. No P14 test failed.
- Architecture/default-gate policy filter — 7 passed, 0 failed.
- Both changed P14 JSON files parsed successfully with `ConvertFrom-Json`.
- `git diff --check` exited 0; output contained only existing LF-to-CRLF conversion warnings and no whitespace error.

## Gates, claim, and exclusions

- All SipJob feature gates remain default OFF. Unknown gates fail closed.
- Exact claim: `RuntimeEnforcedOwnerPrimitive` for existing single-session session-pin plus capability-effect admission ordering on the recorded JIT tuple. It is not an enabled Job contour and not a transaction manager.
- `FG-MULTI-SESSION-SEGMENT`: OFF, `FutureGated`. Owner: runtime authority composition owners. Missing evidence: an owner-defined multi-session segment protocol, seal-plus-Region partial-prepare cleanup matrix, and generated-sentry differential/race qualification for that exact contour. A generic pseudo-transaction or refund would be unsafe.
- Independent multiple non-compensatable commits are rejected/materialized; no atomicity or rollback claim is made.
- Ordinary SIP remains fallback and semantic oracle. Plans, descriptors, metadata, handles, caches, traces, and diagnostics confer no authority. No raw mutable ManagedCap reference crosses a Job boundary.
- No HybridCPU core, ISE, ISA/opcode, compiler-to-ISE, physical-register, frontend/pipeline/replay, memory-controller, retire-coordinator, scheduler-legality, or microarchitecture implementation was changed. QEMU, firmware, CXL boot, hardware execution, NativeAOT, and acceleration remain unclaimed.
