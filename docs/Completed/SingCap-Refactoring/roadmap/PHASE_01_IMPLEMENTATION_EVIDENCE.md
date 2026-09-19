# Phase 01 — Implementation Evidence

**Phase claim:** `StaticAdmission` for toolchain/profile gates; NativeAOT fixture remains `ModelOnly`

**Qualification date:** 2026-09-19

**Solution:** `SingNextOS.slnx`

## 1. Baseline and status preservation

- Phase-start `git rev-parse HEAD`: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- Phase-start status retained the pre-existing deleted legacy roadmap trees and untracked `docs/Completed`, `docs/SingCap-Refactoring`, plus the P00 architecture test.
- No reset, checkout, clean, force, commit, push or destructive operation was used.

## 2. Audited dependencies and reused types

P01 reused `global.json`, `Directory.Build.props`, `Directory.Build.targets`, the existing `SingPlusAnalyzer`, `AdmissionVerifier`, `Net11ReviewProbeTests`, all live project files and the actual Visual C++/NativeAOT toolchain. It did not create another admission verifier or modify capability, Region, SIP, session or external-operation semantics.

## 3. Selected design decisions

- Default production language remains C# 13; C# preview is an opt-in qualification lane through `SingPlusPreviewLanguage=true` and is not a security prerequisite.
- Every project has exactly one inventory classification and owner from `ManagedCap`, `TrustedRuntime`, `NativeIsolated`, `PlatformExternal`, or `BuildTool/TestOnly`.
- A profile declaration is metadata only. The single ManagedCap NativeAOT fixture is explicitly `ModelOnly` and has no admission root/proof.
- No current assembly is classified `NativeIsolated`: same-address-space native/provider code is `TrustedRuntime` or `PlatformExternal`. A future `NativeIsolated` entry must name a process, VM or platform-protection boundary and concrete evidence.
- NativeAOT is a separately executed build property and never substitutes for `SingPlus.Admission`.

## 4. Exact toolchain tuple

The drift golden `eng/singcap-toolchain-v1.json` records:

- SDK `11.0.100-rc.1.26425.128`, commit `3551975be0`;
- runtime `11.0.0-rc.1.26425.128`, commit `3551975be0`;
- Roslyn `5.11.0-1.26425.128`, commit `3551975be08744f0418857c5bed8ab1545c5dd47`;
- target framework `net11.0`, default language `13.0`;
- ILCompiler `11.0.0-rc.1.26425.128`, RID `win-x64`;
- Visual Studio 2019 Build Tools 16.11.49 / MSVC `14.29.30133`, linker file version `14.29.30159.0`;
- `AdmissionVerifier.cs` SHA-256 `1D74629801752215D7AEAB6B1598A1C24D679D1C7C3530312DB6F28299BE1EFA`.

The P01 lane checks SDK/config/policy drift in tests and ILCompiler/linker drift after actual AOT publish.

## 5. Authority and non-duplication proof

P01 adds only qualification metadata, tests, a zero-authority fixture and an orchestration script. The existing `CapabilityAuthority`, `RegionAuthority`, `EndpointSessionRegistry`, `ExternalOperationAuthority` and `AdmissionVerifier` remain the owners established in P00. No authority identity, generation, mutable registry, resolver, capability DTO or effect path was added.

## 6. Lifecycle, stale, revoke, cancellation, quarantine and reclaim

These production semantics are unchanged. P01 neither admits an effect nor consumes provider evidence. Existing stale/revoke/cancellation/quarantine/reclaim code and tests therefore remain authoritative; profile metadata and AOT output cannot change those outcomes.

## 7. Public/SIP/non-leak status

No public contract or SIP surface changed. The inventory contains project paths, classifications, owners and claim levels only; it contains no tokens, Region data, provider-private identity or reusable authority.

## 8. Changed files/projects

- `eng/singcap-security-profiles-v1.json` — exhaustive 26-project classification and owner inventory.
- `eng/singcap-toolchain-v1.json` — exact drift golden.
- `eng/qualify-singcap-p01.ps1` — focused/default/preview/NativeAOT qualification lane with C++ environment discovery and compiler/linker drift checks.
- `tests/fixtures/SingCap.ManagedCap.NativeAotFixture/*` — selected zero-authority NativeAOT fixture.
- `tests/SingPlus.Tests/Architecture/SingCapPhase01ToolchainTests.cs` — inventory, drift, NativeIsolated and metadata-non-promotion gates.
- `docs/SingCap-Refactoring/roadmap/PHASE_01_IMPLEMENTATION_EVIDENCE.md` — this evidence.

Production projects are unchanged.

## 9. Commands and actual results

1. `dotnet --info` — confirmed the SDK/runtime tuple above on Windows `win-x64`.
2. `dotnet .../Roslyn/bincore/csc.dll -version` — `5.11.0-1.26425.128 (3551975be08744f0418857c5bed8ab1545c5dd47)`.
3. `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter FullyQualifiedName~SingCapPhase01ToolchainTests --nologo -v:minimal` — passed 5/5.
4. Initial plain `dotnet publish ... -r win-x64 --self-contained true` — failed closed because the process environment did not expose a platform linker. No check was disabled.
5. NativeAOT publish after initializing the installed Visual C++ build environment — succeeded and generated the native image with ILCompiler `11.0.0-rc.1.26425.128`.
6. `eng/qualify-singcap-p01.ps1` — passed end-to-end: focused tests, default solution build, preview solution build, NativeAOT publish and ILCompiler/linker drift checks. Preview build completed with 5 existing `RS1041` warnings for analyzer/generator assemblies targeting `net11.0`; warnings were retained, not suppressed.
7. Related regressions (`SingCapPhase01ToolchainTests`, `Net11ReviewProbeTests`, `AdmissionVerifierTests`) — passed 64/64.
8. Final `dotnet build SingNextOS.slnx --nologo -v:minimal` — succeeded, 0 warnings, 0 errors.
9. Final `dotnet test SingNextOS.slnx --no-build --nologo -v:minimal` — passed: adapter 12/12, neutral runtime 58/58, HybridCPU platform 60/60, main suite 1097 passed with 2 existing opt-in skips.
10. `git diff --check` — executed after writing this evidence.

## 10. Claim level, limitations and FutureGated work

The executable profile/drift checks support `StaticAdmission` for P01 qualification metadata. The selected ManagedCap fixture remains `ModelOnly`: AOT success does not prove ManagedCap admission, authority enforcement or production safety. Full-module ManagedCap enforcement remains P09; a production `QualifiedManaged` AOT lane remains dependent on that verifier policy and P13 qualification.

The 5 `RS1041` preview-build warnings are an explicit toolchain limitation to resolve or accept through a versioned toolchain policy; they are not suppressed. No `NativeIsolated` claim exists until a real independent protection boundary is implemented and evidenced.

## 11. HybridCPU boundary confirmation

HybridCPU core, ISE, ISA, compiler, scheduler, runtime legality and architecture were not changed. P01 only inventories the existing provider/adapters as `PlatformExternal`; no provider token, receipt, generation or evidence becomes SingNext authority.
