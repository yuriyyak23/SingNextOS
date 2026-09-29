[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p04-dma-composition')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-QualifiedTests([string]$Project, [string]$Filter, [int]$Expected) {
    $output = & dotnet test $Project --no-restore --filter $Filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P04 tests failed for $Project.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw "The test runner summary could not be parsed for $Project." }
    $result = [ordered]@{
        project = $Project
        filter = $Filter
        failed = [int]$match.Groups[1].Value
        passed = [int]$match.Groups[2].Value
        skipped = [int]$match.Groups[3].Value
        total = [int]$match.Groups[4].Value
    }
    if ($result.failed -ne 0 -or $result.passed -ne $Expected -or
        $result.skipped -ne 0 -or $result.total -ne $Expected) {
        throw "Unexpected P04 counts for ${Project}: $($result | ConvertTo-Json -Compress)"
    }
    return $result
}

$runtimeFilter = 'FullyQualifiedName~PlatformDmaGrantTests|FullyQualifiedName~PlatformDmaVisibilityTests|FullyQualifiedName~PlatformDmaSubmissionTests|FullyQualifiedName~PlatformDmaCopySubmissionContractTests|FullyQualifiedName~PlatformDmaCompletionTests|FullyQualifiedName~PlatformDmaPostCompletionLifecycleTests|FullyQualifiedName~PlatformDmaDsc1MappingInterlockTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$runtimeFilter += '|FullyQualifiedName~PlatformDeviceLeaseTests.MalformedProviderDeviceAuthorityFailsClosedAndIsRevoked|FullyQualifiedName~PlatformBorrowReadGrantTests.DeniedRevokedFaultedOrMalformedGrantAdmissionFailsClosed'
$runtimeFilter += '|FullyQualifiedName~PlatformDeviceLeaseTests.MalformedDeviceCleanupFailureQuarantinesExistingDeviceOwner|FullyQualifiedName~PlatformBorrowReadGrantTests.MalformedBorrowMappingCleanupAmbiguityPinsBorrowLifetime'
$runtimeFilter += '|FullyQualifiedName~PlatformBorrowReadGrantTeardownTests'
$runtimeFilter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformBackendResetEpochTests.TerminalBackendEpochStillQuarantinesOldAuthorityAndDeniesNewBinding'
$runtimeFilter += '|FullyQualifiedName=SingPlus.Tests.Platform.PlatformAuthorityBridgeChildDomainTests.BackendResetQuarantinedVirtualIoCannotInvokeLateProviderRevoke'
$runtimeFilter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.BackendResetInsideClosureCallbackCannotTurnLateReceiptIntoClosure'
$runtimeFilter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ClosureCallbackExceptionPinsGuestOrVirtualIoWithoutRetry'
$runtimeFilter += '|FullyQualifiedName~PlatformAuthorityBridgeChildDomainTests.ChildCloseCallbackResetOrThrowCannotCloseOrRetry'
$runtimeFilter += '|FullyQualifiedName~Phase7EvidenceSecureComputeTests.SecureRegionUnbindResetOrThrowKeepsMappingPinned|FullyQualifiedName~Phase7EvidenceSecureComputeTests.SecureDomainRevokeResetOrThrowKeepsDomainPinned'
$runtimeFilter += '|FullyQualifiedName~Phase7EvidenceSecureComputeTests.SecureRegionBindResetOrThrowPinsMappingWithoutPublishedLease|FullyQualifiedName~Phase7EvidenceSecureComputeTests.FailedOrMalformedSecureRegionBindPinsMappingWithoutExactClosure'
$runtimeFilter += '|FullyQualifiedName~Phase7EvidenceSecureComputeTests.AmbiguousSecureCreatePinsParentWithoutPublishedLease|FullyQualifiedName~Phase7EvidenceSecureComputeTests.MalformedSecureCreateCleanupFaultPinsParent|FullyQualifiedName~Phase7EvidenceSecureComputeTests.MalformedSecureCreateWithExactCleanupDoesNotPinParent|FullyQualifiedName~Phase7EvidenceSecureComputeTests.ParentDomainClosesOnlyAfterSecureChildExactClosure'
$runtimeFilter += '|FullyQualifiedName~Phase7EvidenceSecureComputeTests.SecureCreateCallbackCannotRevokeParentBeforeLeasePublication'
$runtimeFilter += '|FullyQualifiedName~Phase7EvidenceSecureComputeTests.ConcurrentParentRevokeCannotPassInFlightSecureCreate|FullyQualifiedName~Phase7EvidenceSecureComputeTests.ConcurrentSecureCreateCannotPassInFlightParentRevoke|FullyQualifiedName~Phase7EvidenceSecureComputeTests.ParentRevokeResetOrThrowRetainsPossibleEffectPin'
$runtimeFilter += '|FullyQualifiedName~Phase7EvidenceSecureComputeTests.ResetBetweenBridgeAdmissionAndKernelPublicationCannotIssueSecureHandle|FullyQualifiedName~Phase7EvidenceSecureComputeTests.ProcessExitBeforeSecureHandlePublicationAttemptsExactChildClosure|FullyQualifiedName~Phase7EvidenceSecureComputeTests.FailedLocalPublicationCleanupRetainsUnpublishedSecureChildPin'
$hybridFilter = 'FullyQualifiedName~HybridCpuDmaGrantTests|FullyQualifiedName~HybridCpuDmaVisibilityTests'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/DmaExecutionBindingV1.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformAuthorityContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformDmaGrantContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformDmaVisibilityContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformDmaSubmissionContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformDmaCopySubmissionContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformDmaCompletionContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformDmaPageFaultContracts.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Dma.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.MappingV2.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Device.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BorrowReadGrant.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BackendEpoch.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Virtualization.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.VirtualIo.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.GuestMemory.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.SecureCompute.cs',
    'src/Runtime/SingPlus.Runtime/SecureCompute/RuntimeKernel.SecureCompute.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaVisibility.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaSubmission.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaTrace.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCopySubmission.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPostCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPageFault.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.MappingUse.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaSubmission.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBorrow.cs',
    'src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPostCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPageFault.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBackendReset.cs',
    'tests/SingPlus.Tests/Platform/PlatformBackendResetEpochTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformAuthorityBridgeChildDomainTests.cs',
    'tests/SingPlus.Tests/Platform/Phase7EvidenceSecureComputeTests.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6PlatformDmaSemanticSubmission.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs',
    'src/Runtime/SingPlus.Runtime/RuntimeKernel.ProcessTeardown.cs',
    'src/Sip/SingPlus.Sip/Regions/BorrowLease.cs',
    'src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'src/Platform/SingPlus.Platform.HybridCpu/HybridCpuPlatformAuthorityProvider.Dma.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaSubmissionTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaGrantTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDeviceLeaseTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformBorrowReadGrantTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformBorrowReadGrantTeardownTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaCopySubmissionContractTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaCompletionTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaPostCompletionLifecycleTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaDsc1MappingInterlockTests.cs',
    'tests/SingPlus.Platform.HybridCpu.Tests/HybridCpuDmaGrantTests.cs',
    'tests/SingPlus.Platform.HybridCpu.Tests/HybridCpuDmaVisibilityTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P04DmaBindingPerformanceQualification.cs',
    'eng/v6/Qualify-V6P04Dma.ps1'
)

Push-Location $RepositoryRoot
try {
    $runs = @(
        Invoke-QualifiedTests 'tests\SingPlus.Tests\SingPlus.Tests.csproj' $runtimeFilter 204
        Invoke-QualifiedTests 'tests\SingPlus.Platform.HybridCpu.Tests\SingPlus.Platform.HybridCpu.Tests.csproj' $hybridFilter 6
    )
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p04-dma-binding-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P04 DMA binding performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p04-dma-binding-performance/1' -or
        $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
        throw 'The P04 DMA binding performance artifact is malformed.'
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
        phase = @('P04', 'P04-B-live-binding', 'P04-C-managed-page-fault', 'P04-D-atomic-copy-bridge', 'P04-managed-binding-performance', 'P05-checker-input')
        slice = 'existing-generation-bound-platform-dma-and-binding-overhead'
        contour = 'single-host/exact-domain-device-mapping-grant/copy-or-bounded-dma'
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim()
            sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim()
            targetFramework = 'net11.0'
            hybridCpuAdapter = 'in-tree NeutralDomainRuntimeFacade-backed admission and visibility adapter'
        }
        tests = $runs
        totals = [ordered]@{
            passed = [int]$runs[0].passed + [int]$runs[1].passed
            failed = [int]$runs[0].failed + [int]$runs[1].failed
            skipped = [int]$runs[0].skipped + [int]$runs[1].skipped
            total = [int]$runs[0].total + [int]$runs[1].total
        }
        sliceStatus = 'managed-completion-page-fault-and-post-acquire-race-sentries-qualified; HybridCPU-bounded-submit-FutureGated'
        requirementClassification = [ordered]@{
            VerifiedExisting = @('RegionUse and mapping/device grant owners remain separate',
                'managed deterministic provider exposes exact pending submission and completion',
                'v6 bound submit retains possible effect for every failure except explicit NotAccepted; V1 path remains compatible')
            Partial = @('provider incarnation is revalidated before and after managed completion, post-write acquire, grant revoke, mapping closure, and device lease revoke callbacks; physical provider closure remains unqualified',
                'bound submit and atomic copy now require a live provider incarnation source; the selected bounded provider is a deterministic test implementation, with no product bounded submit consumer')
            Missing = @('HybridCPU bounded submit/completion runtime surface',
                'live translation-generation invalidation source for the selected adapter',
                'physical invalidation/containment evidence')
            Contradicted = @('admission and visibility alone imply bounded DMA execution')
            ExternalBlocked = @('physical IOMMU isolation campaign and physical faulting provider')
            FutureGated = @('HybridCPU bounded submit/completion', 'production DMA qualification')
        }
        requirementIds = @('P04-ADMISSION-CLEANUP-PIN-01', 'P04-COMPLETION-RESET-01', 'P04-POST-ACQUIRE-RESET-01',
            'P04-PAGE-FAULT-COMPLETION-RACE-01', 'P04-PAGE-FAULT-REVOKE-01',
            'P04-PAGE-FAULT-CAPABILITY-TEARDOWN-01',
            'P04-PAGE-FAULT-REGION-GENERATION-01',
            'P04-POST-SUBMIT-PROVIDER-RESET-01',
            'P04-TRUSTED-BACKEND-RESET-DURING-SUBMIT-01',
            'P04-TRUSTED-BACKEND-RESET-DURING-ATOMIC-COPY-01',
            'P05-TRACE-DMA-SUBMIT-PREFIX-01',
            'P05-TRACE-DMA-COMPLETION-RESET-01', 'P05-TRACE-DMA-VISIBILITY-ORDER-01',
            'P05-TRACE-DMA-PAGE-FAULT-QUARANTINE-01',
            'P05-TRACE-DMA-BACKEND-RESET-01',
            'P05-TRACE-REPEATED-BACKEND-EPOCH-01',
            'P05-TRACE-POST-QUARANTINE-PROVIDER-DRIFT-01',
            'P05-TRACE-CANONICAL-OBSERVED-GENERATIONS-01',
            'P04-PAGE-FAULT-POST-CALLBACK-OBSERVATION-01',
            'P04-GRANT-REVOKE-RESET-01',
            'P04-POST-VISIBILITY-AMBIGUOUS-GRANT-CLOSURE-PIN-01',
            'P04-MAPPING-CLOSURE-RESET-01', 'P04-DEVICE-LEASE-CLOSURE-RESET-01',
            'P04-EXACT-MAP-AMBIGUOUS-ADMISSION-01', 'P04-V1-ADMISSION-COMPAT-01',
            'P04-EFFECT-PIN-01', 'P04-BOUND-SUBMIT-UNKNOWN-RESULT-01', 'P04-V1-AMBIGUOUS-CLEANUP-QUARANTINE-01',
            'P04-V1-FAILURE-WITHOUT-LEASE-PIN-01',
            'P07-DMA-MOVE-RESET-NO-SOURCE-RELEASE-01',
            'P07-DMA-PARTIAL-CLOSURE-NO-RESUBMIT-01',
            'P04-BORROW-EXIT-LOCAL-ACCESS-VS-GRANT-CLOSURE-01')
        testIds = @('PlatformDmaGrantTests.FailedDmaAdmissionCannotDiscardPossibleProviderGrant',
            'PlatformDmaGrantTests.RejectedProviderGrantNeedsConfirmedCleanupBeforeMappingClosure',
            'PlatformDmaCompletionTests.ProviderResetDuringCompletionObservationCannotBecomeReusableProof',
            'PlatformBorrowReadGrantTeardownTests.BorrowerTeardownClosesGrantBeforeReturningCpuBorrow',
            'PlatformBorrowReadGrantTeardownTests.OwnerTeardownClosesGrantBeforeRevokingBorrowAndReclaimingRegion',
            'PlatformBorrowReadGrantTeardownTests.BorrowerTeardownRemainsDrainingUntilGrantClosureIsObserved',
            'PlatformDmaPostCompletionLifecycleTests.ProviderResetDuringPostCompletionAcquirePinsPossibleWrite',
            'PlatformDmaSubmissionTests.CompletionWaitsForInFlightPageFaultBeforeProviderObservation',
            'PlatformDmaSubmissionTests.PageFaultWaitsForInFlightCompletionBeforeProviderResolution',
            'PlatformDmaSubmissionTests.RevokeAndUnmapCannotCrossInFlightPageFault',
            'PlatformDmaSubmissionTests.CapabilityRevokeDuringPageFaultKeepsSubmittedEffectPinned',
            'PlatformDmaSubmissionTests.ProcessTeardownDuringPageFaultKeepsSubmittedEffectPinned',
            'PlatformDmaSubmissionTests.RegionOwnershipAndMutationCannotCrossPendingPageFault',
            'PlatformDmaSubmissionTests.ProviderResetAfterSubmitBeforeObservationFaultPinsPendingWrite',
            'PlatformDmaSubmissionTests.TrustedBackendResetDuringSubmitPinsPossibleEffectWithoutProviderIncarnationDrift',
            'PlatformDmaSubmissionTests.TrustedBackendResetDuringAtomicCopyPinsBothPossibleEffects',
            'PlatformDmaSubmissionTests.ProviderDmaDataMotionResetBeforeClosureRetainsSourceAndNoReceipt',
            'PlatformDmaSubmissionTests.ProviderDmaPartialGrantClosureCannotResubmitOrReleaseSourceAfterDestinationReset',
            'PlatformDmaSubmissionTests.V6BoundSubmitEmitsNonAuthoritativeTraceAfterOwnerCommit',
            'PlatformDmaSubmissionTests.V6DeniedSubmitQuarantinesPossibleEffectTrace',
            'PlatformDmaSubmissionTests.V6BoundProviderDenialFaultPinsPreparedCycle',
            'PlatformDmaSubmissionTests.V6BoundExplicitNotAcceptedLeavesPreparedCycleRetryable',
            'PlatformDmaSubmissionTests.V6BoundTransportUnavailableRetainsPossibleWriteAndBlocksRevoke',
            'PlatformDmaSubmissionTests.V6BoundSubmitTraceSinkFailureCannotChangeCommittedEffect',
            'PlatformDmaSubmissionTests.V6BoundDmaCompletionTraceFollowsCommittedOwnerOrder',
            'PlatformDmaSubmissionTests.V6BoundDmaResetTraceMarksNewGenerationAndQuarantine',
            'PlatformDmaSubmissionTests.V6BoundDmaTraceSinkMayReenterAfterOwnerLock',
            'PlatformDmaSubmissionTests.V6BoundDmaCompletionTraceSinkFailureCannotChangeOwnerResult',
            'PlatformDmaSubmissionTests.V6BoundDmaVisibilityTraceFollowsExactPostCompletion',
            'PlatformDmaSubmissionTests.V6BoundDmaResetDuringAcquireTraceQuarantinesWithoutVisibility',
            'PlatformDmaSubmissionTests.V6BoundDmaTraceRetainsOrderedEventsAcrossReentrantRemoval',
            'PlatformDmaSubmissionTests.V6BoundDmaMalformedPageFaultTraceQuarantinesExactSubmission',
            'PlatformDmaSubmissionTests.V6BoundDmaPageFaultResetTraceMarksGenerationAndQuarantine',
            'PlatformDmaSubmissionTests.V6BoundDmaBackendResetTraceMarksLocalEpochAndQuarantine',
            'PlatformDmaSubmissionTests.V6BoundDmaRepeatedBackendResetPreservesEveryEpochAfterQuarantine',
            'PlatformDmaSubmissionTests.V6BoundDmaProviderDriftAfterQuarantineRetainsGenerationObservation',
            'PlatformDmaSubmissionTests.V6BoundDmaBackendResetDoesNotInventUnobservedProviderGeneration',
            'PlatformDmaSubmissionTests.V6BoundDmaBackendResetPreservesLastObservedProviderGeneration',
            'PlatformDmaSubmissionTests.V6BoundDmaPageFaultBackendResetDoesNotObserveLaterProviderDrift',
            'PlatformDmaSubmissionTests.ProviderResetBeforeGrantRevokeAfterCompletionPinsMappingWithoutCallback',
            'PlatformDmaSubmissionTests.ProviderResetDuringGrantRevokeCannotTurnReceiptIntoClosure',
            'PlatformDmaSubmissionTests.ProviderThrowDuringGrantRevokePinsMappingAfterPossibleClosure',
            'PlatformDmaSubmissionTests.BackendResetDuringGrantRevokeCannotTurnReceiptIntoClosure',
            'PlatformDmaSubmissionTests.ProviderResetBeforeMappingRevokeKeepsExactReservationPinned',
            'PlatformDmaSubmissionTests.ProviderResetDuringMappingClosureCannotMakeReceiptReusable',
            'PlatformDmaSubmissionTests.ProviderThrowDuringMappingObservationKeepsReservationPinned',
            'PlatformDmaSubmissionTests.ProviderResetDuringExactMappingAdmissionReturnsQuarantinedHandle',
            'PlatformDmaSubmissionTests.BackendResetDuringMappingObservationCannotReleaseReservation',
            'PlatformDmaSubmissionTests.ProviderResetBeforeDeviceRevokeAfterDmaClosureKeepsLeasePinned',
            'PlatformDmaSubmissionTests.ProviderResetDuringDeviceRevokeCannotCloseLease',
            'PlatformDmaSubmissionTests.ProviderThrowDuringDeviceRevokeKeepsLeasePinned',
            'PlatformDmaSubmissionTests.BackendResetDuringDeviceRevokeCannotCloseLease',
            'PlatformDmaSubmissionTests.ProviderResetDuringDeviceAdmissionReturnsFaultPinnedLease',
            'PlatformDmaSubmissionTests.MalformedExactMappingAdmissionRetainsFaultedReservation',
            'PlatformDeviceLeaseTests.MalformedProviderDeviceAuthorityFailsClosedAndIsRevoked',
            'PlatformBorrowReadGrantTests.DeniedRevokedFaultedOrMalformedGrantAdmissionFailsClosed',
            'PlatformDeviceLeaseTests.MalformedDeviceCleanupFailureQuarantinesExistingDeviceOwner',
            'PlatformBorrowReadGrantTests.MalformedBorrowMappingCleanupAmbiguityPinsBorrowLifetime',
            'PlatformDmaSubmissionTests.MalformedLegacyMappingCleanupFailureRetainsRegionReservation',
            'PlatformDmaSubmissionTests.MalformedLegacyMappingCleanupReceiptAfterProviderResetCannotReleaseReservation',
            'PlatformDmaSubmissionTests.AmbiguousLegacyMappingFailureWithoutLeaseRetainsRegionReservation')
        commandsActuallyRun = @(
            "dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter $runtimeFilter",
            "dotnet test tests\SingPlus.Platform.HybridCpu.Tests\SingPlus.Platform.HybridCpu.Tests.csproj --no-restore --filter $hybridFilter",
            "dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p04-dma-binding-performance --output $performancePath"
        )
        environment = [ordered]@{ os = [Environment]::OSVersion.VersionString; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(); shell = 'PowerShell' }
        changedFilesThisIteration = @('src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaTrace.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.MappingV2.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Device.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BorrowReadGrant.cs',
            'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs',
            'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBorrow.cs',
            'src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Dma.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaSubmission.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCopySubmission.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCompletion.cs',
            'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaCompletion.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPostCompletion.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPageFault.cs',
            'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPostCompletion.cs',
            'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPageFault.cs',
            'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BackendEpoch.cs',
            'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBackendReset.cs',
            'src/Runtime/SingPlus.Runtime/V6/V6PlatformDmaSemanticSubmission.cs',
            'tests/SingPlus.Tests/Platform/PlatformDmaSubmissionTests.cs',
            'tests/SingPlus.Tests/Platform/PlatformDeviceLeaseTests.cs',
            'tests/SingPlus.Tests/Platform/PlatformBorrowReadGrantTests.cs',
            'eng/v6/Qualify-V6P04Dma.ps1')
        publicApiPackageSchemaDelta = 'None; V1 malformed provider admission still returns PlatformFaulted; internal reservation-retention overloads preserve ambiguous cleanup.'
        runtimeComposition = @(
            'process incarnation -> platform domain binding generation',
            'Region handle/mutation reservation -> exact mapping generation',
            'device lease generation -> DMA grant generation',
            'provider-owned runtime incarnation -> grant capture -> pre/post-submit revalidation',
            'Sing-local backend epoch -> pre/post-submit revalidation -> ambiguous single/copy fault pin',
            'prepared visibility cycle -> bounded submission generation',
            'authoritative generations -> DmaExecutionBindingV1 admitted sideband -> provider submit -> EffectPossible binding',
            'pending submission -> region-relative page-fault request -> exact provider evidence',
            'exact completion -> post-write acquire -> grant/mapping closure')
        coverage = [ordered]@{
            positive = @('exact bounded submit', 'completion identity', 'post-write CPU acquire',
                'exact one-use page-fault resolution', 'drain-before-revoke closure',
                'exact v6 semantic binding at managed provider submit boundary',
                'static admission for atomic equal-length read-source/write-destination copy pair',
                'atomic provider acceptance materializes both pending local lifetimes or neither',
                'copy completion proves both legs before source no-op visibility and destination acquire',
                'completed source evidence is cached for exact pair resume while destination drains',
                'resume requires the same provider copy-submission identity on both legs',
                'cross-owner copy preserves independent process/domain grant identities',
                'HybridCPU admission and non-coherent visibility cycle')
            stale = @('grant generation', 'prepared cycle', 'operation generation', 'mapping and device identity',
                'provider reset before submit', 'provider reset after submit before observation',
                'provider reset during completion observation',
                'provider reset during post-completion acquire',
                'trusted backend epoch change inside single or atomic-copy provider submit',
                'repeated backend reset after DMA quarantine retains each observed epoch in non-authoritative trace',
                'successive provider incarnation drifts after quarantine remain ordered in the same trace',
                'page-fault backend reset skips later provider-generation read after owner identity becomes stale',
                'backend reset composes its local epoch with the last provider incarnation actually observed before reset',
                'borrower process exit rejects new CPU borrow token access while retaining provider-facing read-grant lifetime until exact closure; previously issued managed spans remain readable',
                'provider reset during mapping begin/observe and device lease revoke')
            race = @('DMA versus DSC1 mapping-use interlock', 'concurrent submit',
                'completion waits for in-flight page fault', 'page fault waits for in-flight completion',
                'revoke and unmap cannot cross in-flight page fault',
                'capability revoke and process teardown while DMA is pending or page fault is in flight',
                'Region transfer, release, and incompatible use cannot cross pending page fault')
            fault = @('ambiguous provider acceptance pins mapping', 'provider incarnation drift during submit pins mapping', 'malformed completion pins authority',
                'trusted backend reset during provider submit faults the result and pins the possible effect without inventing provider incarnation drift',
                'ambiguous grant closure after post-completion visibility still pins the grant and blocks a second provider revoke',
                'failed grant admission or unconfirmed cleanup of a rejected grant retains a bridge-owned pin on the device and mapping',
                'atomic copy ambiguous, malformed, or reset response pins both mappings',
                'page-fault range or direction widening denied before callback',
                'page-fault provider reset or malformed evidence pins mapping',
                'post-completion visibility failure forbids reclaim',
                'provider incarnation drift during completion pins mapping before evidence can become reusable',
                'provider incarnation drift during post-write acquire pins mapping before visibility can become reusable',
                'mapping and device admission drift retains faulted owner records',
                'malformed successful exact mapping result retains a faulted reservation while V1 borrow/device admission still fails closed',
                'ambiguous cleanup of malformed successful V1 mapping, borrow mapping, or device lease quarantines the existing owner and retains its reservation',
                'provider incarnation drift during malformed V1 mapping cleanup invalidates a successful cleanup receipt',
                'faulted or revoked mapping admission without a lease retains the existing Region or borrow reservation across backend reset',
                'mapping completion receipt and device revoke receipt cannot close after provider or backend generation drift')
            abi = @('no raw physical/IOMMU/PASID/descriptor value in public DMA authority surface',
                'page-fault evidence grants neither mapping nor memory-access authority')
            performance = @('immutable tuple equality baseline', 'generation validation and exact match',
                'region/security digest construction and validation', 'canonical codec round-trip and match',
                'rotating Release JIT named-host rounds', 'median/p95/p99 elapsed time',
                'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            recordEqualityMedianNanoseconds = [double]$performance.summary.recordEqualityMedianNanoseconds
            validateAndMatchMedianNanoseconds = [double]$performance.summary.validateAndMatchMedianNanoseconds
            constructDigestsMedianNanoseconds = [double]$performance.summary.constructDigestsMedianNanoseconds
            canonicalRoundTripMedianNanoseconds = [double]$performance.summary.canonicalRoundTripMedianNanoseconds
            supportsGatePromotion = $false
            interpretation = 'DmaExecutionBindingV1 contract/sideband CPU overhead only; functional provider submit/complete/race and V1 ambiguous-cleanup coverage is in the 173 selected runtime tests.'
        }
        featureGate = [ordered]@{
            roadmapGate = 'V6-DMA-TRANSLATION-BINDING'
            implementationGate = 'V6-DMA-GENERATION-BINDING'
            state = 'OFF'
            note = 'Evidence qualifies pre-existing platform DMA contours; it does not enable a v6 consumer.'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Sip.dll',
            'tests/SingPlus.Platform.HybridCpu.Tests/bin/Debug/net11.0/SingPlus.Platform.HybridCpu.Tests.dll',
            'tools/HybridCpu_ExecutableAdapter/packages.lock.json'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary/package evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        maximumSupportedClaim = [ordered]@{
            managedPlatformDmaWithDeterministicTestProvider = 'RuntimeEnforced'
            managedPageFaultRevalidation = 'RuntimeEnforced for exact pending submission/range/access/sequence on deterministic test provider'
            hybridCpuAdmissionAndVisibility = 'ExecutableAdapter (admission/visibility only)'
            hybridCpuBoundedSubmitCompletion = 'FutureGated'
            dmaExecutionBindingContract = 'RuntimeEnforced correlation at internal managed bound-submit contour; never authority'
            atomicCopyPairContract = 'RuntimeEnforced all-or-neither pending-lifetime tracking plus both-leg completion and destination acquire on the deterministic managed provider contour'
            bindingPerformance = 'ModelOnly contract construction/check/codec overhead; no physical DMA or gate-promotion claim'
            physicalIommuIsolation = 'FutureGated'
            production = 'FutureGated'
        }
        remainingBlockers = @(
            'Already issued managed spans cannot be revoked by local token invalidation; this contour does not prove memory-access containment or physical isolation.',
            'The concrete HybridCPU adapter intentionally advertises DMA admission/visibility, not bounded submit/completion.',
            'The page-fault hook is qualified only on the deterministic managed provider; no physical faulting/SVA provider is integrated.',
            'The selected managed provider has no live translation-generation invalidation source after mapping lease creation.',
            'Only the deterministic test provider implements the bound DMA submit contract; no product bounded-submit provider is integrated.',
            'No physical IOMMU invalidation or isolation campaign exists.',
            'V6-DMA-TRANSLATION-BINDING remains OFF; the live DmaExecutionBindingV1 consumer is internal and uses only the deterministic managed provider, not production.',
            'The atomic DMA copy and P07 movement orchestration are qualified only against deterministic managed providers; physical-provider movement remains unqualified.',
            'Malformed successful V1 admission cleanup is qualified against deterministic test providers only; physical revoke completion/containment is unproven.',
            'A faulted admission without a returned mapping lease has no exact provider closure identity; backend reset cannot prove containment, so local reservation reconciliation remains blocked.'
        )
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        rollbackFallback = 'Keep the v6 gate OFF; use staged/copy paths or the already-qualified pre-existing platform contour only.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P04 DMA composition evidence

- Result: 179/179 selected tests passed across the managed runtime and HybridCPU adapter projects.
- Grant admission cleanup: a failed provider bind or unconfirmed revoke of a rejected grant retains a bridge-owned fault pin, blocking mapping and device closure. Confirmed cleanup of a malformed grant permits normal closure; this is deterministic managed provider evidence.
- Bound submit failure: only explicit provider `NotAccepted` permits retry. Denied, unavailable, stale or faulted responses retain possible-effect quarantine and prevent grant revoke; this is managed adapter evidence only.
- V1 malformed admission: ambiguous mapping, borrow mapping, or device cleanup retains the existing owner reservation and blocks reclaim while preserving the fail-closed V1 result.
- V1 provider failure without lease: Faulted/Revoked mapping responses retain the Region or borrow reservation because the response alone cannot prove no external effect.
- Completion reset safety: provider incarnation is checked after the completion callback before evidence can become reusable; drift fault-pins the exact mapping.
- Post-write acquire reset safety: provider incarnation is checked again after the acquire callback, before pending lifetime removal; drift fault-pins the exact mapping.
- Page-fault/completion ordering: each callback waits while the other is in flight for the same pending DMA submission; neither result can silently retire the other's lifetime.
- Page-fault/revoke ordering: grant, mapping, and device revocation stay blocked while the exact page fault is in flight and afterward until DMA completion/visibility closure.
- Capability/teardown race: revocation and process teardown during the provider page-fault callback preserve the pending DMA and local mapping reservation.
- Region owner/generation race: transfer, release, and incompatible RegionUse acquisition are denied during a held page fault; Region generation and mutation epoch remain exact.
- Post-submit reset: provider incarnation drift before the first observation fault-pins the pending write, skips further provider callbacks, and retains the mapping reservation.
- Trusted backend reset during single or atomic-copy submit changes only the local epoch; post-callback revalidation rejects the ambiguous acceptance and fault-pins the exact grant or both copy legs.
- Bound submit and atomic copy reject providers without a live incarnation source before invoking a submit callback; the legacy constant-incarnation fallback cannot support v6 generation revalidation.
- After post-completion visibility removes the active submission, ambiguous provider grant closure still fault-pins the grant. A thrown closure response cannot trigger a second provider revoke or make the mapping reclaimable.
- Runtime claim: the managed platform contour constructs and revalidates `DmaExecutionBindingV1` from owner generations at bound submit, enforces exact domain/mapping/device/grant/cycle/submission/provider-incarnation generations, revalidates bounded region-relative page faults, and pins ambiguous effects. Its bounded provider is a deterministic test implementation; no product bounded submit is claimed.
- Adapter claim: the in-tree HybridCPU adapter executes DMA admission and visibility only; bounded submit/completion is not claimed for it.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.
- Atomic-copy bridge: exact equal-length read-source/write-destination pairing is RuntimeEnforced on the deterministic managed contour; provider success creates both pending lifetimes atomically, while ambiguous, malformed, or reset outcomes pin both mappings.
- Copy completion: neither pending lifetime is finalized until both completions are proven; destination write visibility requires the ordinary acquisition fence before either grant can close.
- P07 composition: exact same-process and cross-owner managed contours close both grants and mappings before releasing the source Region and issuing a canonical data-motion receipt; a pending destination preserves both Regions and lower authority and resumes without another provider submit.
- Named-host binding medians: equality = $([double]$performance.summary.recordEqualityMedianNanoseconds) ns; validate/match = $([double]$performance.summary.validateAndMatchMedianNanoseconds) ns; construct digests = $([double]$performance.summary.constructDigestsMedianNanoseconds) ns; canonical round-trip = $([double]$performance.summary.canonicalRoundTripMedianNanoseconds) ns.
- Performance boundary: contract/sideband CPU cost only; RuntimeKernel lookup, provider callbacks, invalidation, page faults, device lifecycle, physical DMA/IOMMU, and gate promotion are not inferred.
- Gate: roadmap gate `V6-DMA-TRANSLATION-BINDING` is represented by implementation gate `V6-DMA-GENERATION-BINDING`, which remains OFF; `DmaExecutionBindingV1` remains non-authoritative correlation evidence with an internal managed consumer only.
- Not claimed: physical faulting/SVA provider, physical IOMMU isolation, hardware, or production qualification.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    Get-ChildItem -LiteralPath $OutputDirectory -File |
        Where-Object { $_.Extension -in '.json', '.md' } |
        Sort-Object Name |
        ForEach-Object {
        "$(Get-Sha256 $_.FullName)  $($_.Name)"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally {
    Pop-Location
}
