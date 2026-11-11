[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p08-preemption')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$filter = 'FullyQualifiedName~PreemptionContractsV1Tests|FullyQualifiedName~V6RestartAdmissionTests|FullyQualifiedName~V6StatefulResumeAccountingTests|FullyQualifiedName~V6ManagedStatefulResumeProviderTests|FullyQualifiedName~V6ManagedSafePointProviderTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~SemanticAdmissionSentryTests|FullyQualifiedName~CancellationContainmentSemanticsV1Tests|FullyQualifiedName~Phase04DeadlineCancellationTests|FullyQualifiedName=SingPlus.Tests.Runtime.HybridCpuExternalOperationProviderTests.CancellationReceiptDistinguishesPreSubmitClosureFromPendingPostSubmitEffect|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$filter += '|FullyQualifiedName~Phase8ResidualVirtualizationTests.ExecutableArtifactBindOrStartReceiptLossPinsChildAndMapping|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.RevokedParentMappingCannotReachExecutableChildStartProvider'
$filter += '|FullyQualifiedName~Phase8ResidualVirtualizationTests.GuestUnmapCannotOvertakeExecutableBindOrStartCallback'
$filter += '|FullyQualifiedName~Phase8ResidualVirtualizationTests.BoundArtifactPinsOnlyItsExactGuestMappingAfterCallbackSettles'
$filter += '|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.TypedSipV3ArtifactContourPinsMappingAfterRetiredWorkUntilReleaseEvidence'
$filter += '|FullyQualifiedName~Phase8ResidualVirtualizationTests.ParentAuthorizationRevokedInsideExecutableCallbackPreventsPublication'
$filter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ChildTransitionCallbackFaultOrResetCannotPublishLateState|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ConcurrentChildTransitionHasOneProviderCallback'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.TransitionCallbackRejectsGuestMapEventAndTrapBeforeProvider|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ChildEffectCallbackRejectsOverlappingTransition|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.VirtualEffectCallbackFaultOrResetCannotPublishLateEvidence'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ReentrantChildCloseCannotInvokeProviderTwice|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.NestedCreateCallbackBlocksImmediateParentTransition|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.ImmediateParentTransitionCallbackRejectsNestedCreateBeforeProvider'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ChildCreateCallbackPinsParentBeforeChildBindingPublication|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ParentRevokeCallbackRejectsChildCreateBeforeProvider'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetInsideChildCreateCallbackPinsNewParentGeneration|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.ResetInsideNestedCreateCallbackCannotPublishLateChild'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetInsideRootBindCallbackPinsSubjectWithoutInventingLease|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.LostRootBindReceiptPinsProcessReclaimWithoutProviderLease|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.RootBindCallbackRejectsDuplicateAdmissionBeforeProvider|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ProcessExitInsideRootBindCallbackRevokesPublishedBindingBeforeReclaim|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ParentRevokeCallbackRejectsNewMappingBeforeProvider|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.MappingCallbackCannotRevokeParentBeforePublication|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.MappingReceiptLossPinsParentAndLocalReservation|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.BackendResetInsideMappingCallbackFaultPinsLateLease|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ProcessExitInsideMappingCallbackTracksLateMappingForExactTeardown|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.LostMappingReceiptBlocksProcessReclaim|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetFaultedMappingKeepsBudgetAndRegionPinnedAcrossTeardownRetries|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ResetBeforeNotAcceptedMappingReplyDoesNotReleaseRegion|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.StableNotAcceptedMappingReplyReleasesLocalReservation'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.DeviceBindCallbackBlocksParentRevokeBeforeLeasePublication|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.ResetInsideDeviceBindCallbackPinsNewParentGeneration|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDeviceLeaseTests.ParentRevokeCallbackRejectsDeviceBindBeforeProvider'
$filter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.VirtualIoBindCallbackRejectsOverlappingChildTransition|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.ChildTransitionCallbackRejectsVirtualIoBindBeforeProvider|FullyQualifiedName=SingPlus.Tests.Virtualization.Phase8ResidualVirtualizationTests.ReentrantExecutableStartCannotEnterProviderTwice'
$adapterFilter = 'FullyQualifiedName~AdapterBoundaryTests|FullyQualifiedName~SemanticTraceInstrumentationTests'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/PreemptionContracts.cs',
    'contracts/SingPlus.Contracts/ExternalOperations.cs',
    'contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs',
    'contracts/SingPlus.Contracts/ResourceBudgetContracts.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6RestartAdmission.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6StatefulResumeAccounting.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ManagedStatefulResumeProvider.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ManagedSafePointProvider.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.ChildDomains.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BackendEpoch.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs',
    'src/Runtime/SingPlus.Runtime/RuntimeKernel.ProcessTeardown.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Device.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.GuestMemory.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.VirtualEffects.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.VirtualIo.cs',
    'src/Platform/SingPlus.Platform.HybridCpu/HybridCpuPlatformAuthorityProvider.ChildDomains.cs',
    'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs',
    'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs',
    'src/Runtime/SingPlus.Runtime/Deadlines/RuntimeKernel.DeadlineCancellation.cs',
    'tests/SingPlus.Tests/Contracts/PreemptionContractsV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/CancellationContainmentSemanticsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6RestartAdmissionTests.cs',
    'tests/SingPlus.Tests/Runtime/V6StatefulResumeAccountingTests.cs',
    'tests/SingPlus.Tests/Runtime/V6ManagedStatefulResumeProviderTests.cs',
    'tests/SingPlus.Tests/Runtime/V6ManagedSafePointProviderTests.cs',
    'tests/SingPlus.Tests/Virtualization/Phase8ResidualVirtualizationTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformAuthorityBridgeChildDomainTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDeviceLeaseTests.cs',
    'tests/SingPlus.Platform.HybridCpu.Tests/Phase504ExecutableAdapterTests.cs',
    'tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs',
    'tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs',
    'tests/SingPlus.Tests/Runtime/SemanticAdmissionSentryTests.cs',
    'tests/SingPlus.Tests/Runtime/Phase04DeadlineCancellationTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/HybridCpu_ExecutableAdapter/Adapter/HybridCpuExecutableChildAdapter.cs',
    'tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj',
    'tools/HybridCpu_ExecutableAdapter/packages.lock.json',
    'tools/HybridCpu_ExecutableAdapter.Tests/AdapterBoundaryTests.cs',
    'tools/HybridCpu_ExecutableAdapter.Tests/SemanticTraceInstrumentationTests.cs',
    'tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P08StatefulResumePerformanceQualification.cs',
    'eng/v6/Qualify-V6P08Preemption.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P08 preemption tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The P08 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value
    $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value
    $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 243 -or $skipped -ne 0 -or $total -ne 243) {
        throw "Unexpected P08 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    $adapterOutput = & dotnet test 'tools\HybridCpu_ExecutableAdapter.Tests\HybridCpu_ExecutableAdapter.Tests.csproj' --no-restore --filter $adapterFilter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P08 adapter boundary tests failed.`n$adapterOutput" }
    $adapterMatch = [regex]::Match($adapterOutput, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $adapterMatch.Success) { throw 'The P08 adapter test runner summary could not be parsed.' }
    $adapterFailed = [int]$adapterMatch.Groups[1].Value
    $adapterPassed = [int]$adapterMatch.Groups[2].Value
    $adapterSkipped = [int]$adapterMatch.Groups[3].Value
    $adapterTotal = [int]$adapterMatch.Groups[4].Value
    if ($adapterFailed -ne 0 -or $adapterPassed -ne 83 -or $adapterSkipped -ne 0 -or $adapterTotal -ne 83) {
        throw "Unexpected P08 adapter counts: failed=$adapterFailed passed=$adapterPassed skipped=$adapterSkipped total=$adapterTotal"
    }
    $raceFilter = 'FullyQualifiedName~V6ManagedStatefulResumeProviderTests.ConcurrentResumeHasOneRestoreWinner'
    for ($iteration = 1; $iteration -le 10; $iteration++) {
        $raceOutput = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-build --no-restore --filter $raceFilter 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $raceOutput -notmatch 'Failed:\s+0, Passed:\s+1, Skipped:\s+0, Total:\s+1') {
            throw "P08 concurrent resume repeat $iteration failed.`n$raceOutput"
        }
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p08-stateful-resume-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P08 stateful-resume performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p08-stateful-resume-performance/1' -or
        $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
        throw 'The P08 stateful-resume performance artifact is malformed.'
    }

    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $sourceSetPayload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($sourceSetPayload))).ToLowerInvariant()

    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('P08-A', 'P08-B-restart-only', 'P08-B-managed-safe-point', 'P08-D-accounting',
            'P08-D-model-provider', 'P08-D-terminal-payload-release', 'P08-managed-performance')
        slice = 'preemption-vocabulary-managed-restart-safe-point-stateful-storage-and-model-provider'
        contour = 'single-host/managed/staged-provider-loss/restart-only/deterministic-safe-point/stateful-managed-model'
        sliceStatus = 'provider-pre-submit-owner-closure-and-post-submit-ambiguity-corrected; device-safe-point-production-FutureGated'
        requirementClassification = [ordered]@{
            VerifiedExisting = @('ExternalOperationAuthority keeps post-submit cancellation pending until exact terminal evidence',
                'pre-submit ConfirmedBeforeSubmit now follows owner cancellation and release',
                'all identified ResumeBindingV1 producers emit lowercase SHA-256 digests',
                'unpublished capture recovery verifies exact storage owner and correlation before provider query')
            Partial = @('HybridCPU versioned provider now returns Ambiguous for post-submit owner cancellation request')
            Missing = @('terminal-after-submit acknowledgement source for this provider')
            Contradicted = @('owner CancellationPending can be reported as ConfirmedTerminalAfterSubmit',
                'uppercase hex is canonical ResumeBindingV1 evidence')
            ExternalBlocked = @('physical provider closure and safe-point qualification')
            FutureGated = @('physical or production preemption and stateful resume')
        }
        requirementIds = @('P08-CANCEL-TERMINAL-ACK-01', 'P08-SAFEPOINT-CLOCK-FAULT-QUARANTINE-01',
            'P08-CAPTURE-DISCARD-ESCROW-CLOSURE-01',
            'P08-CAPTURE-SETTLEMENT-RETRY-01',
            'P08-CAPTURE-ADMISSION-ROLLBACK-01',
            'P08-UNPUBLISHED-CAPTURE-RECOVERY-01',
            'P08-UNADMITTED-CAPTURE-RESET-PIN-01',
            'QV1-PROVIDER-FINAL-REVALIDATION-01',
            'P08-RESUME-CONCURRENT-WINNER-01',
            'P08-RESUME-FINAL-THREE-GATE-REVALIDATION-01',
            'P08-RESUME-OWNER-BEFORE-CALLBACK-01',
            'P08-CAPTURE-BUDGET-OWNER-BEFORE-PROVIDER-01',
            'P08-RECOVERY-OWNER-BEFORE-PROVIDER-QUERY-01',
            'P08-RECOVERY-EXACT-BINDING-LOOKUP-01',
            'P08-RESUME-CANONICAL-DIGEST-01')
        testIds = @('V6RestartAdmissionTests.LegalityRevokesEarlierAdmissionBeforeReplacementSubmit[providerRevoked=False]',
            'V6RestartAdmissionTests.LegalityRevokesEarlierAdmissionBeforeReplacementSubmit[providerRevoked=True]',
            'V6RestartAdmissionTests.PostSubmitGenerationDriftOrObservationFailureCannotIssueRestartReceipt[provider]',
            'V6RestartAdmissionTests.PostSubmitGenerationDriftOrObservationFailureCannotIssueRestartReceipt[runtime]',
            'V6RestartAdmissionTests.PostSubmitGenerationDriftOrObservationFailureCannotIssueRestartReceipt[observation-throws]',
            'HybridCpuExternalOperationProviderTests.CancellationReceiptDistinguishesPreSubmitClosureFromPendingPostSubmitEffect',
            'V6ManagedSafePointProviderTests.ClockFailureBeforeHookQuarantinesAttemptWithoutInvokingProvider',
            'V6ManagedSafePointProviderTests.ClockFailureAfterHookQuarantinesPossibleEffectWithoutReceipt',
            'V6ManagedSafePointProviderTests.TerminalGenerationResetQuarantinesInFlightSafePoint',
            'V6ManagedSafePointProviderTests.ExhaustedRequestGenerationDeniesBeforeProviderHook',
            'V6ManagedStatefulResumeProviderTests.TerminalGenerationResetQuarantinesCaptureAndDeniesRestore',
            'V6ManagedStatefulResumeProviderTests.ExhaustedCaptureGenerationDeniesBeforeRetainingPayload',
            'V6ManagedStatefulResumeProviderTests.TerminalResetCannotUseUnchangedGenerationToReleaseStorageEscrow',
            'V6ManagedStatefulResumeProviderTests.ProviderDiscardFailureQuarantinesStorageUntilExactReconciliation',
            'V6ManagedStatefulResumeProviderTests.WrongOwnerCannotDiscardProviderPayloadBeforeEscrowAdmission',
            'V6StatefulResumeAccountingTests.ConcurrentDiscardRetainsEscrowUntilProviderClosureAndHasOneWinner',
            'V6ManagedStatefulResumeProviderTests.SettlementInterruptionRetriesWithoutRepeatingProviderDiscard',
            'V6ManagedStatefulResumeProviderTests.ConcurrentResumeHasOneRestoreWinner',
            'V6ManagedStatefulResumeProviderTests.StorageAdmissionFailureWithAmbiguousProviderDiscardKeepsCaptureQuarantined',
            'V6ManagedStatefulResumeProviderTests.BindingPublicationFailureWithAmbiguousCleanupKeepsProviderAndStoragePinned',
            'V6ManagedStatefulResumeProviderTests.UnadmittedCaptureResetDoesNotImplyProviderPayloadClosure',
            'V6ManagedStatefulResumeProviderTests.UnpublishedSettlementRetryUsesOwnerBindingAfterNewerCapture',
            'SemanticAdmissionSentryTests.ProviderAdmissionRevokedDuringRuntimeLegalityFailsBeforeSubmit',
            'V6StatefulResumeAccountingTests.FinalResumeAdmissionRejectsPostCheckRevocationBeforeRestore',
            'V6StatefulResumeAccountingTests.FinalResumeProviderDriftBeforeRestoreKeepsStorageEscrowBound',
            'V6StatefulResumeAccountingTests.WrongSuspensionOwnerCannotInvokeResumeGatesOrProviderRestore',
            'V6ManagedStatefulResumeProviderTests.UnattachedCaptureOwnerCannotReachProviderOrCreateEscrow',
            'V6ManagedStatefulResumeProviderTests.CaptureRejectsNonCanonicalSemanticDigestBeforeRetainingState',
            'PreemptionContractsV1Tests.ResumeBindingCorrelatesExactGenerationsWithoutPreservingAuthority')
        publicApiDelta = 'ResumeBindingV1.Validate now rejects uppercase SHA-256 hex as noncanonical; schema and enum shape are unchanged. All identified producers use lowercase digests; v6 gates remain OFF.'
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim()
            sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim()
            targetFramework = 'net11.0'
        }
        commandsRun = @(
            "dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter $filter",
            "dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter $adapterFilter",
            'dotnet run --project tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p08-stateful-resume-performance --output artifacts/v6/p08-preemption/performance.json',
            "10 x dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter $raceFilter"
        )
        environment = [ordered]@{
            os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            javaChecks = 'SKIPPED_BY_INSTRUCTION'
        }
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        adapterTests = [ordered]@{ passed = $adapterPassed; failed = $adapterFailed; skipped = $adapterSkipped; total = $adapterTotal }
        concurrentResumeRepeat = [ordered]@{ passed = 10; failed = 0; skipped = 0; total = 10 }
        featureGates = @(
            [ordered]@{ gate = 'V6-PREEMPTION'; state = 'OFF' },
            [ordered]@{ gate = 'V6-STATEFUL-RESUME'; state = 'OFF' }
        )
        ownership = [ordered]@{
            singNext = 'fresh capability, Region, effect, and external-operation admission'
            provider = 'fresh provider-specific legality and provider generation'
            runtime = 'fresh runtime generation, exact dependency tuple, and submit linearization'
            evidence = 'guarantees, lifecycle events, receipts, and resume bindings carry no authority'
        }
        coverage = [ordered]@{
            vocabulary = @('cancel', 'drain', 'contain', 'restart', 'safe point', 'capture', 'suspend', 'resume remain distinct')
            restart = @('released staged ProviderLost predecessor', 'no publication', 'fresh three-owner sentries',
                'exact provider/runtime generations', 'one replacement submit',
                'fresh bound replacement budget', 'consumption before provider callback',
                'provider-loss quarantine and ordinary settlement')
            safePoint = @('named deterministic managed provider', 'provider-owned callback outside lock',
                'exact provider/runtime/operation generations', 'one attempt per operation correlation',
                'requested/draining/reached/contained lifecycle', 'managed-clock enforced latency bound',
                'timeout, callback loss, clock failure before/after hook, reset race, and concurrent duplicate fail closed',
                'receipt carries no execution, preemption, containment, or physical-latency authority')
            negative = @('cancellation is not containment', 'pre-submit confirmation requires owner release',
                'post-submit cancellation request is not a terminal provider acknowledgement', 'generation drift', 'fresh admission denial',
                'provider failure after owner submit', 'malformed guarantees and resume bindings')
            executableAdapter = @('exact external transition, close, and start receipts',
                'manifest-generation fault injection for transition, close, and start',
                'ambiguous external start blocks duplicate submit',
                'external effect remains quarantined until exact terminal close')
            statefulResume = @('opaque correlation carries no authority', 'CheckpointStorageBytes escrow',
                'binding and provider capture reject uppercase semantic/captured digests before storage retention',
                'fresh three-owner gates repeated after the single resume winner is claimed',
                'pre/post callback generation checks', 'one-winner resume',
                'post-check SingNext/provider/legality revocation and provider-generation drift deny restore while escrow stays bound',
                'callback ambiguity quarantine', 'reconciled discard', 'named managed capture/restore provider',
                'bounded opaque state copy and digest', 'provider/runtime reset invalidation',
                'captured payload zeroization and reference release after restore/discard',
                'rejected duplicate capture copy zeroization',
                'provider discard must precede storage release after exact owner preflight',
                'discard failure quarantines escrow and wrong owner cannot touch provider payload',
                'concurrent discard has one provider callback and retains escrow until provider closure')
            captureRollback = @('storage admission failure checks provider cleanup result',
                'ambiguous cleanup retains provider capture and denies duplicate correlation',
                'binding publication failure calls provider closure before budget release and pins storage on ambiguity',
                'unpublished binding recovery requires exact provider binding, unique quarantined suspension and budget owner; correlation is selection only',
                'wrong owner cannot query provider by captured-state correlation; owner preflight is repeated against exact provider binding before discard',
                'unpublished settlement retry resolves the owner-validated capture binding even after a newer capture reuses the correlation',
                'provider reset after unadmitted capture leaves provider-owned payload quarantined until exact discard')
            settlement = @('provider closure precedes budget reconciliation',
                'settlement interruption leaves Reconciled escrow charged',
                'retry skips provider discard after exact closure and settles once')
            performance = @('resume-binding validation baseline',
                'managed safe-point request/lifecycle/digest admission with immediate hook',
                'managed capture/copy/hash/discard/zeroization at 64 B, 4 KiB, and 64 KiB',
                'managed capture/admit/restore/re-hash/zeroization at 64 B, 4 KiB, and 64 KiB',
                'full 4 KiB capture/suspend/fresh-admission/resume/storage-settlement contour',
                'rotating Release JIT named-host rounds', 'median/p95/p99 elapsed time', 'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            resumeBindingValidationMedianNanoseconds = [double]$performance.summary.resumeBindingValidationMedianNanoseconds
            managedSafePointRequestMedianNanoseconds = [double]$performance.summary.managedSafePointRequestMedianNanoseconds
            captureDiscard64BMedianNanoseconds = [double]$performance.summary.captureDiscard64BMedianNanoseconds
            captureDiscard4KMedianNanoseconds = [double]$performance.summary.captureDiscard4KMedianNanoseconds
            captureDiscard64KMedianNanoseconds = [double]$performance.summary.captureDiscard64KMedianNanoseconds
            captureAdmitRestore64BMedianNanoseconds = [double]$performance.summary.captureAdmitRestore64BMedianNanoseconds
            captureAdmitRestore4KMedianNanoseconds = [double]$performance.summary.captureAdmitRestore4KMedianNanoseconds
            captureAdmitRestore64KMedianNanoseconds = [double]$performance.summary.captureAdmitRestore64KMedianNanoseconds
            fullContour4KMedianNanoseconds = [double]$performance.summary.fullContour4KMedianNanoseconds
            supportsGatePromotion = $false
            interpretation = 'Named deterministic managed model copy/hash/lifecycle and storage-accounting overhead only; no safe-point or physical-provider latency claim.'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tools/HybridCpu_ExecutableAdapter.Tests/bin/Debug/net11.0/HybridCpu_ExecutableAdapter.Tests.dll',
            'tools/HybridCpu_ExecutableAdapter/packages.lock.json'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary/package evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        maximumSupportedClaim = [ordered]@{
            preemptionVocabulary = 'StaticAdmission'
            managedRestartOnly = 'RuntimeEnforced on the internal staged ProviderLost contour'
            providerSpecificSafePoint = 'ExecutableAdapter on the named deterministic managed reference provider; bound admission is RuntimeEnforced'
            stateCaptureAndResume = 'RuntimeEnforced orchestration against a named managed model provider; provider guarantee remains ModelOnly and has no latency claim'
            latencyGuarantee = 'EnforcedUpperBound only for the deterministic managed clock/hook contour; physical-provider latency is FutureGated'
            restartBudgetAccounting = 'RuntimeEnforced on the internal restart-only contour'
            statefulSuspendResumeAccounting = 'RuntimeEnforced storage escrow on the internal managed contour'
            statefulResumePerformance = 'ModelOnly for the named deterministic managed models; safe-point request overhead is measured but physical-provider interruption latency remains FutureGated'
            hybridCpuChildAdapter = 'ExecutableAdapter for the tested single-host V3 terminal execution and receipt-fault handling only; CPU park is FutureGated in SingNext'
            hardware = 'FutureGated'
            production = 'FutureGated'
        }
        remainingBlockers = @(
            'V6-PREEMPTION remains OFF and the restart entry point is internal.',
            'Only the deterministic managed reference provider exposes a qualified safe point and managed-clock bound; no CPU, MatrixTile, DSC, L7, external, or physical provider does.',
            'V6-STATEFUL-RESUME remains OFF; the named capture/restore provider is a managed model, not a CPU, MatrixTile, DSC, L7, or physical provider.',
            'Unpublished binding recovery is qualified only for the deterministic managed provider and existing budget owner; no physical provider closure source exists. A failed storage admission has no escrow and requires provider-owner exact discard.',
            'No hardware, production, or formal refinement qualification exists.'
        )
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        changedFiles = @(
            'src/Runtime/SingPlus.Runtime/V6/V6ManagedSafePointProvider.cs',
            'tests/SingPlus.Tests/Runtime/V6ManagedSafePointProviderTests.cs',
            'src/Runtime/SingPlus.Runtime/V6/V6ManagedStatefulResumeProvider.cs',
            'src/Runtime/SingPlus.Runtime/V6/V6StatefulResumeAccounting.cs',
            'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs',
            'tests/SingPlus.Tests/Runtime/V6ManagedStatefulResumeProviderTests.cs',
            'tests/SingPlus.Tests/Runtime/V6StatefulResumeAccountingTests.cs',
            'src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs',
            'tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs',
            'eng/v6/Qualify-V6P08Preemption.ps1',
            'tools/HybridCpu_ExecutableAdapter/Adapter/HybridCpuExecutableChildAdapter.cs',
            'tools/HybridCpu_ExecutableAdapter.Tests/AdapterBoundaryTests.cs',
            'tools/HybridCpu_ExecutableAdapter.Tests/SemanticTraceInstrumentationTests.cs'
        )
        rollbackFallback = 'Keep both P08 gates OFF and retain existing cancellation/drain/provider-loss behavior.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P08 preemption/restart evidence

- Result: $passed/$total selected contract, cancellation, lifecycle, admission-sentry, restart, managed safe-point, and stateful-accounting tests passed.
- HybridCPU executable adapter: $adapterPassed/$adapterTotal selected boundary and trace/fault-injection tests passed; external CPU park is not yet connected as a SingNext safe-point contour.
- Concurrent resume winner: 10/10 additional isolated repeats passed; each run had one restore winner.
- Claims: vocabulary = StaticAdmission; managed restart-only = RuntimeEnforced only for the internal staged ProviderLost contour.
- Safety: pre-submit confirmation follows owner release; post-submit cancellation receipts do not imply containment. Restart revalidates SingNext, provider, runtime, budget, exact dependencies, and generations before submit.
- Managed safe point: the named deterministic reference provider runs its hook outside the provider lock, accepts one exact generation-bound attempt, and emits requested/draining/reached/contained evidence only within its managed-clock bound.
- Clock fault: failure before the hook invokes no provider callback; failure after a possible hook effect quarantines the attempt without a safe-point receipt. The exact correlation cannot be retried.
- Budget: a fresh bound replacement reservation becomes Consuming before the provider callback, quarantines on provider loss, and settles only through ResourceBudgetAuthority.
- Stateful managed model: bounded opaque capture is digest/generation-bound, pins exact CheckpointStorageBytes, performs fresh three-owner admission, has one restore winner, zeroizes rejected copies and terminal payloads, releases terminal payload references while retaining receipts, and quarantines ambiguous loss.
- Resume revalidation: after claiming the single in-flight winner, SingNext admission, provider admission, runtime legality, and provider/runtime generations are checked again before restore. A newly denied decision leaves escrow bound and permits explicit discard; provider restore is not called.
- Resume owner boundary: exact checkpoint-storage escrow owner is checked before any SingNext, provider, or runtime gate callback; the escrow is checked again before claiming the restore winner. A stale owner reaches no callback.
- Capture owner boundary: the exact process generation must already be attached to ResourceBudgetAuthority before managed provider capture; final checkpoint reservation remains authoritative after capture, and failed reservation still uses exact provider discard or quarantine.
- Unpublished recovery owner boundary: accounting checks the quarantined escrow owner and correlation before provider query, then verifies the exact provider binding again before discard.
- Canonical digest: `ResumeBindingV1` and the managed capture boundary reject uppercase SHA-256 hex; rejected capture retains no payload. Contract shape and enum values are unchanged.
- Discard ordering: exact owner and escrow are checked before the provider callback. Provider discard failure quarantines storage; successful provider discard precedes budget release. Wrong owner cannot discard captured payload, including during reconciliation.
- Settlement retry: once provider discard has succeeded, a budget settlement interruption remains charged in Reconciled state; retry skips the provider callback and settles through ResourceBudgetAuthority.
- Capture rollback: storage-admission and binding-publication failure check provider discard; ambiguous cleanup retains the provider record and any admitted storage escrow in quarantine. The managed unpublished binding can reconcile through exact provider binding and budget owner without treating correlation as authority.
- Unadmitted capture reset: provider reset does not close or free a captured payload after failed storage admission. Only exact provider-owned discard clears it in the deterministic model; this is not physical reset containment.
- Named-host managed-model medians: binding validation = $([double]$performance.summary.resumeBindingValidationMedianNanoseconds) ns; safe-point request/evidence = $([double]$performance.summary.managedSafePointRequestMedianNanoseconds) ns; capture/discard = $([double]$performance.summary.captureDiscard64BMedianNanoseconds) / $([double]$performance.summary.captureDiscard4KMedianNanoseconds) / $([double]$performance.summary.captureDiscard64KMedianNanoseconds) ns at 64 B / 4 KiB / 64 KiB; capture/admit/restore = $([double]$performance.summary.captureAdmitRestore64BMedianNanoseconds) / $([double]$performance.summary.captureAdmitRestore4KMedianNanoseconds) / $([double]$performance.summary.captureAdmitRestore64KMedianNanoseconds) ns; full 4 KiB contour = $([double]$performance.summary.fullContour4KMedianNanoseconds) ns.
- Performance boundary: managed memory copy/hash/zeroization, lifecycle, fresh admission, and CheckpointStorageBytes accounting only; no safe-point bound, physical provider, durable storage I/O, or gate promotion is inferred.
- Gates: `V6-PREEMPTION` and `V6-STATEFUL-RESUME` remain OFF.
- Stateful resume: the named managed provider is ModelOnly; captured state and resume bindings preserve no capability or authority.
- Not claimed: CPU/MatrixTile/DSC/L7/external/physical safe points, physical bounded latency, hardware, production, or formal refinement.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    $regressionPath = Join-Path $OutputDirectory 'regression.json'
    $manifestInputs = @($performancePath, $jsonPath, $markdownPath)
    if (Test-Path -LiteralPath $regressionPath -PathType Leaf) { $manifestInputs += $regressionPath }
    $manifestInputs | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally {
    Pop-Location
}
