# Phase 12 — HybridCPU Provider Alignment and External-Operation Qualification

## Goal

Qualify current HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2` as a lower-level semantic provider while preserving the SingNextOS authority boundary and existing ISE.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Current HybridCPU facts used

Current master supplies provider-neutral contracts for exact operation identity/correlation, opaque generation sets, independent CPU guard + provider admission binding, staged publication evidence, cancellation ambiguity and secure-domain admission policy.

These are **not** SingNext capabilities. They are provider/runtime evidence and enforcement within their own scope.

## Current SingNext facts used

`HybridCpuExternalOperationProvider` already maps HybridCPU external operation requests into SingNext's existing external-operation lifecycle and Region uses. This implementation remains the primary SingNext-side authority adapter.

## Work

### 1. Artifact qualification

Build/obtain the exact `HybridCPU.ExternalRuntime.Contracts` artifact from the approved source commit and record SHA-256. Do not silently replace the repository-local `1.14.0` package solely because the version string matches.

### 2. Contract delta mapping

Create an exact table:

```text
HybridCPU external request/generation/correlation
    -> SingNext ExternalOperationHandle / dependency snapshot
CPU guard receipt
    -> secondary CPU-side evidence only
provider admission receipt
    -> provider gate only
DeviceComplete receipt
    -> completion evidence only
Visible receipt
    -> visibility evidence only
publication evidence/gate
    -> additional prerequisite, not local authority
release receipt
    -> provider closure evidence; local Region closure still required
```

### 3. Admission binding

Where integrated, require both independent gates rather than interpreting one as the other. CPU guard allowed + provider denied => no submission. Provider admitted + CPU guard stale => no submission.

The local capability/session/Region/external-operation admission must first commit one Authority Composition attempt. Provider admission is then correlated to that attempt outside all SingNext authority locks. A provider callback may re-enter only through exact operation/correlation/generation lookup; nested adapter/runtime gates require a documented lock-order audit.

### 4. Publication

Use the current HybridCPU publication evidence/gate as additional fail-closed evidence for staged output where appropriate. SingNext `PublishExternalOperation` remains responsible for local publication and Region state.

### 5. Cancellation / generation drift

Generation drift or ambiguous cancellation prevents release/reuse. Reconcile or quarantine; do not infer success from transport loss.

### 6. SecureCompute boundary

Secure-domain admission may be used as coarse provider enforcement only when an actual provider capability is qualified. A HybridCPU domain tag is not a CHERI tag and does not replace SingNext subject/resource capabilities.

## Must not change

```text
HybridCPU ISE instructions/opcodes
architectural register file
load/store semantics
retire semantics
compiler->ISE contract for SingCap
```

## Primary SingNext paths

```text
src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs
src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge*
src/Platform/SingPlus.Platform.HybridCpu/
tools/HybridCpu_ExecutableAdapter/
tools/SingPlus.HybridCpuQualification/
tests/SingPlus.Tests/Runtime/Phase10ProviderConformanceTests.cs
```

## PR slices

- **P12-1:** exact package/source qualification and compatibility inventory.
- **P12-2:** integrate independent admission binding where current adapter contour supports it.
- **P12-3:** publication evidence + generation drift/cancel negative tests.
- **P12-4:** qualification artifacts and cross-project claim matrix.
- **P12-5:** adapter/runtime lock-order and callback re-entry audit with deadlock stress.

## Tests

- exact package hash/source commit;
- CPU guard missing/rejected/stale/cross-request;
- provider admission missing/rejected/stale/cross-request;
- generation mismatch;
- completion without visibility does not publish;
- visibility without local publication does not return ownership;
- cancellation ambiguous => quarantine;
- provider loss => no false release;
- no HybridCPU token appears in public SingNext capability APIs.
- provider callback/reconnect races cannot deadlock or bypass local final revalidation;
- no provider call is made while a SingNext capability/session/Region/external-operation registry lock is held.

## Exit criteria

The new HybridCPU external-operation features strengthen provider-side checking/evidence without becoming the SingNext authority root, and the ISE remains unchanged.
