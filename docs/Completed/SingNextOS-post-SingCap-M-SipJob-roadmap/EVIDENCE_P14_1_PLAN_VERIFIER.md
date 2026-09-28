# P14-1 executable evidence — immutable plan verifier

## Repeat-audit note (2026-09-20)

The latest strict-cycle entry captured qualification HEAD `6227ea7cf258ef6ffce52001d4d2ffee07355b35`, audit baseline `52ccf45c05498a9143a599bb54499919a2cbcf8c`, and preserved the full dirty P14-0/P14-7/P14-8 worktree. It found a live-verifier/parser consistency defect: JSON null stage/edge entries were typed `Malformed`, but an in-memory immutable descriptor containing a null stage or edge was dereferenced during version validation. `Verify` now rejects either collection form as `Malformed` before any graph/catalog/digest traversal. `DirectDescriptorNullStageOrEdgeFailsClosedWithoutThrowing` covers both paths. The P14-1/P14-0/P14-8 focused lane passed 49/49 after the executable tuple correctly detected and required refresh of the rebuilt Runtime DLL hash. Runtime and solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1348 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58.

The repeat audit re-entered P14-1 at baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c` with the complete pre-existing dirty SipJob/runtime/generator/test/evidence contour listed by `git status --short`, not only the P14-0 files described by the original historical phase-entry paragraph. Those user-owned changes were preserved. A malformed in-memory descriptor with a null `PlanDigest` exposed a fail-closed defect: fixed-time comparison dereferenced the supplied value and threw instead of returning typed verifier failure. `FixedDigestEquals` now treats a missing digest as mismatch, and `MissingDigestFailsClosedWithoutThrowing` is the regression test. This changes no authority owner or enabled gate.

A later repeat pass found that the JSON DTO boundary treated absent non-nullable constructor parameters as CLR defaults. In particular, an omitted enum could silently become `None`, `Synchronous`, `ClosedCopiedValue`, or `CallerProvidedCapabilityReference` instead of being rejected as malformed. Parser options now require every DTO constructor parameter, while continuing to reject unknown fields, wrong case, numeric enums, and unknown enum names. `ParserRejectsMissingSemanticEnumInsteadOfUsingClrDefault` proves the missing-`Mode` case. The current P14-1/P14-0/P14-8 focused lane passed 45/45; runtime and solution builds were clean; full non-GUI results were 1329 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`, with the other assemblies at 60/60, 90/90, and 58/58.

The next malformed-input pass found that JSON `null` elements inside `Stages`, `Edges`, or nested `AuthorityRequirements` were dereferenced during DTO projection and could escape as `NullReferenceException` rather than a typed parser failure. Element nullability is now explicit and every element is converted to `JsonException`/`Malformed` before descriptor construction. `ParserRejectsNullDescriptorArrayElementsWithoutThrowing` and `ParserRejectsNullAuthorityRequirementWithoutThrowing` cover all three collections. The post-remediation focused lane passed 48/48; runtime and solution builds were clean; full non-GUI results were 1332 passed, the same 8 unrelated failures, and 2 skipped, with the other assemblies at 60/60, 90/90, and 58/58.

Immediate post-remediation qualification: P14-0/P14-1 focused set 43 passed, 0 failed; Runtime and full solution builds succeeded with 0 warnings and 0 errors; full non-GUI main assembly 1317 passed, 8 unchanged unrelated failures, 2 skipped; the other three assemblies passed 60/60, 90/90, and 58/58. Both roadmap JSON files parsed and `git diff --check` reported no whitespace error. The runtime and focused-test hashes at this checkpoint were `2829288FB012DA735ED58702B853CDBD8BC93C9D1182A5B346C91694FF69D172` and `593B3235FA67C068D5BAD9E90CE1441CE052E003E770CE8734BF2D87DE35C60C`; later phase remediation supersedes the runtime artifact hash in the qualification tuple.

## Disposition and tuple

P14-1 reaches `StaticAdmission` for the non-executing 2–4 stage linear, synchronous, same-runtime, closed-copied-value plan verifier. `FG-JOB-LINEAR` remains OFF and no execution API exists.

Baseline HEAD remains `52ccf45c05498a9143a599bb54499919a2cbcf8c`; the dirty worktree at phase entry contained only the P14-0 files recorded in `EVIDENCE_P14_0_ARCHITECTURAL_FREEZE.md`, all preserved. Runtime is Windows x64 JIT, SDK/runtime `11.0.100-rc.1.26425.128` / `11.0.0-rc.1.26425.128`. The built P14-1 runtime DLL SHA-256 is `DD9E6F660D7AAE766EAB1E49E35C307CE1D36F66B46DF69308C6C93B1165BD3A`; focused-test DLL SHA-256 is `9089539F63DC4F3D0093BF2670498A61EAE35DAD559545B9855E2EB381F91EB7`.

## Requirement disposition

| Requirement | Implementation / live owner | Focused evidence | Claim |
|---|---|---|---|
| SJOB-001/005 | authority requirements carry only closed source enum plus opaque owner/requirement IDs; existing owners are not queried or copied | success metadata has no authorization boolean; reflection surface test | StaticAdmission metadata only |
| SJOB-002 | descriptors use strings/enums/immutable arrays; no `object`, `Type`, delegate, Task/ValueTask or service provider field | `DescriptorSurfaceCannotCarryRawRuntimeReferences` | StaticAdmission |
| SJOB-003/013 | each catalog entry binds exact contract, operation, request/response schema and precompiled thunk ID/digest | unknown operation and catalog/digest mismatch negatives | StaticAdmission; no thunk execution |
| SJOB-004/011/012/014/015 | MVP accepts only closed copy, no barrier, no external effect, hidden intermediate invocation and one final publication | Region/external/async/barrier/observable-intermediate negatives | StaticAdmission only |
| SJOB-006 | no cache exists | source inspection | ModelOnly for cache |
| SJOB-007–010 | Region, composed admission and async rejected | focused negatives | ModelOnly; later phases required |
| SJOB-016 | exact JIT artifact hashes and gate state recorded | focused suite | contour-specific StaticAdmission |

## Verifier behavior

`SipJobPlanVerifier` defensively copies all collections to immutable arrays. It proves stage count 2–4, unique stages/edges, exact ordered linear connectivity, no orphan/cycle/side edge, closed catalog equality, allowed authority-source classes, synchronous/no-effect/no-ownership contour, one final publication, explicit policy IDs, exact producer/edge/consumer schema equality, and fixed-time canonical digest equality.

`SipJobPlanJsonParser` is case-sensitive, rejects unmapped fields, numeric enum values and malformed input. Exact version checks reject unknown plan, stage, edge and authority descriptor versions. Canonical serialization binds versions, ordered gates/stages/edges and every descriptor semantic field. Tests mutate contract/operation/schema/thunk/authority/protocol/cancellation/execution/isolation/barrier/publication families and prove the digest changes.

`VerifiedPlanMetadata` contains only plan digest, ordered stage IDs and format version. It has no permission result, implementation binding, capability lease, session pin or service reference. Possession cannot execute anything because P14-1 adds no executor and every gate remains OFF.

## Defects and remediation

The live tree had no immutable SipJob descriptor, strict parser, canonical digest, finite catalog or graph verifier. These were added as TCB-internal non-authoritative metadata. The first test pass exposed only test-shape issues (internal xUnit data accessibility and compiler-generated record `EqualityContract`); no verifier bypass was observed. Tests were corrected without weakening descriptor checks.

## Commands and actual results

- phase-entry `git rev-parse HEAD` → `52ccf45c05498a9143a599bb54499919a2cbcf8c`.
- phase-entry `git status --short` → only P14-0 untracked files.
- `dotnet build src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj --no-restore --verbosity minimal` → succeeded, 0 warnings, 0 errors.
- focused verifier command → passed 21, failed 0, skipped 0.
- `git diff --check` → passed.

- `dotnet build SingNextOS.slnx --no-restore --verbosity minimal` → succeeded, 0 warnings, 0 errors.
- required full non-GUI command → 1220 passed, 8 failed, 2 skipped, total 1230. The same eight unrelated architecture-policy failures documented by P14-0 are the only failures; all 21 new P14-1 tests are included in the passing count.

## Gates, fallback and exclusions

Enabled gates: none. `FG-JOB-LINEAR` is recognized in descriptors but remains non-executing and OFF. Ordinary SIP remains the only runtime path, oracle and fallback. Parser/verifier failure returns typed evidence only and cannot grant authority.

P14-2 direct sentry execution, Region, external effects, async, DAG, dynamic binding/cache, NativeAOT, provider scheduling and hardware remain FutureGated under their documented owners/prerequisites. No HybridCPU core/ISE/ISA/compiler/microarchitecture file changed. No plan, digest, verified metadata or catalog became authority.
