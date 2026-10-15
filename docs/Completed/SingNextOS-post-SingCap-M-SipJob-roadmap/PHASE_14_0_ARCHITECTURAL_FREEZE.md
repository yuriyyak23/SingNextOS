# P14-0 — Architectural Freeze, Baseline Tuple, and Semantic Contract

## 1. Purpose

Freeze the exact implementation/tooling/provider baseline and the architectural meaning of `SipJob` before any semantic Job runtime code lands.

P14-0 is documentation, evidence, and test-instrumentation work. It MUST NOT introduce a privileged Job execution path.

## 2. Entry conditions

The following sources must be reviewed at the exact candidate SHAs:

```text
SingNextOS:
  contracts/SingPlus.Contracts/**
  src/Sip/SingPlus.Sip/**
  src/Runtime/SingPlus.Runtime/Capabilities/**
  src/Runtime/SingPlus.Runtime/Regions/**
  src/Runtime/SingPlus.Runtime/Services/**
  src/Runtime/SingPlus.Runtime/Channels/**
  src/Runtime/SingPlus.Runtime/ExternalOperations/**
  src/Runtime/SingPlus.Runtime/NativeServices/**
  src/Platform/**
  sdk/SingPlus.Generators/**
  sdk/SingPlus.Analyzers/**
  tools/SingPlus.Admission/**
  tools/SingPlus.SingCapQualification/**
  tests/SingPlus.Tests/**

SingCap-M normative/evidence corpus:
  docs/Completed/SingCap-Refactoring/**

System co-design corpus:
  docs/whitebook/hybridcpu-ise/**

HybridCPU-v2:
  README.md
  Documentation/WhiteBook/**
  Documentation/Stream WhiteBook/**
  Documentation/Virtualization WhiteBook/**
  Documentation/SecureCompute WhiteBook/**
  Documentation/ISE_Instructions_By_Lane_CodeConfirmed.md
```

## 3. Live implementation anchors to re-confirm

The pinned tuple review MUST trace the ordinary execution path through these concrete owners/components before relying on any P14 assumption:

```text
CapabilityAuthority
OperationAuthorityLease
RuntimeKernel.EffectAdmission
EndpointSessionRegistry
EndpointSessionInvocationRegistry
ChannelRegistry
ResponseRegistry
RegionAuthority
OwnedBuffer<T> / OwnedRegion<T> / BorrowLease<T>
RuntimeSipClientTransport
SIP generators/analyzers
AdmissionVerifier
platform/provider bridge
```

In particular the candidate tuple MUST re-confirm, with code/tests rather than documentation alone:

- whether operation-authority acquisition consumes quota/one-shot at acquisition;
- whether lease disposal restores any consumed authority state (P14 assumes no compensation unless explicitly provided);
- exact session pin/close/draining semantics;
- exact Region transfer owner/generation linearization;
- ordinary ownership-response and consuming-request transition sequence;
- response waiter/publication lifecycle;
- closed-world ManagedCap restrictions on delegates/reflection/reference-bearing globals;
- exact provider contract/version and legality/admission boundary.

If any of these semantics differ in the pinned implementation tuple, the dependent phase text MUST be reconciled before implementation rather than forcing code to match a stale assumption.

## 4. Source snapshot

The source snapshot recorded when this roadmap was prepared is:

```text
Date:              2026-09-19
SingNextOS HEAD:   0f152a5502c58546eb0458b4be3e7cbce6c5ef3e
HybridCPU-v2 HEAD: 90329e2e342575953ce66e741f3c1f223ff577a0
```

Before any semantic P14 PR merges, CI/reviewer evidence MUST record the actual candidate tuple. If either HEAD changes, delta review is mandatory. A historical pinned SHA is not evidence for a newer commit.

## 5. Required freeze artifact

Create a machine-readable and human-readable qualification tuple containing at least:

```text
SingNextOSSourceSha
HybridCpuSourceSha
DotNetSdkVersion
RuntimeVersion
NativeAotVersion (when applicable)
GeneratorArtifactDigest
AnalyzerArtifactDigest
AdmissionVerifierArtifactDigest
AdmissionPolicyDigest
SipSchemaManifestDigest
ThunkCatalogDigest
ProviderPackageVersion/Digest (when applicable)
ProviderContractVersion/Digest (when applicable)
EnabledFeatureGates
TargetRuntimeMode: JIT | NativeAOT
```

The tuple is evidence metadata only; it is not authority.

## 6. Architectural freeze decisions

### ADR-14-0-A — SipJob is an optimization layer

`SipJob` does not define new capability, session, Region, seal, external-operation, response, or publication ownership.

### ADR-14-0-B — ordinary SIP is the oracle

Every qualified contour keeps a normal SIP path that can execute the same contract with the same authority inputs.

### ADR-14-0-C — direct dispatch is still a security transition

Every fused stage uses an operation-specific generated trusted sentry/static thunk. Direct implementation invocation is prohibited.

### ADR-14-0-D — semantic trace, not final state, defines equivalence

Qualification compares ordered authoritative transitions. Transport-only events may differ only when listed in a fixed erase/whitelist set.

### ADR-14-0-E — copied-value isolation remains semantic

Shared runtime placement does not permit reuse of mutable caller references when ordinary SIP provides snapshot/isolation semantics.

### ADR-14-0-F — consumptive admission is commit

An authority API that consumes quota/one-shot state during acquisition is a commit linearization. It is not placed in a reversible prepare set unless the authority owner itself exposes a qualified reservation/commit primitive.

### ADR-14-0-G — Region transition sequence is normative

Fusion preserves the complete ordinary ownership/use sequence and generation progression.

### ADR-14-0-H — Job is not a transaction

Failure after committed private mutation, authority consumption, Region transfer, provider submission, or publication does not imply rollback.

### ADR-14-0-I — async service state remains compartment-private

Service-created awaitables/state machines stay behind the generated sentry. Job state contains only TCB-owned correlation and admitted closed values.

### ADR-14-0-J — physical HybridCPU details are not Job ABI

Only semantic requests/hints may cross the OS/platform boundary, and only through a qualified provider contract.

## 7. Authority map

P14-0 MUST produce a reviewable map that names, for every proposed Job field:

```text
field
purpose
whether ManagedCap-visible
whether reference-bearing
which existing owner validates it
when it is revalidated
whether it may be cached
why possession of it is not authority
```

Fields with no answer to `which existing owner validates it` are rejected unless they are pure value metadata with no permission meaning.

## 8. Linearization map

Before P14-1, document the current ordinary SIP linearization points for:

- process/service generation validation;
- session pin/close/drain;
- operation-authority acquisition/consumption;
- sealed-object pin/close;
- Region borrow/use acquisition and release;
- Region transfer/generation advance;
- protocol transition;
- invocation/cancellation transition;
- response publication;
- external operation prepare/admit/submit/complete/visibility/release.

The map MUST cite code paths and executable tests in the pinned source tuple.

## 9. Semantic event vocabulary

Introduce test-only event identifiers sufficient for differential qualification. Event payloads should contain opaque IDs/generations and outcome classes, not authority-bearing references.

Instrumentation requirements:

- disabled or low-overhead in production builds;
- cannot affect admission outcome;
- no user/provider callback from instrumentation path;
- event order reflects completed owner linearizations, not speculative intent.

## 10. Baseline drift review

For every authority owner and generator/admission path touched by P14, compare the candidate source tuple against the most recent qualified SingCap-M artifacts.

Classify each delta:

```text
NoSemanticImpact
SecurityRelevantButCompatible
RequiresRequalification
BlocksP14
```

A delta classified `RequiresRequalification` or `BlocksP14` must be resolved before the dependent Job gate advances.

## 11. Initial feature state

All Job feature gates remain OFF.

P14-0 may add:

- schemas/models with no execution enablement;
- test-only semantic tracing;
- qualification tuple capture;
- CI checks that ensure gates default OFF.

## 12. PR decomposition

Recommended independent PRs:

1. `P14-0A`: exact baseline/source tuple and drift artifact.
2. `P14-0B`: authority/linearization map and ADRs.
3. `P14-0C`: test-only semantic event vocabulary/instrumentation for ordinary SIP.
4. `P14-0D`: CI assertion that all P14 gates default OFF and unknown gates fail closed.

Each PR must be revertible without changing ordinary SIP semantics.

## 13. Exit criteria

P14-0 exits only when:

- exact source/toolchain/admission/provider tuple is pinned;
- drift is classified;
- all existing authority owners are named;
- no Job field is allowed to become an authority ledger;
- ordinary SIP linearization points are documented from live code;
- semantic trace instrumentation exists for the linear synchronous baseline;
- default-off gate behavior is executable;
- fallback remains ordinary SIP.

No P14 runtime claim is permitted at this phase.
