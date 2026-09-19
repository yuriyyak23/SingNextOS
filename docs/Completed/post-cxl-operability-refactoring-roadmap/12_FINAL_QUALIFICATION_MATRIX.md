# Phase 12 Final Executable Qualification Matrix

This matrix names the executable proof for every mandatory negative category and end-to-end scenario. A row is qualified only by the actual focused/full commands recorded in `12_PHASE_12_IMPLEMENTATION_EVIDENCE.md`. Test/model evidence is not a hardware claim.

## Negative matrix

| Area | Required negative property | Executable proof |
|---|---|---|
| Supervisor | stale generation, dependency policy, fresh restart authority, bounded crash loop, ambiguous replacement block | `Phase02ServiceSupervisorTests.StaleHandleAndRevokedSupervisorCapabilityFailClosed`; `DependencyReplacementRebindsDependentToFreshExactGeneration`; `PlannedReplacementUsesFreshProcessCapabilityAndStableServiceLineage`; `BoundedRestartPolicyEntersCrashLoop`; `AmbiguousExternalEffectQuarantinesAndBlocksReplacement` |
| Inspector | unauthorized projection, redaction, stale/current separation, no mutation authority | `Phase03AuthorityInspectorTests.SelfScopedInspectorCannotInspectUnrelatedRegion`; `WhyMoveBlockedReportsPlatformMappingWithoutProviderPrivateIdentity`; `StaleRegionIsRepresentedSeparatelyAndCannotAuthorizeMutation`; `InspectorDtosContainNoReusableAuthorityOrProviderTopology` |
| Deadline/cancellation | pre-effect close, submitted/post-effect pinning, stale scope, published irreversibility | `Phase04DeadlineCancellationTests.DeadlineBeforeExternalAdmissionCancelsLocallyWithoutRegionPins`; `SubmittedCancellationKeepsPinsUntilExactProviderClosure`; `CancellationAfterDeviceCompleteSuppressesStagedPublicationButStillRequiresClosure`; `StaleCancellationGenerationIsTypedAndCannotMutateLiveScope`; `CancellationAfterPublicationCannotUndoPublication` |
| Budgets | parent limit, concurrent no-overcommit, stale release, crash pin, replacement accounting | `Phase05ResourceBudgetTests.ChildServiceBudgetCannotExceedConfiguredSystemParent`; `ConcurrentReservationsCannotOvercommitAndStaleReleaseCannotFreeCurrentCharge`; `CrashWithSubmittedExternalEffectKeepsChargeUntilExactClosure`; `Phase02ServiceSupervisorTests.PlannedReplacementUsesFreshProcessCapabilityAndStableServiceLineage` |
| Trace/replay | observation only, incomplete marker, divergence, HybridCPU evidence non-authority | `Phase06DeterministicTracingTests.TraceDtosContainMetadataOnlyAndNeverAuthority`; `OverflowIsExplicitAndBackpressureIsTestOnlyDisposition`; `DeterministicModelReplayReportsTypedSemanticDivergence`; `HybridCpuEvidenceCorrelationCannotRestoreRevokedCapability` |
| IPC v2 | exact MOVE owner, borrow lifetime, deadline/crash safety, scatter bounds, semantic path equivalence | `Phase07IpcV2Tests.PreAdmissionMoveFailureRetainsSenderAndSuccessfulMoveHasOneOwner`; `BorrowIsReadOnlyAndCancellationDoesNotFabricateReturn`; `Phase04DeadlineCancellationTests.IpcDeadlineAfterMoveDoesNotReturnOrDuplicateOwnership`; `Phase07IpcV2Tests.ReceiverCrashAfterMoveReclaimsReceiverOwnershipWithoutRestoringSender`; `ScatterGatherValidatesBoundsOverlapModesAndCopiesSegments`; `CopyIsValueIndependentAndReceiptNeverPromisesZeroCopy` |
| Checkpoint | live resource refusal, partial/tampered/incompatible refusal, fresh generation, stale authority | `Phase08OrdinaryCheckpointTests.LiveExternalOperationBlocksThenExactClosureAllowsCheckpoint`; `PartialTamperedAndIncompatibleImagesNeverRestore`; `PlannedCheckpointReplacementRestoresLogicalMemoryWithFreshAuthority`; `FreshCapabilityAdmissionCanFailWithoutReusingSerializedAuthority` |
| Telemetry | self scope, cross-tenant denial, topology redaction, evidence separation, stale subscription | `Phase09StructuredTelemetryTests.SelfProjectionReflectsAuthoritativeBudgetMemoryDeadlineTraceAndCheckpointState`; `CrossServiceProjectionRequiresExactDedicatedCapabilityAndLeaksNoHostTopology`; `SecurityEvidenceProjectionRemainsTypedDataAndCannotBeReplacedByTelemetry`; `BoundedSubscriptionReportsOverflowAndOldGenerationCannotReadAfterRestart` |
| Provider conformance | pre-effect rejection, ambiguous pins/accounting, malformed/stale receipt, post-effect exception, reset exactness | `Phase10ProviderConformanceTests.NativeExternalOperationModelPassesReusableFaultMatrix`; `HybridCpuExecutableAdapterBoundaryPassesSameFaultMatrix`; common `ProviderConformanceSuite` validates all ten deterministic modes twice |
| Platform reset prerequisite | stale generation and pinned reservation, virtual-domain quarantine, failed reset does not advance epoch | `PlatformBackendResetEpochTests.ResetMakesExistingDomainAndMappingStaleAndKeepsReservationPinned`; `ResetQuarantinesLocalVirtualDomainAndStalesItsPlatformBinding`; `ResetWithoutConfiguredProviderFailsWithoutAdvancingEpoch` |

## End-to-end scenarios

| # | Scenario | Executable proof |
|---:|---|---|
| 1 | startup → IPC → external effect → publication → exact shutdown/replacement | `Phase11CrossCuttingIntegrationTests.ManagedLifecycleComposesDependenciesMoveExternalEffectObservabilityCheckpointAndReplacement` plus `ExternalOperationLifecycleTests.MockNonCxlOperationTraversesAllSevenStatesWithoutConflation` |
| 2 | exact dependency replacement/rebind | `Phase02ServiceSupervisorTests.DependencyReplacementRebindsDependentToFreshExactGeneration` |
| 3 | crash after submission with cancellation/closure | `Phase02ServiceSupervisorTests.SubmittedClosableOperationDrainsBeforeReplacement`; `Phase05ResourceBudgetTests.CrashWithSubmittedExternalEffectKeepsChargeUntilExactClosure` |
| 4 | ambiguous acceptance quarantines and blocks replacement | `Phase11CrossCuttingIntegrationTests.AmbiguousProviderLossIsExplainedObservedAndBlocksReplacementUntilContainment` |
| 5 | deadline/cancellation at pre-admission, submitted, complete/visible, published | the five Phase 04 lifecycle tests listed above |
| 6 | budget exhaustion and exact recovery | `Phase11CrossCuttingIntegrationTests.DeadlineAfterAcceptanceAndBudgetPressurePreservePinsAndAuthorityUntilExactClosure`; `Phase05ResourceBudgetTests.OwnedMemoryBudgetGatesAllocationAndRecoversOnExactRelease` |
| 7 | MOVE under deadline/crash races | `Phase04DeadlineCancellationTests.IpcDeadlineAfterMoveDoesNotReturnOrDuplicateOwnership`; `Phase07IpcV2Tests.ReceiverCrashAfterMoveReclaimsReceiverOwnershipWithoutRestoringSender` |
| 8 | trace + inspector explain blocked reclaim | `Phase11CrossCuttingIntegrationTests.AmbiguousProviderLossIsExplainedObservedAndBlocksReplacementUntilContainment`; `Phase03AuthorityInspectorTests.ProcessAndServiceDrainQueriesReportExactExternalOperationClosureDependency` |
| 9 | checkpoint replacement with fresh generation | `Phase08OrdinaryCheckpointTests.PlannedCheckpointReplacementRestoresLogicalMemoryWithFreshAuthority` |
| 10 | provider reset/loss with truthful supervisor, trace, telemetry, and pins | `Phase11CrossCuttingIntegrationTests.AmbiguousProviderLossIsExplainedObservedAndBlocksReplacementUntilContainment`; Phase 10 `ResetDuringSubmitted`/`ResetDuringVisible` common scenarios; `PlatformBackendResetEpochTests` |

## Boundary gates

- Architecture and public-surface regressions enforce provider-neutral layering and absence of CXL/HybridCPU internals from ordinary contracts.
- HybridCPU execution remains at `tools/HybridCpu_ExecutableAdapter`; no core/ISA/compiler/scheduler/runtime-legality/microarchitecture/architecture change is part of this roadmap.
- Trace, telemetry, inspector, manifest digest, replay evidence, checkpoint data, health, and budget state remain non-authoritative.
- Hardware, production performance, confidential checkpoint, cross-host migration, arbitrary live replay, and unprivileged production fault injection remain explicitly FutureGated.
