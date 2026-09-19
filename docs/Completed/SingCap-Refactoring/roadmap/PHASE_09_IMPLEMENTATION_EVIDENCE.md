# Phase 09 Implementation Evidence

## 1. Local baseline and preservation

- Baseline HEAD: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- The worktree was already dirty, including unrelated deleted roadmap trees and untracked documentation. Those changes were preserved; no reset, checkout, clean, commit, push, or destructive filesystem operation was used.
- `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent. This is recorded as a limitation; no content was inferred from it.

## 2. Audited dependencies and reused types

- Reused the existing `SingPlus.Admission.AdmissionVerifier`, `AdmissionVerificationResult`, `AdmissionViolation`, `SingPlusAdmissionProofV1`, metadata/CIL reader, dependency digest, CLI, and `SingPlusAdmissionVerify` MSBuild target.
- Reused the P01 ManagedCap NativeAOT fixture as the real consumer. No second static verifier or admission result was introduced.
- Audited the existing `KernelNoHeap` reachability policy and retained its behavior and executable regressions.

## 3. Selected design decisions

- `ProfilePolicy` is internal policy data owned by `AdmissionVerifier`; unknown profiles fail closed.
- `ManagedCapV1|net11|framework-surface-v1` uses a full-module scan over the root and transitive local managed dependency closure. The declared root is still uniquely resolved but is not the security boundary.
- External framework calls are classified by assembly identity, declaring type/member, and canonical metadata signature bytes. Framework type references and framework assembly major version are independently classified. Namespace prefixes never grant admission.
- The positive v1 surface is intentionally small. New types, members, signatures, or framework versions require explicit policy classification.
- Mutable static reference-bearing fields are rejected; ordinary value statics are not rejected merely for mutability.
- Undeclared native `.dll`, `.so`, and `.dylib` assets colocated with the admitted assembly are rejected and content-bound into dependency evidence.

## 4. Authority / identity / evidence / provider split

- The verifier decides only static ManagedCap admission. It creates no capability, Region, SIP, sealing, quota, lifecycle, completion, publication, or provider authority.
- Assembly bytes, local dependency identities/digests, native inventory, framework tuple, policy identity, violations, and proof digests are evidence inputs, not runtime authority.
- Provider receipts and HybridCPU tokens remain outside this decision and cannot establish ManagedCap admission.

## 5. Single-gate and non-duplication proof

- `AdmissionVerifier.Verify` remains the sole final static policy result.
- The NativeAOT fixture invokes the existing repository `SingPlusAdmissionVerify` target with profile `ManagedCap`; it does not invoke a parallel verifier.
- The target now launches through MSBuild's exact `DOTNET_HOST_PATH`, fixing the pre-existing child-process PATH failure.
- The admitted fixture generated `SingPlusAdmissionProofV1.json` with profile `ManagedCap`, root `SingCap.ManagedCap.NativeAotFixture.Program::Main`, one scanned method, and zero violations.

## 6. Lifecycle and runtime semantics

- P09 changes no runtime effect lifecycle, stale-handle, revocation, cancellation, quarantine, or reclaim semantics.
- Static admission occurs before the ManagedCap artifact may be treated as admitted. Runtime capability/effect admission remains owned by the existing authorities from P02-P08.
- Full-module scanning prevents hidden method bodies, module initializers, static constructors, or unsafe bodies in local dependencies from escaping because they are unreachable from the declared root.

## 7. Public / SIP / non-leak status

- No public capability or SIP contract was expanded.
- No resolver, ambient authority root, enumerable capability bag, raw mutable graph transport, or authority-bearing descriptor was added.
- Exact framework metadata identities appear only in verifier policy/evidence and convey no authority.

## 8. Changed files and projects

- `tools/SingPlus.Admission/AdmissionVerifier.cs`: profile policy, ManagedCap full-closure scan, exact positive member/type/version classification, static-state scan, native inventory, and deterministic policy digest binding.
- `Directory.Build.targets`: existing admission target uses `DOTNET_HOST_PATH`.
- `tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs`: positive, negative, hidden-code, dependency, metadata, native, drift, and fail-closed tests.
- `tests/fixtures/SingCap.ManagedCap.NativeAotFixture/SingCap.ManagedCap.NativeAotFixture.csproj`: real ManagedCap gate integration.
- `tests/SingPlus.Tests/Architecture/SingCapPhase01ToolchainTests.cs`, `eng/singcap-toolchain-v1.json`, and `eng/singcap-security-profiles-v1.json`: qualified policy digest and claim/gate expectations.

## 9. Commands and actual results

- `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~AdmissionVerifierTests`
  - Passed: 52; failed: 0; skipped: 0.
- `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~SingPlus.Tests.Admission|FullyQualifiedName~SingCapPhase01ToolchainTests|FullyQualifiedName~Net11ReviewProbeTests"`
  - Passed: 96; failed: 0; skipped: 0.
- `dotnet build tests/fixtures/SingCap.ManagedCap.NativeAotFixture/SingCap.ManagedCap.NativeAotFixture.csproj --no-restore -p:PublishAot=false`
  - Succeeded: 0 warnings, 0 errors; canonical admission proof created.
- `dotnet build "SingNextOS.slnx"`
  - Succeeded: 0 warnings, 0 errors.
- `dotnet test "SingNextOS.slnx" --no-build`
  - Adapter: 12 passed; neutral runtime: 58 passed; platform: 60 passed; SingPlus: 1165 passed, 2 skipped. Aggregate: 1295 passed, 2 skipped, 0 failed.
- `git diff --check`
  - Exit code 0; only line-ending conversion notices.

## 10. Claim level, limitations, and FutureGated work

- Claim: `RuntimeEnforced` for the implemented ManagedCap v1 static gate and the explicitly admitted fixture; no hardware-enforcement claim.
- The positive framework surface is deliberately minimal and policy-versioned. Additional collections, async primitives, generated value shapes, or framework members remain denied until classified with tests.
- Native assets are deny-by-default because no declaration schema is admitted in P09. A future declaration format must remain evidence and must be content/identity-bound.
- Malformed-metadata fuzzing beyond the PE/metadata reader's fail-closed behavior is FutureGated to P13 adversarial expansion.
- P10 must bind this AdmissionVerifier policy digest and ManagedCap framework-surface identity into manifests and audit evidence.

## 11. HybridCPU boundary confirmation

- HybridCPU core, ISE, ISA/opcodes, register file, load/store, retire, scheduler, compiler semantics, runtime legality, microarchitecture, and architecture were not changed.
- No SingNextOS-to-HybridCPU implementation dependency was added. Existing adapter/qualification tests only ran as regressions.
