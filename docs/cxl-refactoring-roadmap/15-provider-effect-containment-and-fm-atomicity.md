# Phase 15 — Provider Effect Containment and FM Atomicity

Status: **Complete for staged single-host software/model scope.**

Phase 16 later closed stale pool/ticket destruction and extended ambiguous
creation containment across generic Fabric Manager and Type-3 paths. Its
869/869 qualification supersedes the historical snapshot below.

## Delta-audit result

The Phase-14 re-audit found that provider unavailability was still usable as a
local release condition, malformed or ambiguous Type-2 responses could lose
closure authority, the Type-2 ABI omitted the fabric binding ID, and Fabric
Manager drain was not atomic with Type-2 admission/provider closure. Those
findings were confirmed against production code and corrected.

## Implemented closure

1. `ProviderUnavailable` is diagnostic only. `ReleasePlan` now requires either
   `ProviderResourcesClosed` or independently proven `ProviderEffectContained`;
   provider loss alone cannot release `RegionUse` or permit region reclaim.
2. Type-2 submission distinguishes `NotAccepted` from ambiguous failure. A
   definitely rejected request is closed locally; ambiguous acceptance remains
   registered and blocks teardown/reclaim.
3. Malformed Type-2 success receipts are compensated exactly. Failed
   compensation retains the submission and external operation in the recovery
   registry, keeping its region uses pinned.
4. Type-2 completion, visibility, publication, release and teardown use one
   symmetric provider-closure/local-release path. Negative completion,
   visibility failure and publication exceptions are also closed; failed
   provider closure remains quarantined.
5. Fabric Manager owns an atomic admission boundary around final validation and
   provider submit. `Bound -> Draining` closes admission before drain begins.
   Every tracked Type-2 operation has a provider-closure callback, so
   reconfiguration closes provider work before releasing the external operation.
   Completed operations are untracked and do not poison later reconfiguration.
6. `CxlAcceleratorRequest` and `CxlAcceleratorSubmission` carry opaque
   `CxlFabricBindingRef { BindingId, Generation }`, distinguishing two bindings
   of one endpoint even when their generations match.
7. Malformed fabric, memory and coherent creation receipts are tracked before
   validation. Failed compensation leaves the provider identity in the bridge
   teardown registry and retains dependent local authority.
8. `PlatformAuthorityStatus.NotAccepted` was appended without renumbering the
   existing status values; a contract test fixes this compatibility invariant.

## Required regression gates

All seven gates requested by the re-audit are executable and passing:

- `MalformedType2Submission_ReleaseFailure_KeepsOperationAndRegionUsesPinned`
- `FailBeforePublication_ProviderReleaseFailure_QuarantinesInsteadOfReclaim`
- `SubmitFailure_AmbiguousAcceptance_DoesNotUseProviderUnavailableAsClosure`
- `Type2Submit_WhileFabricBindingDraining_IsRejectedBeforeProviderEffect`
- `FabricReconfiguration_WithLiveType2Submission_ClosesProviderBeforeExternalOperationRelease`
- `TwoBindingsSameEndpointSameGeneration_AreDistinctInAcceleratorContract`
- `MalformedFabricOrMemoryBinding_CompensationFailure_RemainsTrackedForTeardown`

Additional tests cover faulted completion, unsatisfied visibility, publication
exceptions, completed-operation FM untracking and stable platform-status values.

## Executable evidence

```text
focused Phase 11/14/15 matrix: 99/99 passed
fresh forced no-cache restore: passed for 26 projects
full solution: 857/857 passed
  727 SingPlus.Tests
   60 SingPlus.Platform.HybridCpu.Tests
   58 HybridCPU_NeutralRuntime.Tests
   12 HybridCpu_ExecutableAdapter.Tests
failures: 0
skipped: 0
git diff --check: passed (line-ending notices only)
```

## Scope boundary

`DirectCoherentWrite` remains `FutureGated`. QEMU, FPGA, physical-hardware
coherence, IDE/security and Fabric Manager enforcement are excluded from this
software/model qualification and are not inferred from model tests.
