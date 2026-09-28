# SingCap-M Requirements Traceability and CI Gates

**Baseline:** SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

This file is the compact phase/test mapping. The Technical Specification remains normative for requirement text.

## Traceability matrix

| Requirement family | Current basis | Main gap | Implementing phase | Mandatory evidence |
|---|---|---|---|---|
| CAP-001..006 | `CapabilityAuthority`, V1 descriptor/tests | opaque reference, realm, resource generation, capacity | P02/P05 | forgery/restart/exhaustion tests |
| CON-001..004 | rights subset in V1 delegation | typed dimensions, ranges, aggregate quota | P03 | property + concurrency quota tests |
| REV-001..008 / ACP-001..007 | direct revoke/domain epoch; separate owner registries | subtree, effect policy, cross-authority prepare/pin/commit | P04/P06/P07/P08/P12 | barrier stress + composed operation lease/pin/compensation tests |
| SEAL-001..007 | session-bound file/socket handles | generic stable sealing | P06/P11 | wrong type/service/session/restart tests |
| REG-001..010 | RegionAuthority, OwnedBuffer, BorrowLease; no internal Region synchronization at audit baseline | subrange/read-write/epoch/quarantine/zeroize + catalog/record linearization | P07 | range/lifetime/MOVE/provider-loss/deadlock tests |
| SIP-001..007 | EndpointSession, generator/analyzer | exact projection, deep values, non-ambient sentry | P08 | generator goldens + negative invocation tests |
| MCP-001..010 | analyzer + AdmissionVerifier | ManagedCap policy/full-module/native closure/AOT + positive framework member surface | P01/P09 | hostile compiled fixture and unknown/drifted-BCL corpus |
| MAN-001..006 | ServiceManifestV1 + admission proof | security profile/audit/provenance/version drift | P10 | canonical audit + authority-diff tests |
| EXT-001..007 | HybridCpuExternalOperationProvider + current HCPU contracts | new admission/publication binding qualification | P12 | cross-project conformance matrix |
| PERF-001..007 | existing normal Span path | regression bounds/caches/contention scaling | P07/P13 | single-thread + 1/2/4/8/16/32-worker percentile/lock-wait gate |
| DIAG-001..003 | inspection/telemetry surfaces | authority leakage/zeroization policy | P02/P07/P10 | diagnostic redaction + reuse tests |

## Cross-phase mandatory CI

Starting with P02, every security PR must run:

```text
build + existing tests
new positive tests
new negative tests
relevant concurrency/property tests
architecture dependency checks
Authority Composition owner/pin/lock/provider-boundary checks where an effect path is touched
generator/analyzer goldens where touched
admission proof/audit determinism where touched
```

Starting with P09 add:

```text
full-module ManagedCap fixtures
transitive dependency/native asset verification
NativeAOT selected fixture
```

Starting with P12 add:

```text
exact HybridCPU contract package digest/source commit check
external provider conformance matrix
```

P13 makes all of the above release-blocking.

## Required PR security checklist

Every PR description answers:

```text
Does this add a new identity or generation?
Can the identity become authority by accident?
Where is authoritative state stored?
Is there any second ledger/table for the same truth?
Can child authority exceed parent?
What is the revocation linearization point?
Which closed EffectRevocationPolicy class applies at admit/submit/publish/release?
Can a copied handle duplicate consumable state?
What happens after process/service/runtime restart?
What happens on counter/table exhaustion?
What happens on exception/cancellation/provider loss?
Can the token be serialized/replayed?
Can reflection/native code bypass it?
Does RegionAuthority remain the only memory ownership truth?
Does completion remain distinct from publication/release?
Which registries own the composed effect, and what exact leases/pins bind one admission attempt?
What is the prepare/commit validation point, lock order and reverse compensation order?
Can any provider callback, blocking wait or user code run under an authority lock?
Does provider evidence remain distinct from SingNext authority?
Does the audit artifact show the new declared/static authority?
Does HybridCPU ISE remain unchanged?
```

## Claim evidence rule

No parser success, metadata presence, test fake backend, telemetry event, audit JSON or provider DTO constructor is sufficient evidence of runtime enforcement. Each claim must cite the exact production gate and tests/conformance that exercise it.
