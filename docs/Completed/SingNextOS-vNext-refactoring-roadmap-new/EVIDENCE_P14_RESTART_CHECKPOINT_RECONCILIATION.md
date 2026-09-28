# P14 evidence — restart, checkpoint and reconciliation

## Disposition

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry/qualification HEAD: `cf66d014fd0fc19cdcabca2ef1b5b974805ecda1`.
- Claim: host/JIT `RuntimeEnforced` for ordinary component checkpoint/fresh-generation restore and in-process provider-generation/quarantine handling of the enabled ComputeTime contour.
- No new feature gate is enabled. Cold runtime-process recovery/persistent quarantine journal is excluded.
- P12–P13 dirty work and prior user/parallel SipJob P14 evidence were preserved and not attributed to this phase.

## Owner and transition chain

Ordinary checkpoint persists logical bytes and selected owned-memory content only. Capability, budget, cancellation and telemetry entries are `RecreateOnRestore`; active sessions, ExternalOperations, unsafe Regions, platform/private state and now every active ComputeTime reservation/lease are non-checkpointable. Restore requires exact image digest, compatible manifest/image, a strictly newer process generation and fresh `AdmitComponent`; old capability and lease handles remain bound to the retired generation.

`ResourceBudgetAuthority` remains the only lease owner. Reserved/bound/consuming/quarantined/reconciled leases appear in its inspection snapshot and block checkpoint. Pre-submit terminal cancellation removes the blocker. Provider loss after possible submit remains quarantined and cannot refund; P07 exact reconciliation/settlement remains the only quantitative closure. Settlement does not release Region use or publish output.

## Defect and remediation

Checkpoint already rejected a live ExternalOperation but did not independently reject an active ComputeTime lease before operation binding. `ClassifyCheckpoint` now adds a generation-correlated `Budget/NonCheckpointable` disposition for every live ComputeTime reservation owned by the exact source process generation. The image never contains the lease handle as restorable authority. Regression proves blocking, terminal cancellation, fresh-generation restore, and rejection of the old handle after restore.

Existing tests cover process/service generation ABA, session stale generations, provider generation drift/late evidence, provider-loss quarantine, duplicate settlement/refund, checkpoint tamper/incompatibility, and Region independence. Applicable invariants reviewed: VNX-001, VNX-004, VNX-007, VNX-008, VNX-010 through VNX-015, VNX-021, VNX-022, VNX-025 through VNX-028.

## Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~VNextPhase03ResourceLeaseTests|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests|FullyQualifiedName~SingCapPhase13CrossAuthorityRaceTests" --verbosity minimal
  Passed 26, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1653, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one user-owned SipJob P14 tuple/HEAD coupling failure, and one security-profile project-list drift.

## FutureGated and exclusions

Cold runtime-process restart with a durable reconciliation journal remains FutureGated. Owner: `ResourceBudgetAuthority` plus an authenticated local recovery journal and exact provider reconciliation adapter. Missing evidence: crash-after-every-transition durable replay, atomic journal/ledger commit, provider-generation recovery and conservative worst-case closure. Bypassing it could resurrect a serialized lease or lose a quarantined charge.

Ordinary SIP fallback remains intact. Checkpoint descriptors, receipts, telemetry and caches remain non-authoritative. No automatic refund/reclaim, non-compute-family persistence, production, NativeAOT, hardware, QEMU, firmware or CXL boot claim is made. No HybridCPU ISA or microarchitecture work was performed.
