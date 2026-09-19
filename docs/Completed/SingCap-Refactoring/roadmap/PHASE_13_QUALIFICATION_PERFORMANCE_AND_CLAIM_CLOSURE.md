# Phase 13 — Qualification, Performance and Claim Closure

## Goal

Close SingCap-M v1 only after security, concurrency, performance and claim evidence exist for real migrated services and provider boundaries.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Security qualification

Run the full matrix:

```text
capability algebra properties
forgery/replay/restart
subtree revocation
derive/revoke and validate/effect races
quota concurrency/table exhaustion
sealed object wrong type/service/session/incarnation
Region range/lifetime/MOVE/pool/quarantine
SIP raw-object/deep-value/temporary-authority failures
ManagedCap precompiled-assembly bypass corpus
manifest/audit determinism/provenance
HybridCPU admission/publication/generation/cancellation matrix
cross-authority prepare/pin/commit and compensation races
deadlock/lock-order stress across capability/session/seal/Region/external-operation/provider gates
```

Mandatory cross-authority races include operation admission vs Region MOVE/reclaim, admission vs session close, seal resolution vs object close/service restart, publication vs capability revoke/Region reclaim, and provider completion vs service restart. Each test must assert both the winner and the absence of leaked pins/false closure.

## Fuzz/property testing

Minimum properties:

```text
child authority never exceeds parent
revoked/stale/old-realm token never regains authority
quota aggregate never exceeds authoritative budget
Region ownership has exactly one owner after any admitted MOVE sequence
publication never precedes required completion/visibility evidence
```

## Performance baseline

Measure before/after:

```text
capability lookup
single SIP empty call
SIP with N exact capabilities
seal/unseal internal resolution
Region lexical borrow acquire
async lease revalidation
Span read/write loop after acquisition
MOVE
external operation prepare/admit/publish/release
```

No benchmark may hide table lookup inside per-element loops.

Run authority fast-path measurements at 1, 2, 4, 8, 16 and 32 concurrent workers for both shared and unrelated capabilities/subjects/Regions. Report throughput, median, p95, p99 and maximum observed lock wait (or the closest explicitly named runtime measurement). Separate ledger contention from provider latency. A benchmark that serializes unrelated work without reporting it cannot support `ProductionCandidate`.

## Claim closure

Produce a machine-readable and human claim/evidence matrix assigning each feature one level:

```text
ModelOnly
StaticAdmission
RuntimeEnforced
QualifiedManaged
ProductionCandidate
```

No feature inherits a higher claim because a neighboring subsystem is qualified.

## TCB review

Review all assemblies/projects classified TrustedRuntime or NativeIsolated. Confirm the actual isolation primitive for NativeIsolated. Document any unavoidable unsafe/native TCB.

## Supply-chain closure

Record exact:

```text
SingNextOS SHA a67eea1aafc72054d22f1586b62c6883cdc71681
HybridCPU SHA 794c4a53494f503855ac8cf209efab23fde083b2
.NET SDK/runtime/compiler tuple
AdmissionVerifier policy digest
AOT compiler tuple
all external package IDs/versions/SHA-256
manifest/audit schema versions
```

## CI release gate

Required pipeline should include:

```text
locked restore
deterministic build
unit/negative/property tests
generator goldens
ManagedCap analyzer tests
full-module admission fixtures
dependency/native closure
NativeAOT build
service integration
concurrency stress
cross-authority generation-race and compensation suite
lock-order/deadlock stress
HybridCPU provider qualification
single-thread and 1/2/4/8/16/32-worker performance/contention regression gate
audit authority-diff gate
documentation claim check
```

## Exit criteria

All Technical Specification §22 DoD items are satisfied. Remaining unsupported claims are explicitly documented. No `CHERI-equivalent` or equivalent hardware-security wording appears.

## Rollback

A release that fails qualification cannot preserve its previous claim level. Rollback returns to the last fully qualified artifact tuple, including external package digests.
