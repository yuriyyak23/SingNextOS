[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p10-ras-failure-domains')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$filter = 'FullyQualifiedName~FaultReconciliationPolicyV1Tests|FullyQualifiedName~RasFailureContractsV1Tests|FullyQualifiedName~V6RasFailureConsequenceTests|FullyQualifiedName~V6BoundedEvidenceReconcilerTests|FullyQualifiedName~Phase10ProviderConformanceTests|FullyQualifiedName~CxlType3MemoryProviderTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff|FullyQualifiedName=SingPlus.Tests.Runtime.ExternalOperationLifecycleTests.ProviderLossRequiresExplicitClosureOrContainmentBeforeLocalRelease|FullyQualifiedName=SingPlus.Tests.Runtime.V6MemoryRuntimeEnforcementTests.ProviderLossProjectsToQuarantineWithoutInventingRelease|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.CancellationReceiptDistinguishesPreSubmitClosureFromPendingPostSubmitEffect|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.ReconfigureAfterSubmitRejectsStaleClosureAndKeepsRegionPinned|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.ReconfigureBeforeSubmitClosesLocalAdmissionWithoutInventingEffect|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.ReconfigureAfterCompletionOrVisibilityProjectsProviderLossWithoutPublication|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.ReconfigureAfterReleasePreservesPublishedTerminalDecision|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.ReconfiguredOperationRequiresCurrentGenerationAndExplicitClosureForReconciliation|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.ReconfigureGenerationAbaNeverReauthorizesOldRequest|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.PublicationCallbackReentryIsFailClosedAndGenerationChangeWaitsForBoundaryExit'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.TrustedBackendResetDuringSubmitPinsPossibleEffectWithoutProviderIncarnationDrift|FullyQualifiedName~PlatformDmaSubmissionTests.ProviderThrowDuringGrantRevokePinsMappingAfterPossibleClosure'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaSubmissionTests.AlreadyRevokedStatusWithoutResetCannotProveExactGrantClosure'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaGrantTests.RejectedProviderGrantRevokedStatusDoesNotProveCleanup|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.ProviderRevokedStatusCannotCloseExactDeviceLease'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.DeviceBindReceiptLossQuarantinesParentDomain'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetInsideRootBindCallbackPinsSubjectWithoutInventingLease|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.LostRootBindReceiptPinsProcessReclaimWithoutProviderLease|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.RootBindCallbackRejectsDuplicateAdmissionBeforeProvider|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ProcessExitInsideRootBindCallbackRevokesPublishedBindingBeforeReclaim|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ParentRevokeCallbackRejectsNewMappingBeforeProvider|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.MappingCallbackCannotRevokeParentBeforePublication|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.MappingReceiptLossPinsParentAndLocalReservation|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.BackendResetInsideMappingCallbackFaultPinsLateLease|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ProcessExitInsideMappingCallbackTracksLateMappingForExactTeardown|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.LostMappingReceiptBlocksProcessReclaim|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetFaultedMappingKeepsBudgetAndRegionPinnedAcrossTeardownRetries|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetBeforeNotAcceptedMappingReplyDoesNotReleaseRegion|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.StableNotAcceptedMappingReplyReleasesLocalReservation'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.DeviceBindCallbackBlocksParentRevokeBeforeLeasePublication|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.ResetInsideDeviceBindCallbackPinsNewParentGeneration|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.ParentRevokeCallbackRejectsDeviceBindBeforeProvider'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ReentrantVirtualIoRevokeCannotEnterProviderTwice'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ReentrantChildCloseCannotInvokeProviderTwice|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.NestedCreateCallbackBlocksImmediateParentTransition|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.ImmediateParentTransitionCallbackRejectsNestedCreateBeforeProvider'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ChildCreateCallbackPinsParentBeforeChildBindingPublication|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ParentRevokeCallbackRejectsChildCreateBeforeProvider'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetInsideChildCreateCallbackPinsNewParentGeneration|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.ResetInsideNestedCreateCallbackCannotPublishLateChild'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.BackendResetPreservesUnknownDeviceBindPin|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.BackendResetPreservesUnknownChildCreatePin|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.NestedCreateReceiptLossPinsImmediateParentAndRoot'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ParentDomainWaitsForExactPublishedChildClosure|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ChildCreateReceiptLossOrAmbiguousStatusPinsParent|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.MalformedChildCreateRequiresExactCleanupBeforeParentClose'
$filter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.GuestMapReceiptLossOrAmbiguousStatusPinsChildAndParentMapping|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.VirtualIoBindReceiptLossOrAmbiguousStatusPinsChildAndDevice|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.BackendResetDuringGuestOrVirtualIoAdmissionCannotPublishChild'
$filter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.MalformedGuestOrVirtualIoAdmissionNeedsExactCompensation'
$filter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ParentAuthorizationRevokedInsideChildAdmissionPinsPossibleEffect|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ProviderIncarnationDriftInsideChildAdmissionPinsParent'
$filter += '|FullyQualifiedName~Phase8ResidualVirtualizationTests.ExecutableArtifactBindOrStartReceiptLossPinsChildAndMapping|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.RevokedParentMappingCannotReachExecutableChildStartProvider'
$filter += '|FullyQualifiedName~Phase8ResidualVirtualizationTests.BoundArtifactPinsOnlyItsExactGuestMappingAfterCallbackSettles'
$filter += '|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.TypedSipV3ArtifactContourPinsMappingAfterRetiredWorkUntilReleaseEvidence'
$filter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ChildTransitionCallbackFaultOrResetCannotPublishLateState|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ConcurrentChildTransitionHasOneProviderCallback'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.TransitionCallbackRejectsGuestMapEventAndTrapBeforeProvider|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ChildEffectCallbackRejectsOverlappingTransition|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.VirtualEffectCallbackFaultOrResetCannotPublishLateEvidence'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.VirtualIoBindCallbackRejectsOverlappingChildTransition|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ChildTransitionCallbackRejectsVirtualIoBindBeforeProvider|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.ReentrantExecutableStartCannotEnterProviderTwice'
$filter += '|FullyQualifiedName~PlatformDeviceLeaseTests.DeviceBindNonAcceptanceWithoutExactNoEffectPinsParentDomain'
$filter += '|FullyQualifiedName~PlatformIrqBindingTests.RevokedOrThrowingInterruptClosureKeepsDevicePinned|FullyQualifiedName~PlatformMmioLeaseTests.RevokedOrThrowingMmioClosureKeepsDevicePinned'
$filter += '|FullyQualifiedName~PlatformIrqBindingTests.MalformedInterruptCleanupWithoutExactSuccessPinsParentDevice|FullyQualifiedName~PlatformMmioLeaseTests.MalformedMmioCleanupWithoutExactSuccessPinsParentDevice'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformIrqBindingTests.InterruptBindReceiptLossPinsParentDevice|FullyQualifiedName=SingPlus.Tests.Platform.PlatformMmioLeaseTests.MmioMapReceiptLossPinsParentDevice'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.EffectPublicationSemanticsV1Tests.ProviderLossDuringAmbiguousPublicationClosureCannotClearPossibleEffect'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.EffectPublicationSemanticsV1Tests.RegionDamageDuringPublicationCannotBecomePublishedEvidence'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ExternalOperationLifecycleTests.ProviderLossInvalidatesWritableUseAtTerminalMutationEpochWithoutReclaim'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ExternalOperationLifecycleTests.ProviderLossWithBrokenRegionUseBindingReportsFailureAndRetainsOwnerPin'
$filter += '|FullyQualifiedName~ExternalOperationLifecycleTests.LateCancellationCannotEraseProviderLossConsequence'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.ReconfigureGenerationAbaInsideAmbiguousPublicationCannotReauthorizeOldRequest'
$filter += '|FullyQualifiedName=SingPlus.Tests.Ownership.RegionUseTests.ExhaustedMutationEpochCannotReleaseOrInvalidateWritableUse'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ExternalOperationLifecycleTests.MultiRegionReleaseFailureLeavesEveryUsePinned'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ExternalOperationLifecycleTests.FailedAdmissionCleanupRetainsAcquiredUseUnderOperationOwner|FullyQualifiedName=SingPlus.Tests.Runtime.ExternalOperationLifecycleTests.FailedAdmissionWithSuccessfulCleanupLeavesNoRegionUse'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.PublishedOperationCannotReleaseRegionWhileResourceSettlementIsInFlight'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.ProviderLossWithQuarantinedResourceBindingCannotReleaseRegion'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.FailedRegionInvalidationStillQuarantinesResourceBoundProviderLoss'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.FailedBudgetQuarantineReportsUncontainedProviderLossAndKeepsResourcePinned'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.ProviderLossDuringExactSettlementRetainsLossAndCompletesBudgetReceipt'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.ProviderLossAfterBudgetSettlementAcceptsExactChargeBeforeBindingCompletion'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.ProviderLossBindingReadRacesExactSettlementWithoutFalseQuarantineFailure'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.PriorDifferentBudgetTerminalChargeCannotCompleteExactReceipt'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase16DurableResourceAdmissionTests.JournalFailureAfterBudgetSettlementAndProviderLossKeepsColdChargeConservative'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase16DurableResourceAdmissionTests.RetryAfterDurableTerminalRecordDoesNotAppendSecondReceipt'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase16DurableResourceAdmissionTests.LiveJournalRollbackCannotLowerObservedRecoveryCharge'
$filter += '|FullyQualifiedName=SingPlus.Tests.Ownership.RegionUseTests.DomainReclaimPreflightsLaterInvalidatedUseBeforeReleasingEarlierRegion'
$filter += '|FullyQualifiedName=SingPlus.Tests.Ownership.RegionUseTests.ConcurrentAllocationAndTeardownLeaveNoOwnedRegion'
$filter += '|FullyQualifiedName=SingPlus.Tests.Ownership.RegionUseTests.ConcurrentTransferAndTargetTeardownLeaveNoRegionOwnedByExitedTarget'
$filter += '|FullyQualifiedName=SingPlus.Tests.Contracts.ProtocolRuntimeTests.ConsumingSendAndReceiverTeardownLeaveNoRegionOwnedByExitedReceiver|FullyQualifiedName=SingPlus.Tests.Contracts.ResponsePublicationRuntimeTests.OwnershipResponseAndRequesterTeardownLeaveNoRegionOwnedByExitedRequester'
$filter += '|FullyQualifiedName=SingPlus.Tests.Contracts.ProtocolRuntimeTests.ConcurrentChannelCreationAndTargetTeardownLeaveNoOpenEndpoint'
$filter += '|FullyQualifiedName=SingPlus.Tests.Contracts.ResponseClientAdapterTeardownTests.ConcurrentCancellationAndTeardownFinalizeOneResponseAndInvalidateEndpoint'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ServiceDiscoverySessionTests.ExpirationWithInlineEffectPinDefersChannelCloseUntilSettlement'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ServiceDiscoverySessionTests.ConcurrentOpenAndProviderTeardownLeaveNoActiveSessionOrOpenChannel'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ServiceDiscoverySessionTests.ConcurrentServiceRegistrationAndProviderTeardownLeaveNoDiscoverableDescriptor'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.ConcurrentReceiveAndSessionCloseCannotDeliverAfterClose'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.ClosedSessionRetainsAcceptedUnsettledInvocationAndBlocksProcessReclaim|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.ClosedSessionCanReclaimWhenRequestWasNeverAccepted'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.CallerCancellationBeforeServiceAcceptanceSettlesAsCancelledWithoutExecutionAcceptance|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.AcceptedCancelledResponseDoesNotProveEffectClosureForProcessReclaim|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.AcceptedCancellationAndSessionCloseRetainPossibleEffectInEitherOrder'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.FailedAcceptedInlineSettlementDoesNotAssertEffectContainment'
$filter += '|FullyQualifiedName~EndpointSessionCancellationTests.FailedQueuedSettlementCallbackRetainsPossibleEffectWithoutServiceAcceptance'
$filter += '|FullyQualifiedName~PlatformBorrowReadGrantTeardownTests|FullyQualifiedName=SingPlus.Tests.SipJobs.Phase142ClosedValueDirectContourTests.ServiceFaultAfterInlineBeginPreventsSentryEntryAndCannotReplayBinding|FullyQualifiedName=SingPlus.Tests.SipJobs.Phase143InlineRegionBorrowTests.ServiceTerminationBeforeFinalRevalidationInvalidatesBorrowAndUseWithoutRetry|FullyQualifiedName=SingPlus.Tests.SipJobs.Phase143InlineRegionBorrowTests.StaleServiceDuringUseReleaseStillRunsReverseCleanupAndTerminatesLease|FullyQualifiedName=SingPlus.Tests.SipJobs.Phase143InlineRegionMoveTests.ConsumerTerminationAfterSecondMoveUsesOwnerReclaimAndNeverMovesBackToCaller|FullyQualifiedName=SingPlus.Tests.NativeServices.NativeSystemServiceVerticalSliceTests.ServiceFaultAfterSessionResolutionPreventsGeneratedSentryAdmission'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase06ResourceDonationTests.PossibleSubmitCannotDowngradeDonationToPreSubmitReturn|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase06ResourceDonationTests.PossibleSubmitAndPreSubmitReturnHaveOneDonationWinner'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase06ResourceDonationTests.GeneratedSubmitBlocksLatePreSubmitReturnDuringSessionDrain|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase06ResourceDonationTests.SessionCloseWithoutLivePinQuarantinesBoundDonationInsteadOfAssumingPreSubmitClosure'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase06ResourceDonationTests.GeneratedSubmitAfterSessionCloseWithoutPinCannotRefundOrCallProvider'
$filter += '|FullyQualifiedName~EndpointSessionCancellationTests.AmbiguousSettlementBlocksDonationBindingAndActivation|FullyQualifiedName~EndpointSessionCancellationTests.DonationReturnReservationExcludesSettlementAndPreservesFaultConsequence'
$filter += '|FullyQualifiedName~VNextPhase06ResourceDonationTests.NestedDonationPreservesProvenanceAndConservesOneLeaseLineage'
$filter += '|FullyQualifiedName~VNextPhase06ResourceDonationTests.ConcurrentDuplicateNestedDonationHasOneSplitAndPreservesHeldCapacity'
$filter += '|FullyQualifiedName~EndpointSessionCancellationTests.SettlementAdmissionWaitsForCorrelationGateAndCallbackReleasesIt'
$filter += '|FullyQualifiedName~VNextPhase06ResourceDonationTests.ConcurrentInitialDonationBindingHasOneReservationAndOneDerivedRecord|FullyQualifiedName~VNextPhase06ResourceDonationTests.StaleIdentityWideningAndAmbientReuseFailClosedBeforeCharging'
$filter += '|FullyQualifiedName~VNextPhase06ResourceDonationTests.RevokedDonationCannotBeginConsumptionAndBudgetRemainsReserved'
$filter += '|FullyQualifiedName~VNextPhase06ResourceDonationTests.GeneratedSentryConsumesExactInvocationDonationWithoutSecondCharge'
$filter += '|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests.DisposedAdmissionCannotWinSubmitOrMutateSubmitState'
$filter += '|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests.FailedFinalSentryClosesAdmissionBeforeLateSubmit|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests.LosingFinalSentryCannotCompensateConcurrentSubmitWinner'
$filter += '|FullyQualifiedName~VNextPhase16DurableResourceAdmissionTests.DeniedCompensationCannotJournalPreSubmitClosure'
$filter += '|FullyQualifiedName~VNextPhase16DurableResourceAdmissionTests.PrepareFailureCleanupJournalsOnlyOwnerConfirmedCancellation'
$filter += '|FullyQualifiedName~VNextPhase16DurableResourceAdmissionTests.CancellationAppendFailureRetriesOnlyExactVerifiedClosure|FullyQualifiedName~VNextPhase16DurableResourceAdmissionTests.PreparedAppendFailureHasNoSubmitAndUsesVerifiedCleanupHistory|FullyQualifiedName~VNextPhase16DurableResourceAdmissionTests.CancellationTerminalReplayRejectsConflictingExactTuple'
$filter += '|FullyQualifiedName~VNextPhase16ResourceBudgetRecoveryJournalTests.LiveReplayRejectsAuthenticatedForkWithoutChangingObservedPrefix|FullyQualifiedName~VNextPhase16ResourceBudgetRecoveryJournalTests.LiveReplayAcceptsAuthenticatedExtensionOfObservedPrefix'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/FaultReconciliationContracts.cs',
    'contracts/SingPlus.Contracts/RasFailureContracts.cs',
    'contracts/SingPlus.Contracts/Regions.cs',
    'src/Platform/SingPlus.Platform.Abstractions/CxlProviderContracts.cs',
    'src/Platform/SingPlus.Platform.Host/CxlType3ModelProvider.cs',
    'src/Runtime/SingPlus.Runtime/Cxl/CxlAuthorityBridge.cs',
    'src/Runtime/SingPlus.Runtime/Cxl/CxlType3MemoryAuthority.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs',
    'src/Runtime/SingPlus.Runtime/Channels/RuntimeKernel.Channels.cs',
    'src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs',
    'src/Runtime/SingPlus.Runtime/Channels/ResponseRegistry.cs',
    'src/Runtime/SingPlus.Runtime/RuntimeKernel.Responses.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs',
    'src/Runtime/SingPlus.Runtime/RuntimeKernel.ProcessTeardown.cs',
    'src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs',
    'src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs',
    'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs',
    'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetRecoveryJournal.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs',
    'src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs',
    'src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs',
    'src/Sip/SingPlus.Sip/Regions/BorrowLease.cs',
    'src/Sip/SingPlus.Sip/Regions/OwnedRegion.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaSubmission.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaTrace.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Dma.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Device.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.ChildDomains.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.VirtualEffects.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.GuestMemory.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.VirtualIo.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BackendEpoch.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Irq.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Mmio.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6BoundedEvidenceReconciler.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tests/SingPlus.Tests/Contracts/FaultReconciliationPolicyV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/RasFailureContractsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6RasFailureConsequenceTests.cs',
    'tests/SingPlus.Tests/Ownership/RegionUseTests.cs',
    'tests/SingPlus.Tests/Contracts/ProtocolRuntimeTests.cs',
    'tests/SingPlus.Tests/Contracts/ResponsePublicationRuntimeTests.cs',
    'tests/SingPlus.Tests/Contracts/ResponseClientAdapterTeardownTests.cs',
    'tests/SingPlus.Tests/Runtime/ServiceDiscoverySessionTests.cs',
    'tests/SingPlus.Tests/Runtime/EndpointSessionCancellationTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformBorrowReadGrantTeardownTests.cs',
    'tests/SingPlus.Tests/SipJobs/Phase142ClosedValueDirectContourTests.cs',
    'tests/SingPlus.Tests/SipJobs/Phase143InlineRegionBorrowTests.cs',
    'tests/SingPlus.Tests/SipJobs/Phase143InlineRegionMoveTests.cs',
    'tests/SingPlus.Tests/NativeServices/NativeSystemServiceVerticalSliceTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase06ResourceDonationTests.cs',
    'tests/SingPlus.Tests/Runtime/V6BoundedEvidenceReconcilerTests.cs',
    'tests/SingPlus.Tests/Runtime/CxlType3MemoryProviderTests.cs',
    'tests/SingPlus.Tests/Runtime/Phase10ProviderConformanceTests.cs',
    'tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase16DurableResourceAdmissionTests.cs',
    'tests/SingPlus.Tests/Runtime/EffectPublicationSemanticsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs',
    'tests/SingPlus.Tests/Runtime/V6MemoryRuntimeEnforcementTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaSubmissionTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaGrantTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDeviceLeaseTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformAuthorityBridgeChildDomainTests.cs',
    'tests/SingPlus.Tests/Virtualization/Phase8ResidualVirtualizationTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformIrqBindingTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformMmioLeaseTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'eng/v6/Qualify-V6P10Ras.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P10 RAS tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The P10 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value
    $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value
    $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 309 -or $skipped -ne 0 -or $total -ne 309) {
        throw "Unexpected P10 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }

    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $sourceSetPayload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($sourceSetPayload))).ToLowerInvariant()

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('P10', 'P10-C-bounded-reconciliation', 'P10-D-cxl-type3-model-fault-adapter',
            'P10-D-post-callback-backing-revalidation')
        slice = 'failure-vocabulary-region-subrange-existing-provider-loss-and-cxl-type3-model-fault-adapter'
        contour = 'single-host/managed-fault-injection/staged-external-operation/cxl-type3-model'
        sliceStatus = 'provider-cancellation-and-reconfigure-reconciliation-fail-closed; physical-RAS-FutureGated'
        requirementClassification = [ordered]@{
            VerifiedExisting = @('RegionAuthority quarantines exact model subranges and retains damage generation high-watermarks',
                'P04 DMA owner fault-pins possible submit effects and ambiguous grant closure before mapping reclaim',
                'ExternalOperationAuthority rejects an old closure callback if provider loss changes the owner transition sequence',
                'HybridCPU provider retains deferred A-B-A generation drift across an in-flight ambiguous publication')
            Partial = @('internal managed fault-injection and staged external-operation consequence contours only',
                'closed queued sessions retain accepted unclosed work and block reclaim, but no exact post-close reconciliation path is implemented',
                'direct donation possible-submit/return interlock and one generated callback boundary are qualified; other generated callback interleavings remain separately bounded by external-operation and budget owners')
            Missing = @('live provider backing-replacement consumer and physical health/closure evidence source',
                'exact post-close service or provider settlement evidence and API for retained queued invocation work')
            Contradicted = @('provider loss or failed revoke proves DMA containment or safe Region reclaim')
            ExternalBlocked = @('physical ECC/poison/CXL fault source and containment qualification')
            FutureGated = @('granular physical RAS and production deployment contour')
        }
        requirementIds = @('P10-AMBIGUOUS-EFFECT-ACK-01', 'V6-013', 'V6-026', 'P10-C',
            'P10-REGION-DAMAGE-GENERATION-HIGHWATERMARK-01',
            'P10-DEFERRED-RECONFIGURE-ADMISSION-BARRIER-01',
            'P10-DMA-POSSIBLE-EFFECT-REGION-PIN-01',
            'P10-PUBLICATION-CLOSURE-PROVIDER-LOSS-RACE-01',
            'P10-DEFERRED-PROVIDER-GENERATION-ABA-01',
            'P10-REGION-USE-MUTATION-EXHAUSTION-01',
            'P10-MULTI-REGION-ATOMIC-RELEASE-01',
            'P10-FAILED-ADMISSION-OWNER-PIN-01',
            'P10-PUBLISHED-SETTLEMENT-RELEASE-INTERLOCK-01',
            'P10-PROVIDER-LOSS-INVALIDATED-USE-PIN-01',
            'P10-EXTERNAL-OPERATION-REGION-USE-RELEASE-OWNER-01',
            'P10-DOMAIN-RECLAIM-LATE-PIN-PREFLIGHT-01',
            'P10-ALLOCATION-TEARDOWN-LINEARIZATION-01',
            'P10-PUBLIC-TRANSFER-TARGET-TEARDOWN-LINEARIZATION-01',
            'P10-CHANNEL-RESPONSE-MOVE-TEARDOWN-LINEARIZATION-01',
            'P10-CHANNEL-CREATION-TEARDOWN-LINEARIZATION-01',
            'P10-RESPONSE-CLOSE-CANCEL-LINEARIZATION-01',
            'P10-SESSION-EXPIRATION-INLINE-PIN-01',
            'P10-SESSION-OPEN-TEARDOWN-LINEARIZATION-01',
            'P10-SERVICE-REGISTRATION-TEARDOWN-LINEARIZATION-01',
            'P10-SESSION-RECEIVE-CLOSE-LINEARIZATION-01',
            'P10-SESSION-ACCEPTED-EFFECT-RECLAIM-PIN-01',
            'P10-DONATION-CLOSURE-NO-DOWNGRADE-01',
            'P10-DONATION-SUBMIT-RETURN-LINEARIZATION-01',
            'P10-GENERATED-DONATION-CALLBACK-LOSS-01',
            'P10-GENERATED-DONATION-CLOSE-BEFORE-SUBMIT-01',
            'P10-ACCEPTED-CANCEL-NO-CONTAINMENT-01',
            'P10-FAILED-INLINE-NO-CONTAINMENT-01',
            'P10-FAILED-QUEUED-SETTLEMENT-POSSIBLE-EFFECT-01',
            'P10-EXIT-LOCAL-TOKEN-REVOCATION-01',
            'P10-EXIT-BORROW-GRANT-CLOSURE-01')
        testIds = @('HybridCpuExternalOperationProviderTests.CancellationReceiptDistinguishesPreSubmitClosureFromPendingPostSubmitEffect',
            'HybridCpuExternalOperationProviderTests.ReconfigureAfterSubmitRejectsStaleClosureAndKeepsRegionPinned',
            'HybridCpuExternalOperationProviderTests.ReconfigureBeforeSubmitClosesLocalAdmissionWithoutInventingEffect',
            'HybridCpuExternalOperationProviderTests.ReconfigureAfterCompletionOrVisibilityProjectsProviderLossWithoutPublication',
            'HybridCpuExternalOperationProviderTests.ReconfigureAfterReleasePreservesPublishedTerminalDecision',
            'HybridCpuExternalOperationProviderTests.ReconfiguredOperationRequiresCurrentGenerationAndExplicitClosureForReconciliation',
            'HybridCpuExternalOperationProviderTests.ReconfigureGenerationAbaNeverReauthorizesOldRequest',
            'HybridCpuExternalOperationProviderTests.PublicationCallbackReentryIsFailClosedAndGenerationChangeWaitsForBoundaryExit',
            'V6RasFailureConsequenceTests.ClearedDamageRejectsOldProviderGenerationEvenWithNewerObservationSequence',
            'V6RasFailureConsequenceTests.ReconfiguredDamageRejectsOldProviderGenerationWithNewerSequence',
            'PlatformDmaSubmissionTests.TrustedBackendResetDuringSubmitPinsPossibleEffectWithoutProviderIncarnationDrift',
            'PlatformDmaSubmissionTests.ProviderThrowDuringGrantRevokePinsMappingAfterPossibleClosure',
            'EffectPublicationSemanticsV1Tests.ProviderLossDuringAmbiguousPublicationClosureCannotClearPossibleEffect',
            'EffectPublicationSemanticsV1Tests.RegionDamageDuringPublicationCannotBecomePublishedEvidence',
            'ExternalOperationLifecycleTests.ProviderLossInvalidatesWritableUseAtTerminalMutationEpochWithoutReclaim',
            'ExternalOperationLifecycleTests.ProviderLossWithBrokenRegionUseBindingReportsFailureAndRetainsOwnerPin',
            'ExternalOperationLifecycleTests.LateCancellationCannotEraseProviderLossConsequence',
            'HybridCpuExternalOperationProviderTests.ReconfigureGenerationAbaInsideAmbiguousPublicationCannotReauthorizeOldRequest',
            'RegionUseTests.ExhaustedMutationEpochCannotReleaseOrInvalidateWritableUse',
            'RegionUseTests.DomainReclaimPreflightsLaterInvalidatedUseBeforeReleasingEarlierRegion',
            'RegionUseTests.ConcurrentAllocationAndTeardownLeaveNoOwnedRegion',
            'RegionUseTests.ConcurrentTransferAndTargetTeardownLeaveNoRegionOwnedByExitedTarget',
            'ProtocolRuntimeTests.ConsumingSendAndReceiverTeardownLeaveNoRegionOwnedByExitedReceiver',
            'ProtocolRuntimeTests.ConcurrentChannelCreationAndTargetTeardownLeaveNoOpenEndpoint',
            'ResponsePublicationRuntimeTests.OwnershipResponseAndRequesterTeardownLeaveNoRegionOwnedByExitedRequester',
            'ResponseClientAdapterTeardownTests.ConcurrentCancellationAndTeardownFinalizeOneResponseAndInvalidateEndpoint',
            'ServiceDiscoverySessionTests.ExpirationWithInlineEffectPinDefersChannelCloseUntilSettlement',
            'ServiceDiscoverySessionTests.ConcurrentOpenAndProviderTeardownLeaveNoActiveSessionOrOpenChannel',
            'ServiceDiscoverySessionTests.ConcurrentServiceRegistrationAndProviderTeardownLeaveNoDiscoverableDescriptor',
            'EndpointSessionCancellationTests.ConcurrentReceiveAndSessionCloseCannotDeliverAfterClose',
            'EndpointSessionCancellationTests.ClosedSessionRetainsAcceptedUnsettledInvocationAndBlocksProcessReclaim',
            'EndpointSessionCancellationTests.ClosedSessionCanReclaimWhenRequestWasNeverAccepted',
            'EndpointSessionCancellationTests.CallerCancellationBeforeServiceAcceptanceSettlesAsCancelledWithoutExecutionAcceptance',
            'EndpointSessionCancellationTests.AcceptedCancelledResponseDoesNotProveEffectClosureForProcessReclaim',
            'EndpointSessionCancellationTests.AcceptedCancellationAndSessionCloseRetainPossibleEffectInEitherOrder',
            'EndpointSessionCancellationTests.FailedAcceptedInlineSettlementDoesNotAssertEffectContainment',
            'EndpointSessionCancellationTests.FailedQueuedSettlementCallbackRetainsPossibleEffectWithoutServiceAcceptance',
            'PlatformBorrowReadGrantTeardownTests.BorrowerTeardownClosesGrantBeforeReturningCpuBorrow',
            'PlatformBorrowReadGrantTeardownTests.OwnerTeardownClosesGrantBeforeRevokingBorrowAndReclaimingRegion',
            'PlatformBorrowReadGrantTeardownTests.BorrowerTeardownRemainsDrainingUntilGrantClosureIsObserved',
            'Phase142ClosedValueDirectContourTests.ServiceFaultAfterInlineBeginPreventsSentryEntryAndCannotReplayBinding',
            'Phase143InlineRegionBorrowTests.ServiceTerminationBeforeFinalRevalidationInvalidatesBorrowAndUseWithoutRetry',
            'Phase143InlineRegionBorrowTests.StaleServiceDuringUseReleaseStillRunsReverseCleanupAndTerminatesLease',
            'Phase143InlineRegionMoveTests.ConsumerTerminationAfterSecondMoveUsesOwnerReclaimAndNeverMovesBackToCaller',
            'NativeSystemServiceVerticalSliceTests.ServiceFaultAfterSessionResolutionPreventsGeneratedSentryAdmission',
            'VNextPhase06ResourceDonationTests.PossibleSubmitCannotDowngradeDonationToPreSubmitReturn',
            'VNextPhase06ResourceDonationTests.PossibleSubmitAndPreSubmitReturnHaveOneDonationWinner',
            'VNextPhase06ResourceDonationTests.GeneratedSubmitBlocksLatePreSubmitReturnDuringSessionDrain',
            'VNextPhase06ResourceDonationTests.SessionCloseWithoutLivePinQuarantinesBoundDonationInsteadOfAssumingPreSubmitClosure',
            'VNextPhase06ResourceDonationTests.GeneratedSubmitAfterSessionCloseWithoutPinCannotRefundOrCallProvider',
            'ExternalOperationLifecycleTests.MultiRegionReleaseFailureLeavesEveryUsePinned',
            'ExternalOperationLifecycleTests.FailedAdmissionCleanupRetainsAcquiredUseUnderOperationOwner',
            'ExternalOperationLifecycleTests.FailedAdmissionWithSuccessfulCleanupLeavesNoRegionUse',
            'VNextPhase07ExternalOperationResourceBindingTests.PublishedOperationCannotReleaseRegionWhileResourceSettlementIsInFlight',
            'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossWithQuarantinedResourceBindingCannotReleaseRegion',
            'VNextPhase07ExternalOperationResourceBindingTests.FailedRegionInvalidationStillQuarantinesResourceBoundProviderLoss',
            'VNextPhase07ExternalOperationResourceBindingTests.FailedBudgetQuarantineReportsUncontainedProviderLossAndKeepsResourcePinned',
            'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossDuringExactSettlementRetainsLossAndCompletesBudgetReceipt',
            'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossAfterBudgetSettlementAcceptsExactChargeBeforeBindingCompletion',
            'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossBindingReadRacesExactSettlementWithoutFalseQuarantineFailure',
            'VNextPhase07ExternalOperationResourceBindingTests.PriorDifferentBudgetTerminalChargeCannotCompleteExactReceipt',
            'VNextPhase16DurableResourceAdmissionTests.JournalFailureAfterBudgetSettlementAndProviderLossKeepsColdChargeConservative',
            'VNextPhase16DurableResourceAdmissionTests.RetryAfterDurableTerminalRecordDoesNotAppendSecondReceipt',
            'VNextPhase16DurableResourceAdmissionTests.LiveJournalRollbackCannotLowerObservedRecoveryCharge')
        changedFiles = @('src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
            'src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs',
            'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs',
            'src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs',
            'src/Runtime/SingPlus.Runtime/Channels/RuntimeKernel.Channels.cs',
            'src/Runtime/SingPlus.Runtime/RuntimeKernel.Responses.cs',
            'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs',
    'src/Runtime/SingPlus.Runtime/RuntimeKernel.ProcessTeardown.cs',
            'src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs',
            'src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs',
            'src/Sip/SingPlus.Sip/Regions/BorrowLease.cs',
            'src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs',
            'src/Sip/SingPlus.Sip/Regions/OwnedRegion.cs',
            'src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs',
            'src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs',
            'tests/SingPlus.Tests/Ownership/RegionUseTests.cs',
            'tests/SingPlus.Tests/Contracts/ProtocolRuntimeTests.cs',
            'tests/SingPlus.Tests/Contracts/ResponsePublicationRuntimeTests.cs',
            'tests/SingPlus.Tests/Contracts/ResponseClientAdapterTeardownTests.cs',
            'tests/SingPlus.Tests/Runtime/ServiceDiscoverySessionTests.cs',
            'tests/SingPlus.Tests/Runtime/EndpointSessionCancellationTests.cs',
            'tests/SingPlus.Tests/Platform/PlatformBorrowReadGrantTeardownTests.cs',
            'tests/SingPlus.Tests/SipJobs/Phase142ClosedValueDirectContourTests.cs',
            'tests/SingPlus.Tests/SipJobs/Phase143InlineRegionBorrowTests.cs',
            'tests/SingPlus.Tests/SipJobs/Phase143InlineRegionMoveTests.cs',
            'tests/SingPlus.Tests/NativeServices/NativeSystemServiceVerticalSliceTests.cs',
            'tests/SingPlus.Tests/Runtime/VNextPhase06ResourceDonationTests.cs',
            'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs',
            'eng/v6/Qualify-V6P10Ras.ps1')
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim()
            sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim()
            targetFramework = 'net11.0'
            hybridCpuAdapter = 'in-tree executable adapter exercised by reusable provider fault matrix'
        }
        commandsRun = @("dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter $filter")
        environment = [ordered]@{
            os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            javaChecks = 'SKIPPED_BY_INSTRUCTION'
        }
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        featureGate = [ordered]@{ roadmapGate = 'V6-RAS-PARTIAL-FAILURE'; implementationGate = 'V6-RAS-PARTIAL-FAILURE'; state = 'OFF' }
        ownership = [ordered]@{
            provider = 'health evidence and exact provider/failure-domain generations'
            regionAuthority = 'subrange usability consequence and damage generation'
            externalOperationAuthority = 'in-flight ambiguity, quarantine, closure, and release interlock'
            capabilityAuthority = 'unchanged; health evidence grants no capability or permission'
        }
        coverage = [ordered]@{
            contract = @('bounded provider/failure-domain scope', 'non-authoritative health evidence',
                'typed failure consequence projection', 'healthy evidence cannot clear quarantine')
            region = @('overlapping uses invalidated', 'disjoint uses remain valid',
                'overlapping new use denied', 'damaged Region release/reclaim pinned',
                'new backing reservation denied while subrange damage is active and readmitted after exact replacement',
                'CXL Type-3 re-placement denied after damaged old backing closes',
                'internal owner-authorized backing replacement contract after tracked closure; test caller only',
                'per-failure-domain observation and generation high-watermark survives damage removal and reconfiguration')
            regionMutationBoundary = @('failed writable-use release or invalidation at mutation epoch exhaustion retains the active use pin')
            providerLossTerminalEpoch = @('provider loss invalidates the exact operation-owned writable use at terminal mutation epoch without wrapping the epoch or releasing the owner pin')
            providerLossInvalidationFailure = @('broken operation/use binding returns ExternalEffectUncontained after recording provider loss; release remains pinned')
            providerLossCancellation = @('late cancellation preserves ProviderLost or Faulted consequence for staged and direct effects')
            multiRegionRelease = @('all Region uses are validated and writable mutation capacity is reserved under ordered Region owner locks before any use is released')
            domainReclaimPreflight = @('all existing Region records are locked in ID order; a later invalidated or operation-bound use, reservation, damage or exhausted mutation epoch blocks reclaim before an earlier Region changes')
            allocationTeardown = @('managed AllocateBuffer and AllocateRegion check process state, reserve budget, create and register the Region under the same kernel gate as process teardown; a concurrent allocation either precedes reclaim or is denied')
            publicTransferTeardown = @('managed TransferRegion for OwnedBuffer and OwnedRegion checks both process states and commits owner, payload, process registration and budget movement under the teardown gate; an exited target retains no owned Region')
            channelResponseTeardown = @('channel ownership send, response publication and inline session MOVE check live process state while holding the existing teardown gate before Region owner transfer; channel and response correlation remain under their existing gate')
            channelCreationTeardown = @('both channel endpoints and optional response registration commit under the existing process teardown gate after checking both process states; a completed target teardown leaves no open endpoint')
            responseCloseTeardown = @('process and session channel closure serialize response pending/completion removal with cancel, receive, publish and waiter registration; a concurrent cancel or teardown yields one cancelled waiter result and a stale endpoint')
            sessionExpirationPin = @('expiration moves an active pinned inline effect to Draining, rejects new session effects, and defers channel close until the exact pin settles; channel closure is not proof of external containment')
            sessionOpenTeardown = @('session record registration and channel creation share the process teardown gate; a completed provider teardown leaves no active session or open caller channel')
            serviceRegistrationTeardown = @('provider state validation and service descriptor registration share the process teardown gate; a completed provider teardown leaves no discoverable descriptor')
            sessionReceiveClose = @('session state resolve, channel dequeue and invocation delivery registration share the response correlation gate; process teardown also serializes receive under its gate, so no post-close session request is delivered')
            sessionPossibleEffectReclaim = @('channel close cancels the response waiter but retains accepted unclosed invocation or active donation in the invocation owner; accepted Cancelled queued response and failed accepted inline settlement remain possible effects and report TooLateEffectMayExist without explicit closure, including cancel/close race; a queued settlement callback failure after entry retains possible effect even without service acceptance, and a later successful response cannot erase that ambiguity; process reclaim stays draining while possible effect lacks exact settlement; unaccepted cancelled requests without a failed callback remain reclaimable')
            processExitLocalAccess = @('process Exiting denies new local owner-token and borrower CPU-token access without releasing Region backing or the provider-facing borrow lifetime; previously issued managed spans remain readable; platform read grant remains reserved until exact closure, then teardown may return the loan and reclaim')
            donationClosure = @('possible-submit donation cannot transition to pre-submit Returned; owner rejects terminal consequence changes before Budget mutation; session close retains Quarantined donation as a reclaim pin; submit and pre-submit return serialize under the existing response correlation gate')
            generatedDonationCallback = @('generated callback after external owner submit cannot refund a consuming lease; session loss without a live pin quarantines Bound donation and Budget reservation instead of inferring cross-owner pre-submit closure; prepared submit after close cannot reach its provider callback and the external owner retains conservative possible-effect state')
            admissionCleanup = @('failed admission cleans acquired uses as one Region transition or retains every uncleared use in the cancelled operation record; submit remains denied')
            operationUseReleaseOwner = @('operation-bound Region uses are pinned at atomic acquisition; public release, a different operation generation and direct domain reclaim cannot free an active or quarantined use')
            externalOperation = @('provider loss after submit quarantines', 'pre-submit cancellation receipt follows owner release',
                'Region damage during a staged publication callback invalidates the exact use and retains ambiguous publication instead of recording Published',
                'post-submit cancellation receipt stays unconfirmed while effect remains possible',
                'reconfigure automatically cancels and releases pre-submit admission',
                'reconfigure automatically projects post-submit provider loss without release',
                'deferred A-B-A reconfiguration during ambiguous publication still projects provider loss and keeps Region pinned',
                'reset after completion or visibility retains quarantine and denies stale publication',
                'reset after release preserves published terminal decision',
                'reconfigured request stays stale across generation ABA', 'explicit current-generation reconciliation records provider loss',
                'reconciliation without resource closure retains owner state',
                'untrusted containment cannot release', 'exact containment permits eventual release',
                'provider loss during an ambiguous publication closure callback invalidates the callback result',
                'published operation cannot release Region uses before exact resource settlement, during settlement, or after failed settlement',
                'provider loss invalidates use for new access but cannot reclaim Region while the resource binding and budget remain quarantined')
            dmaConsequence = @('trusted local backend reset during possible DMA submit pins the grant and prevents mapping reclaim',
                'ambiguous provider grant closure after completion and visibility pins the grant, blocks repeat provider revoke, and prevents mapping reclaim')
            reconciliation = @('bounded evidence-query attempts for local fallible omission/reordering/recovery',
                'provider callbacks execute without authority locks', 'first exact scope/generation/high-watermark evidence stops retry',
                'wrong tuple or malformed evidence rejects immediately', 'provider exceptions and attempt exhaustion preserve quarantine',
                'non-retry and unsupported policies invoke no provider callback', 'receipt never proves closure or authorizes release/reclaim/execution')
            aba = @('provider generation including old-generation/new-sequence replay', 'failure-domain generation', 'evidence sequence',
                'damage handle generation', 'domain-wide monotonic observation high-watermark',
                'exact replacement tuple correlation')
            adapter = @('generic managed model and HybridCPU executable adapter share the deterministic fault matrix')
            cxlType3 = @('exact endpoint/fabric/memory binding tuple revalidation',
                'deterministic named degradation injection', 'exact Region subrange quarantine',
                'device-rebind stale-evidence rejection before and during health callback',
                'malformed provider tuple rejection',
                'evidence grants no Region mutation or reclaim authority')
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Sip.dll',
            'tools/HybridCpu_ExecutableAdapter/packages.lock.json'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary/package evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        maximumSupportedClaim = [ordered]@{
            failureVocabulary = 'StaticAdmission'
            granularRegionFailure = 'ModelOnly on the managed deterministic fault-injection contour'
            existingManagedProviderLossClosure = 'RuntimeEnforced for staged external operations including transition-sequence revalidation after a closure callback'
            publishedSettlementReleaseInterlock = 'RuntimeEnforced for exact managed resource-bound published operations; in-flight or failed settlement cannot release Region uses'
            providerLossInvalidatedUsePin = 'RuntimeEnforced for exact managed resource-bound staged operations; invalidated use pins overlapping acquire, loan, backing lease, platform mapping, protected label attach, ownership transfer and reclaim until owner release'
            managedSessionLifecycle = 'RuntimeEnforced for single-host managed service registration, session open, request delivery, inline pin expiration/draining, channel closure, accepted-invocation reclaim pin, failed queued settlement callback possible-effect pin, and denial of new local token access during process Exiting; previously issued managed spans remain readable; accepted Cancelled queued response and failed accepted inline settlement retain possible-effect pins until exact closure; no external effect containment follows from cancel, channel close or settlement failure'
            invocationConsequenceObservation = 'StaticAdmission for quiescent exact-generation snapshot of the invocation owner; a closed session or channel cannot erase its possible-effect pin, and the snapshot authorizes neither closure nor reclaim'
            resourceDonationClosure = 'RuntimeEnforced for direct managed donation close/possible-submit interlock and tested generated callback boundaries after external owner submit and after session close before submit; untested callback interleavings and physical closure are outside this claim'
            boundedEvidenceReconciliation = 'RuntimeEnforced query-attempt bound on the internal managed coordinator; exact evidence remains non-authoritative and exhaustion preserves quarantine'
            hybridCpuProviderFaultBoundary = 'ExecutableAdapter'
            physicalEccPoison = 'FutureGated'
            cxlDegradation = 'ExecutableAdapter on the deterministic managed Type-3 model only'
            hardware = 'FutureGated'
            production = 'FutureGated'
        }
        remainingBlockers = @(
            'A failed queued settlement callback may have crossed an irreversible boundary; no exact post-close source or API closes that retained possible effect, including after a later successful response.',
            'No exact post-close settlement source or API releases an accepted failed-inline possible-effect pin; local failure alone cannot prove containment.',
            'Already issued managed spans cannot be revoked by local token invalidation; memory-access containment and physical isolation are outside this claim.',
            'The granular Region consequence entry points are internal and V6-RAS-PARTIAL-FAILURE remains OFF.',
            'No physical ECC, poison, CXL degradation, reset, or hardware fault source is integrated.',
            'The named CXL Type-3 source is deterministic model injection only; it is not hardware telemetry.',
            'RecordAuthoritativeBackingReplacement has only test callers; no executable provider replacement consumer or backing-switch proof is integrated.',
            'No hardware qualification or production campaign exists.',
            'Provider resource closure still requires an explicit assertion; no provider-signed or hardware closure evidence source is integrated.',
            'A closed queued session with accepted but unsettled service work has no exact post-close settlement/reconciliation API; the retained invocation blocks process reclaim rather than inventing effect closure.',
            'Generated donated SIP provider callback composition uses the external-operation and budget owners; this direct donation transition test does not qualify every callback interleaving.',
            'No external formal or finite-model run is tuple-bound to this artifact.'
        )
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        rollbackFallback = 'Keep the v6 gate OFF; retain existing coarse external-operation quarantine and never infer closure from provider loss.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact.requirementIds += 'P10-INVOCATION-AMBIGUOUS-READMISSION-01'
    $artifact.requirementClassification.VerifiedExisting += 'Invocation owner rejects new service acceptance during settlement or after ambiguous callback failure without mutating acceptance; late terminal response retains uncertainty'
    $artifact.requirementIds += 'P10-DONATION-AMBIGUITY-ADMISSION-01'
    $artifact.testIds += 'EndpointSessionCancellationTests.AmbiguousSettlementBlocksDonationBindingAndActivation'
    $artifact.requirementClassification.VerifiedExisting += 'Donation binding/activation deny settlement in flight and retained failed effect; admission resolution separated from terminal cleanup'
    $artifact.requirementIds += 'P10-DONATION-RETURN-OBSERVED-AMBIGUITY-01'
    $artifact.requirementClassification.VerifiedExisting += 'Already observed settlement ambiguity denies pre-submit donation return before budget cancellation and at owner transition'
    $artifact.requirementClassification.VerifiedExisting += 'Donation return reservation excludes new settlement and activation across budget callback outside owner lock; denial retries and callback loss/session loss preserve conservative consequence'
    $artifact.requirementIds += 'P10-DONATION-RETURN-SETTLEMENT-INTERLOCK-01'
    $artifact.testIds += 'EndpointSessionCancellationTests.DonationReturnReservationExcludesSettlementAndPreservesFaultConsequence'
    $artifact.requirementIds += 'P03-DONATION-SPLIT-PREVALIDATION-01'
    $artifact.requirementClassification.VerifiedExisting += 'Already bound child invocation denied before capability derivation/budget split; duplicate denial preserves parent capacity and total held budget'
    $artifact.requirementClassification.Partial += 'Nested donation parent/child may change after prevalidation; exact owner reservation across derivation and split remains required'
    $artifact.requirementIds += 'P03-NESTED-DONATION-CORRELATION-SERIALIZATION-01'
    $artifact.testIds += 'VNextPhase06ResourceDonationTests.ConcurrentDuplicateNestedDonationHasOneSplitAndPreservesHeldCapacity'
    $artifact.requirementClassification.VerifiedExisting += 'Nested split uses existing correlation lock shared with return/submit; 16 concurrent duplicate trials have one split winner and preserve capacity'
    $artifact.requirementIds += 'P10-SETTLEMENT-CORRELATION-ADMISSION-01'
    $artifact.testIds += 'EndpointSessionCancellationTests.SettlementAdmissionWaitsForCorrelationGateAndCallbackReleasesIt'
    $artifact.requirementClassification.VerifiedExisting += 'Runtime settlement reserves under existing correlation gate before invocation lock; callback executes outside both locks; nested split and settlement cannot overlap reservation'
    $artifact.requirementClassification.VerifiedExisting += 'Initial donation binding validates exact invocation before derivation/reservation under shared correlation gate; stale and duplicate admission do not create capability records'
    $artifact.requirementIds += 'P03-INITIAL-DONATION-ADMISSION-01'
    $artifact.testIds += 'VNextPhase06ResourceDonationTests.ConcurrentInitialDonationBindingHasOneReservationAndOneDerivedRecord'
    $artifact.requirementIds += 'P03-DONATION-GRANT-REVALIDATION-01'
    $artifact.testIds += 'VNextPhase06ResourceDonationTests.RevokedDonationCannotBeginConsumptionAndBudgetRemainsReserved'
    $artifact.requirementClassification.VerifiedExisting += 'Internal managed possible-submit helper denies already revoked derived grant before budget mutation; helper has tests-only callers, no production provider coverage claim'
    $artifact.requirementIds += 'P03-DONATED-SUBMIT-FINAL-SENTRY-01'
    $artifact.testIds += 'VNextPhase06ResourceDonationTests.GeneratedSentryConsumesExactInvocationDonationWithoutSecondCharge'
    $artifact.requirementClassification.VerifiedExisting += 'Generated donated submit invokes existing authority final sentry; revoke after prepare prevents provider callback and permits only exact pre-submit compensation'
    $artifact.requirementIds += 'P03-DONATED-PROVIDER-BOUNDARY-REVALIDATION-01'
    $artifact.requirementClassification.VerifiedExisting += 'Generated donated provider boundary rejects observed revoke after submit marker before actual callback; consuming budget/external owner quarantine retained without refund'
    $artifact.requirementClassification.Partial += 'Cross-owner revoke after final live permission check remains separate atomic admission gap; observed hook test does not prove all concurrent interleavings'
    $artifact.requirementIds += 'P10-ADMISSION-DISPOSE-SUBMIT-WINNER-01'
    $artifact.testIds += 'VNextPhase07ExternalOperationResourceBindingTests.DisposedAdmissionCannotWinSubmitOrMutateSubmitState'
    $artifact.requirementClassification.VerifiedExisting += 'Disposed resource admission cannot win submit; disposal and submit share existing atomic state, preventing late compensation after submit winner'
    $artifact.requirementIds += 'P10-FINAL-SENTRY-COMPENSATION-WINNER-01'
    $artifact.testIds += @('VNextPhase07ExternalOperationResourceBindingTests.FailedFinalSentryClosesAdmissionBeforeLateSubmit','VNextPhase07ExternalOperationResourceBindingTests.LosingFinalSentryCannotCompensateConcurrentSubmitWinner')
    $artifact.requirementClassification.VerifiedExisting += 'Losing final sentry must win shared unused-admission CAS before compensation; actual submit winner preserves Consuming/Submitted through losing sentry and dispose'
    $artifact.requirementIds += 'P10-COMPENSATION-OWNER-CONFIRMATION-01'
    $artifact.testIds += 'VNextPhase16DurableResourceAdmissionTests.DeniedCompensationCannotJournalPreSubmitClosure'
    $artifact.requirementClassification.VerifiedExisting += 'Owner-denied compensation retains quarantine and durable conservative charge; physical closure remains FutureGated'
    $artifact.requirementIds += 'P10-PREPARE-CLEANUP-OWNER-CONFIRMATION-01'
    $artifact.testIds += 'VNextPhase16DurableResourceAdmissionTests.PrepareFailureCleanupJournalsOnlyOwnerConfirmedCancellation'
    $artifact.requirementClassification.VerifiedExisting += 'Prepare exception cleanup after durable Prepared uses owner-confirmed cancellation; denied budget transition preserves quarantine across cold recovery'
    $artifact.requirementIds += 'P10-PRE-SUBMIT-JOURNAL-ACK-01'
    $artifact.testIds += @('VNextPhase16DurableResourceAdmissionTests.CancellationAppendFailureRetriesOnlyExactVerifiedClosure','VNextPhase16DurableResourceAdmissionTests.PreparedAppendFailureHasNoSubmitAndUsesVerifiedCleanupHistory','VNextPhase16DurableResourceAdmissionTests.CancellationTerminalReplayRejectsConflictingExactTuple')
    $artifact.requirementClassification.VerifiedExisting += 'Prepared/cancellation append failure before write and after full frame preserves exact recovery history; cancelled terminal replay requires matching owner generation, correlation generation, amounts and empty charge'
    $artifact.requirementIds += 'P10-JOURNAL-PREFIX-CONTINUITY-01'
    $artifact.requirementIds += 'P10-ACTIVE-OPERATION-ADMISSION-01'
    $artifact.requirementClassification.VerifiedExisting += 'Generated donated final admission rejects Cancelled/CancellationPending operation disposition before callback; pre-marker cancellation refunds only owner-confirmed no-effect path, post-marker quarantine retains charge'
    $artifact.testIds += @('VNextPhase16ResourceBudgetRecoveryJournalTests.LiveReplayRejectsAuthenticatedForkWithoutChangingObservedPrefix','VNextPhase16ResourceBudgetRecoveryJournalTests.LiveReplayAcceptsAuthenticatedExtensionOfObservedPrefix')
    $artifact.requirementClassification.VerifiedExisting += 'Live journal replay/append reject equal-length and longer authenticated forks at previously observed prefix before cursor mutation; valid extensions and lost-ack replay remain supported'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P10 RAS/failure-domain evidence

- Result: $passed/$total selected contract, Region consequence, provider-loss, and adapter-matrix tests passed.
- Claims: failure vocabulary = StaticAdmission; granular Region failure = ModelOnly on the managed fault model; existing staged provider-loss closure = RuntimeEnforced; HybridCPU and deterministic CXL Type-3 model boundaries = ExecutableAdapter.
- Gate: `V6-RAS-PARTIAL-FAILURE` remains OFF.
- Ownership: providers emit evidence, RegionAuthority owns subrange usability, and ExternalOperationAuthority owns in-flight ambiguity and closure.
- Safety: operation-bound Region uses can be released only by the exact external operation transition, not a public use-release call, another operation generation or direct domain reclaim while active. Domain reclaim preflights existing Regions under ordered owner locks so a later pin cannot partially reclaim an earlier Region. Managed allocation, public transfer, channel ownership SEND, response publication and inline session MOVE use the teardown gate before owner mutation. Service descriptor registration, session registration and channel creation share that gate, so provider teardown leaves no discoverable descriptor, active session or open caller channel. Process and session channel closure use the response correlation gate so close cannot race with cancellation, receive, publish or waiter registration. Session request dequeue and invocation registration share the correlation gate; receive also shares the teardown gate. Session expiration with an active inline effect pin enters Draining and defers channel close until settlement. A closed queued session retains accepted unsettled work or a quarantined donation and blocks process reclaim; accepted Cancelled response remains possible effect. Process Exiting invalidates local owner and CPU borrow tokens while retained backing and provider-facing borrow lifetime await exact closure. Its cancelled response waiter is not containment evidence. Possible submit cannot be downgraded to pre-submit donation return. Generated callback tests reject a late refund after external owner submit. On session loss, a Bound donation is quarantined because its local state cannot prove cross-owner pre-submit closure. An exact post-close reconciliation API remains missing. The current payload invalidation implementations are internal OwnedBuffer/OwnedRegion paths; no general callback-failure atomicity claim is made. Invalidated unreleased uses pin overlapping Region acquire, whole-Region loan, new backing lease, platform mapping, ownership transfer and reclaim after provider loss; the V1 active read-only transfer and active-use process teardown paths remain permitted. Damaged Regions remain pinned; the internal replacement contract checks tracked-use closure and replay high-watermarks, but its only callers are tests and no provider backing-switch proof is integrated.
- Queued settlement failure: once the response callback begins, a failed result or exception retains a possible-effect pin even without service acceptance. A later successful response does not close the earlier ambiguity; process reclaim waits for exact closure.
- Inline failure: an accepted inline invocation settled with `succeeded=false` retains its possible-effect pin. This local result does not prove external containment, including after a committed MOVE; process reclaim waits for exact closure.
- Span ceiling: Exiting denies later access mediated by local owner and borrow tokens; a span handed out before Exiting remains readable, so the managed contour cannot claim memory-access containment.
- DMA consequence: trusted backend reset during a possible submit and ambiguous grant closure after visibility retain the exact grant and Region mapping reservation; a lost revoke receipt cannot trigger a second provider revoke.
- Bounded reconciliation: the internal managed coordinator executes at most the policy limit, accepts only exact failure-domain generations and observation high-watermarks, invokes no provider for non-retry policies, and converts exhaustion or callback failure to continued quarantine rather than closure.
- CXL Type-3: exact live endpoint/fabric/memory generations are revalidated before and after the deterministic health callback, immediately before Region consequence; rebind during the callback leaves Region undamaged and marks placement for migration. Stale or malformed tuples do not reach RegionAuthority.
- Reconfiguration: old requests remain stale across generation ABA; pre-submit admissions close locally and post-submit operations automatically project provider loss. Explicit current-generation reconciliation with a separate resource-closure assertion is required for release. A provider-signed closure source remains open.
- Publication reconciliation: a provider-loss owner transition during the closure callback invalidates that callback result and retains the possible external effect.
- Publication/damage race: staged publication rechecks exact Region uses after its callback; concurrent subrange damage leaves a possible publication effect pinned and never records Published from the stale use.
- Settlement/release interlock: a published resource-bound operation retains Region uses before settlement, while settlement is in flight, and after a failed settlement transition.
- Provider-loss pin: an invalidated RegionUse rejects new access but remains a reclaim pin while the bound budget is quarantined; the owner trace stays at Quarantined without a synthetic release.
- Deferred provider reconfiguration: A-B-A drift during an in-flight ambiguous publication is retained; the old request becomes stale and provider loss keeps the Region pinned.
- Region damage replay: provider and failure-domain generation high-watermarks remain in RegionAuthority after reconfiguration or replacement; an old generation with a larger observation sequence cannot create fresh damage.
- Region use boundary: mutation epoch exhaustion rejects writable-use release or invalidation before changing the active use pin.
- Provider-loss exception: the exact operation-owned writable use enters terminal local Invalidated state at an exhausted epoch; the epoch does not wrap, and release/reclaim remain pinned. Ordinary public invalidation still rejects epoch exhaustion.
- Invalidation failure: a broken exact operation/use binding reports `ExternalEffectUncontained` while retaining the ProviderLost transition and denying release; this is fault-injection evidence, not a physical containment claim.
- Late cancellation preserves the staged `ProviderLost` or direct-write `Faulted` consequence and cannot rewrite either as `CancellationPending`.
- Multi-use release: exact Region uses release as one owner transition; exhaustion on a later writable Region retains every earlier use pin.
- Admission cleanup: failed acquisition either releases its earlier Region use or records it on a cancelled, unsubmitted operation when local cleanup fails.
- Not claimed: executable provider backing replacement, physical ECC/poison/CXL telemetry, hardware, or production qualification.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally {
    Pop-Location
}
