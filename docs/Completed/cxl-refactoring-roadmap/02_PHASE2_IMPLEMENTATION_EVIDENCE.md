# Phase 2 — Common External Operation Lifecycle Evidence

## Status and delta audit

**Complete for the provider-neutral Phase 2 lifecycle and non-CXL mock vertical
slice.** The delta audit confirmed that existing DMA, DSC1, mapping revocation,
visibility and response publication paths already contain useful narrow
lifecycles, but none is the common seven-state OS/runtime contract. They remain
unchanged and are not relabelled as common-lifecycle or CXL proof.

Phase 1 RegionUse remains the memory authority input. Phase 2 adds operation
identity and ordering only; `OperationGeneration` is not authority and cannot
refresh a stale RegionUse or dependency generation.

## Exact lifecycle route

```text
PrepareExternalOperation
  Prepared: intent + exact logical region-use requests; no provider effect

AdmitExternalOperation
  process/effect validation
  -> RegionAuthority acquires every RegionUse transactionally
  -> region mutation + logical platform/device dependency snapshot
  -> Admitted

RecordExternalOperationSubmission
  dependency + RegionUse revalidation at the last no-effect boundary
  -> provider acceptance is recorded as an opaque local OperationBinding
  -> Submitted

RecordExternalOperationCompletion
  exact operation/binding/generation match
  -> DeviceComplete (never visibility, publication or ownership return)

RecordExternalOperationVisibility
  exact visibility requirement + positive evidence
  -> Visible (never application publication)

PublishExternalOperation
  dependency + RegionUse revalidation
  -> execute the supplied publication action
  -> Published only if the action succeeds

ReleaseExternalOperation
  exact provider closure/loss and disposition validation
  -> idempotent RegionUse release
  -> Released (never implicit MOVE/borrow/device-lease release)
```

Every state change or containment decision produces a logical transition log.
The log distinguishes `DeviceComplete`, `Visible`, `Published` and `Released`.
It contains no provider or physical hardware identity.

## Cancellation, reset/loss and teardown decisions

- `Prepared`/`Admitted`: cancellation records `CancelledBeforeSubmit`; release
  has no provider precondition and no hardware effect is claimed.
- `Submitted`: cancellation records `CancellationPending`. Unsupported provider
  cancellation requires drain; it does not synthesize completion.
- exact cancelled completion reaches `DeviceComplete/Cancelled`, after which
  verified provider closure permits release.
- completed staged output can be discarded before publication.
- direct coherent output cannot be described as discarded after completion;
  cancellation/revalidation failure is fault-contained and remains pinned
  until the stronger Phase 7 publication/replay policy exists.
- provider loss/reset after staged submission explicitly invalidates local uses,
  records `ProviderLost`, forbids publication and allows local cleanup using a
  `ProviderUnavailable` release plan. This is containment evidence, not proof
  that hardware completed successfully.
- process teardown automatically cancels and releases pre-submit work. Accepted
  work blocks reclaim as `PlatformBindingDraining` until exact terminal
  completion or provider-loss containment is recorded.

An active writable RegionUse now also blocks ownership transfer and direct
region release. This preserves pinned direct-write custody; read-only captured
uses may still be invalidated by an authority-mediated MOVE as required by
Phase 1 stale-generation semantics.

## Executable positive and negative evidence

`ExternalOperationLifecycleTests` ports a deterministic non-CXL mock operation
to the common lifecycle and proves:

- the exact seven-state happy path and transition log;
- `DeviceComplete` and `Visible` do not expose staged output;
- submit without admission and successful-state skips fail closed;
- invalid edges from every successful lifecycle state fail closed;
- stale operation generations, wrong bindings and duplicate completions do not
  advance state;
- stale region mutation or dependency generations prevent publication before
  its action executes;
- visibility failure never publishes;
- cancellation before/after submit, exact cancelled completion and staged
  discard semantics;
- direct coherent cancellation is fault-contained and pins writable authority;
- provider loss/reset cannot publish and idempotent local release succeeds for
  staged output;
- teardown releases pre-submit work but drains submitted work;
- the public lifecycle surface has no CXL/HDM/DPA/HPA/PASID/requester/physical
  route/provider-token identity.

Final Phase 2 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused authority/lifecycle/teardown/public-surface tests
  PASS — 229/229

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 779/779 total
    649 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Claims, gates and Phase 3 entry

Current claim: SingNextOS has one provider-neutral operation state machine and
a deterministic mock integration over RegionUse. It does not claim that legacy
DMA/DSC1 providers have already migrated, nor that a mock binding is hardware
execution, coherence, visibility or CXL evidence.

`FutureGated`: production DMA/accelerator adapters to the common lifecycle,
range subdivision, direct coherent publication/replay, compute planning and all
CXL provider roles.

`Unavailable`/`ExternalBlocked`: CXL hardware discovery/coherence, Type-2 and
Type-3 hardware, HDM programming, IDE, Fabric Manager enforcement, QEMU/real
hardware qualification, zero-copy proof and writable multi-host sharing.

Phase 3 may begin only after fresh restore, focused regression tests, full
qualification and dirty-worktree review pass. It must add semantic
ComputeIntent, explicit policy/capability selection and a dependency DAG over
this lifecycle. Provider capability must remain evidence for planning, never
memory authority.
