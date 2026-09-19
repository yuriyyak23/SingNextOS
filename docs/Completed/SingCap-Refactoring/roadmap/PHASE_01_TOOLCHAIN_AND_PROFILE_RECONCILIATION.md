# Phase 01 — Toolchain and Security-Profile Reconciliation

## Goal

Reconcile the already-pinned .NET 11 RC1 baseline with C# 15 preview, NativeAOT and security-profile qualification without changing authority semantics.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Current code truth

`global.json` already pins `11.0.100-rc.1.26425.128`; default `LangVersion` is `13.0`, preview is opt-in. Existing admission/analyzer infrastructure already runs under the current solution.

## Work

### 1. Toolchain evidence

Record exact:

```text
SDK
runtime
Roslyn/compiler version
AOT compiler version where used
AdmissionVerifier policy/ruleset digest
```

### 2. C# 15 policy

Do not make preview language features a prerequisite for capability security. Add qualification builds using `SingPlusPreviewLanguage=true`; decide whether production ManagedCap remains C# 13-compatible until C# 15 stabilizes.

### 3. Security profile inventory

Classify every relevant project/assembly:

```text
ManagedCap
TrustedRuntime
NativeIsolated
PlatformExternal
BuildTool/TestOnly
```

The inventory must cover `src`, `sdk`, `tools`, native/provider adapters and representative services.

### 4. NativeIsolated proof requirement

Do not classify same-address-space unsafe/native code as NativeIsolated. Record the actual process/VM/platform-domain boundary or classify it TrustedRuntime instead.

### 5. NativeAOT qualification skeleton

Add CI lane for the intended ManagedCap NativeAOT profile; capture trimming/AOT warnings as evidence. Do not claim that NativeAOT replaces admission checks.

## Primary paths

```text
global.json
Directory.Build.props
Directory.Build.targets
*.csproj
tools/SingPlus.Admission/
tests/SingPlus.Tests/Net11ReviewProbeTests.cs
CI/workflow files
```

## PR slices

- **P01-1:** baseline metadata and profile inventory.
- **P01-2:** C#15 preview qualification lane.
- **P01-3:** NativeAOT qualification lane and exact compiler/runtime evidence.

## Tests

- default language build;
- preview language build;
- AOT build of selected ManagedCap fixture;
- negative check that profile metadata cannot promote code to ManagedCap by declaration alone;
- exact toolchain drift test/golden.

## Exit criteria

- no stale statement that SingNextOS is on .NET 10;
- exact toolchain tuple appears in qualification evidence;
- profile owner for every security-relevant assembly is known;
- NativeIsolated has a physical isolation requirement;
- capability semantics remain unchanged.

## Rollback

Toolchain evidence/CI changes are independently revertible. Never roll back by disabling admission warnings for production claims.
