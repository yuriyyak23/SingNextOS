# Phase 7 — Visibility, Publication and Replay Evidence

## Implemented contract

**Complete for the SingNextOS boundary.** Provider-neutral
`ExternalEffectClass` defines `StagedReversibleUntilPublish`,
`SnapshotOrIdempotenceRequired` and `IrreversibleBarrier`.
`ExternalReplayProtection` records only semantic snapshot/restore,
idempotence or deduplication promises; no HybridCPU replay token or certificate
is imported.

Every operation now carries an `ExternalEffectPolicy` and exposes an
`ExternalEffectBoundaryState`. Staged operations are `StagedPending` after
submit and become `ExternallyVisible` only when the publication action succeeds.
Direct operations require replay-consumer acknowledgement plus either concrete
replay protection or an explicit irreversible barrier. Their boundary is
crossed at submit because a later failure cannot prove that the write did not
occur.

`DeviceComplete`, `Visible`, `Published` and `Released` remain distinct.
Publication still revalidates mutation/use and dependency generations, and its
action is invoked exactly once. Provider loss on staged work invalidates and
discards; provider loss after direct/irreversible submission faults and remains
pinned, without a rollback claim.

The Phase-6 Type-2 service now passes the typed effect policy into common
admission. Its former boolean direct-path gate has been removed.

## Executable evidence

Focused effect, lifecycle and Type-2 tests prove:

- coherent/direct capability without classification is rejected;
- snapshot/idempotence class without a concrete protection is rejected;
- an irreversible direct boundary is observable immediately after submit and
  survives reset as a fault, never as fake rollback;
- staged output crosses the visible-effect boundary only at publication;
- duplicate publication does not execute the publication action twice;
- read-only observations remain staged/non-barrier by default;
- mutation before staged publication and visibility failure remain fail-closed
  in the common lifecycle suite.

Final Phase 7 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused effect/lifecycle/Type-2 tests
  PASS — 21/21

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 812/812 total
    682 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## External integration boundary

Mapping these semantic states to HybridCPU replay behavior remains an external
HybridCPU-v2 contract. SingNextOS now exposes the information required for that
mapping without claiming that CXL coherence supplies replay or rollback.
