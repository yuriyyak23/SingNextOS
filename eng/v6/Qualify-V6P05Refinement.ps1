[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p05-refinement')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$filter = 'FullyQualifiedName~SemanticRefinementV1Tests|FullyQualifiedName~SemanticExtensionContractsV1Tests|FullyQualifiedName~MemorySemanticsV1Tests|FullyQualifiedName~TemporalSemanticsV1Tests|FullyQualifiedName~DmaExecutionBindingV1Tests|FullyQualifiedName~FailureDurabilitySemanticsV1Tests|FullyQualifiedName~SemanticTraceContractsV1Tests|FullyQualifiedName~SemanticAdmissionSentryTests|FullyQualifiedName~V6MemoryRuntimeEnforcementTests|FullyQualifiedName~Phase16SemanticTraceEquivalenceTests|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundSubmitEmitsNonAuthoritativeTraceAfterOwnerCommit|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundSubmitTraceSinkFailureCannotChangeCommittedEffect|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaCompletionTrace|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaResetTrace|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaTraceSinkMayReenterAfterOwnerLock|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaVisibilityTrace|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaResetDuringAcquireTrace|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaTraceRetainsOrderedEventsAcrossReentrantRemoval|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff|FullyQualifiedName=SingPlus.Tests.Architecture.RepositoryArchitecturePolicyTests.EveryProjectIsClassifiedAndEveryProjectReferenceIsAllowed|FullyQualifiedName=SingPlus.Tests.Architecture.RepositoryArchitecturePolicyTests.ExecutableAdapterMayConsumeOnlyExactlyPinnedExternalFacadePackages'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.PublishedOperationCannotReleaseRegionWhileResourceSettlementIsInFlight'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.ProviderLossWithQuarantinedResourceBindingCannotReleaseRegion'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaMalformedPageFaultTrace|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaPageFaultResetTrace'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaBackendResetTrace'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaRepeatedBackendResetPreservesEveryEpochAfterQuarantine'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaProviderDriftAfterQuarantineRetainsGenerationObservation'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaBackendResetDoesNotInventUnobservedProviderGeneration'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaBackendResetPreservesLastObservedProviderGeneration'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundDmaPageFaultBackendResetDoesNotObserveLaterProviderDrift'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6AmbiguousSubmitTraceRetainsPossibleEffectAndQuarantine|FullyQualifiedName~PlatformDmaSubmissionTests.V6DeniedSubmitQuarantinesPossibleEffectTrace|FullyQualifiedName~PlatformDmaSubmissionTests.V6BoundTransportUnavailableRetainsPossibleWriteAndBlocksRevoke'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6AmbiguousSubmitTraceSinkReentersAfterOwnerLockAndCannotReleaseGrant|FullyQualifiedName~PlatformDmaSubmissionTests.V6AmbiguousSubmitTraceSinkFailureCannotReleaseGrant'
$filter += '|FullyQualifiedName~PlatformDmaSubmissionTests.V6LostProviderIncarnationQuarantinesWithoutInventingGenerationDigest'
$filter += '|FullyQualifiedName~SessionLifecycleTraceContractsV1Tests|FullyQualifiedName=SingPlus.Tests.Runtime.ServiceDiscoverySessionTests.ExplicitCloseAndExpirationHaveTheSameObservedOwnerStateProjection'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.ServiceDiscoverySessionTests.ConcurrentCloseAndProcessTeardownRecordOneDrainingTransition'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.FailedAcceptedInlineSettlementDoesNotAssertEffectContainment|FullyQualifiedName=SingPlus.Tests.Runtime.EndpointSessionCancellationTests.AcceptedCancellationAndSessionCloseRetainPossibleEffectInEitherOrder'
$filter += '|FullyQualifiedName~EndpointSessionCancellationTests.FailedQueuedSettlementCallbackRetainsPossibleEffectWithoutServiceAcceptance'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/SemanticRefinementContracts.cs',
    'contracts/SingPlus.Contracts/SemanticExtensionContracts.cs',
    'contracts/SingPlus.Contracts/SemanticTraceContracts.cs',
    'contracts/SingPlus.Contracts/SessionLifecycleTraceContractsV1.cs',
    'contracts/SingPlus.Contracts/MemorySemanticsV1.cs',
    'contracts/SingPlus.Contracts/TemporalSemanticsV1.cs',
    'docs/SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-002-RESOURCE-RESERVATION-AND-UPPER-BOUND.md',
    'contracts/SingPlus.Contracts/DmaExecutionBindingV1.cs',
    'contracts/SingPlus.Contracts/FailureDurabilitySemanticsV1.cs',
    'contracts/SingPlus.Contracts/RasFailureContracts.cs',
    'contracts/SingPlus.Contracts/PersistenceContracts.cs',
    'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceProjection.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceStream.cs',
    'src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs',
    'src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs',
    'src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs',
    'src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6MemorySemanticBinding.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6SharedOperationSessionProvider.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6PlatformDmaSemanticSubmission.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaTrace.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaSubmission.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPostCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPageFault.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BackendEpoch.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPostCompletion.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPageFault.cs',
    'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBackendReset.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj',
    'tools/HybridCpu_ExecutableAdapter/Adapter/HybridCpuExecutableChildAdapter.cs',
    'tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj',
    'tools/HybridCpu_ExecutableAdapter.Tests/SemanticTraceInstrumentationTests.cs',
    'tests/SingPlus.Tests/Contracts/SemanticRefinementV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/SemanticExtensionContractsV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/SemanticTraceContractsV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/SessionLifecycleTraceContractsV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/FailureDurabilitySemanticsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/SemanticAdmissionSentryTests.cs',
    'tests/SingPlus.Tests/Runtime/V6MemoryRuntimeEnforcementTests.cs',
    'tests/SingPlus.Tests/Runtime/ServiceDiscoverySessionTests.cs',
    'tests/SingPlus.Tests/Runtime/EndpointSessionCancellationTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDmaSubmissionTests.cs',
    'tests/SingPlus.Tests/SipJobs/Phase16SemanticTraceEquivalenceTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P05RefinementPerformanceQualification.cs',
    'eng/v6/Qualify-V6P05Refinement.ps1'
)

Push-Location $RepositoryRoot
try {
    $coreOutput = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P05 refinement tests failed.`n$coreOutput" }
    $adapterOutput = & dotnet test 'tools\HybridCpu_ExecutableAdapter.Tests\HybridCpu_ExecutableAdapter.Tests.csproj' --no-restore --filter 'FullyQualifiedName~SemanticTraceInstrumentationTests' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P05 HybridCPU trace instrumentation tests failed.`n$adapterOutput" }
    $matches = @(@($coreOutput, $adapterOutput) | ForEach-Object {
        $value = [regex]::Match($_, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
        if (-not $value.Success) { throw 'A P05 test runner summary could not be parsed.' }
        $value
    })
    $failed = ($matches | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    $passed = ($matches | ForEach-Object { [int]$_.Groups[2].Value } | Measure-Object -Sum).Sum
    $skipped = ($matches | ForEach-Object { [int]$_.Groups[3].Value } | Measure-Object -Sum).Sum
    $total = ($matches | ForEach-Object { [int]$_.Groups[4].Value } | Measure-Object -Sum).Sum
    if ($failed -ne 0 -or $passed -ne 170 -or $skipped -ne 0 -or $total -ne 170) {
        throw "Unexpected P05 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p05-refinement-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P05 refinement/trace performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p05-refinement-performance/1' -or
        $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
        throw 'The P05 refinement/trace performance artifact is malformed.'
    }
    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $payload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'; generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('P05', 'P05-managed-performance'); slice = 'executable-refinement-trace-and-cross-cutting-semantics'
        contour = 'managed-runtime/staged-exclusive-memory/exact-dma-correlation/temporal/failure/durability/session-owner-state'
        sliceStatus = 'managed-cancel-and-ambiguous-publication-trace-qualified; cross-project-generation-trace-partial'
        requirementClassification = [ordered]@{
            VerifiedExisting = @('external-operation owner records exact ambiguous-publication and reconciliation transitions', 'EndpointSessionRegistry records Active, Draining and Closed state changes under its owner lock in a bounded non-authoritative journal')
            Partial = @('selected staged managed trace projects cancellation request, ambiguity, exact closure, settlement, and release', 'v6 bound DMA emits committed submit/effect, completion and post-completion visibility or reset-quarantine; final grant closure is not yet emitted', 'checker requires explicit marker for changed generation digest but no complete live per-transition vector exists across projects')
            Missing = @('fresh mutable provider-generation vector on each owner transition', 'direct-coherent and physical-provider trace qualification', 'atomic cross-owner session/channel/invocation transition stream and exact external effect closure source')
            Contradicted = @('failed publication callback implies no external effect', 'post-submit cancellation request implies effect containment', 'GenerationChanged marker with unchanged digest is valid evidence')
            ExternalBlocked = @('physical provider trace producer and qualification environment')
            FutureGated = @('cross-project complete generation trace', 'physical or production refinement claim')
        }
        requirementIds = @('P05-TRACE-CANONICAL-DIGEST-01', 'P05-TRACE-GENERATION-MARKER-01', 'P05-TRACE-PRIVATE-DRIFT-01', 'P05-TRACE-DMA-SUBMIT-PREFIX-01', 'P05-TRACE-DMA-COMPLETION-RESET-01', 'P05-TRACE-DMA-VISIBILITY-ORDER-01', 'P05-TRACE-DRIFT-QUARANTINE-01', 'P05-TRACE-PUBLICATION-PROVENANCE-01', 'P05-TRACE-POST-SETTLEMENT-DRIFT-01', 'P05-TRACE-AMBIGUOUS-PUBLICATION-01', 'P05-TRACE-CANCEL-REQUEST-01', 'QV1-EFFECT-CLOSURE-TRACE-01')
        testIds = @('SemanticTraceContractsV1Tests.TraceAndSourceTupleDigestsRejectNonCanonicalCase', 'SemanticTraceContractsV1Tests.GenerationMarkerRequiresChangedDigestAndEveryDigestChangeRequiresMarker', 'SemanticTraceContractsV1Tests.ProviderPrivateTailCannotConcealGenerationDrift', 'SemanticTraceContractsV1Tests.GenerationDriftAfterPossibleEffectRequiresQuarantineBeforeAnySuccessProjection', 'SemanticTraceContractsV1Tests.PublishedEffectCannotLaterBeClosedAsNeverPublished', 'SemanticTraceContractsV1Tests.GenerationDriftAfterSettlementCannotAuthorizeReleaseProjection', 'SemanticTraceContractsV1Tests.AmbiguousPublicationRequiresExactEffectClosureBeforeReleaseProjection', 'SemanticTraceContractsV1Tests.CancellationRequestPreservesPossibleEffectAndRequiresTerminalEvidence', 'PlatformDmaSubmissionTests.V6BoundSubmitEmitsNonAuthoritativeTraceAfterOwnerCommit', 'PlatformDmaSubmissionTests.V6BoundSubmitTraceSinkFailureCannotChangeCommittedEffect', 'PlatformDmaSubmissionTests.V6BoundDmaCompletionTraceFollowsCommittedOwnerOrder', 'PlatformDmaSubmissionTests.V6BoundDmaResetTraceMarksNewGenerationAndQuarantine', 'PlatformDmaSubmissionTests.V6BoundDmaTraceSinkMayReenterAfterOwnerLock', 'PlatformDmaSubmissionTests.V6BoundDmaCompletionTraceSinkFailureCannotChangeOwnerResult', 'PlatformDmaSubmissionTests.V6BoundDmaVisibilityTraceFollowsExactPostCompletion', 'PlatformDmaSubmissionTests.V6BoundDmaResetDuringAcquireTraceQuarantinesWithoutVisibility', 'PlatformDmaSubmissionTests.V6BoundDmaTraceRetainsOrderedEventsAcrossReentrantRemoval', 'V6MemoryRuntimeEnforcementTests.AmbiguousPublicationAndExactReconciliationRemainInDirectSemanticTrace', 'V6MemoryRuntimeEnforcementTests.PostSubmitCancelRequestRemainsPossibleEffectInDirectTrace')
        changedFiles = @('src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaTrace.cs', 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaSubmission.cs', 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCompletion.cs', 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPostCompletion.cs', 'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaCompletion.cs', 'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPostCompletion.cs', 'src/Runtime/SingPlus.Runtime/V6/V6PlatformDmaSemanticSubmission.cs', 'tests/SingPlus.Tests/Platform/PlatformDmaSubmissionTests.cs', 'eng/v6/Qualify-V6P05Refinement.ps1', 'eng/v6/Qualify-V6P04Dma.ps1')
        publicApiDelta = 'Additive SessionLifecycleTraceEventV1 schema, checker and differential API; existing V1 operation trace schema and enum values remain unchanged.'
        sourceAndDependencyTuple = [ordered]@{ singNextHead = (& git rev-parse HEAD).Trim(); sourceSetSha256 = $sourceSetDigest; sdk = (& dotnet --version).Trim(); targetFramework = 'net11.0' }
        commandsRun = @(
            "dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter $filter",
            'dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter FullyQualifiedName~SemanticTraceInstrumentationTests',
            'dotnet run --project tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p05-refinement-performance --output artifacts/v6/p05-refinement/performance.json'
        )
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        featureGate = [ordered]@{ roadmapGate = 'V6-FORMAL-REFINEMENT'; implementationGate = 'V6-FORMAL-REFINEMENT'; state = 'OFF' }
        coverage = [ordered]@{
            core = @('mandatory/advisory semantics', 'dimension partial-order laws', 'unknown clauses fail closed', 'canonical bounded payloads', 'lowercase-only SHA-256 trace and source-tuple digests')
            dimensions = @('base V1 classes', 'staged/exclusive memory', 'temporal upper-bound direction', 'exact DMA generation correlation', 'failure containment/recovery', 'durable publication/recovery integrity/named domain assurance')
            trace = @('lifecycle validator', 'generation marker requires changed digest and all digest changes require a marker', 'generation drift after possible effect requires quarantine before any success projection', 'published provenance survives generation drift and quarantine and forbids no-publication closure', 'generation drift after settlement forbids release based on stale closure', 'provider-private tail or transient digest change cannot be erased', 'direct v6 bound DMA committed submit/effect/completion/visibility or reset-quarantine projection, reentrant ordering and sink failure isolation', 'selected staged-owner transition projection', 'tuple-bound reproducible differential counterexample', 'post-submit cancellation request with possible effect retained through completion or quarantine', 'ambiguous publication quarantine and exact no-publication closure from owner transitions', 'settlement and release only after exact closure', 'direct managed QV1 owner-projected lifecycle stream', 'direct HybridCPU executable-start submit/effect/terminal instrumentation', 'HybridCPU executable-start quarantine projection differential against reference lifecycle', 'bounded session owner-state journal and separate additive state-trace checker; explicit close and expiry have equal allowed state projection while preserving cause')
            runtime = @('four-gate sentry', 'final owner revalidation', 'single irreversible submit winner', 'V1/v6 staged projection equivalence')
            performance = @('pure mandatory refinement decision', 'seven-event lifecycle validation',
                'equivalent allowed-projection differential comparison', 'provider-private erasure plus projection validation and comparison',
                'rotating Release JIT named-host rounds', 'median/p95/p99 elapsed time', 'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            refinementDecisionMedianNanoseconds = [double]$performance.summary.refinementDecisionMedianNanoseconds
            traceValidateMedianNanoseconds = [double]$performance.summary.traceValidateMedianNanoseconds
            traceDifferentialMedianNanoseconds = [double]$performance.summary.traceDifferentialMedianNanoseconds
            providerProjectValidateDifferentialMedianNanoseconds = [double]$performance.summary.providerProjectValidateDifferentialMedianNanoseconds
            supportsGatePromotion = $false
            interpretation = 'Executable checker and in-memory trace projection CPU overhead only; trace emission and physical provider instrumentation remain outside the measured boundary.'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll',
            'tools/HybridCpu_ExecutableAdapter.Tests/bin/Debug/net11.0/HybridCpu_ExecutableAdapter.Tests.dll',
            'tools/HybridCpu_ExecutableAdapter/packages.lock.json'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary/package evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        maximumSupportedClaim = [ordered]@{
            executableRefinementChecker = 'RuntimeEnforced on named managed contours'
            semanticTraceChecker = 'RuntimeEnforced for schema/lifecycle/differential comparison, the managed QV1 owner-projected stream, and named HybridCPU executable-start instrumentation'
            sessionOwnerStateTrace = 'RuntimeEnforced for the managed EndpointSessionRegistry state transitions; StaticAdmission for offline session-trace validation and differential state comparison; no channel-closure or external-effect claim'
            channelClosureSnapshot = 'StaticAdmission for quiescent exact-generation ChannelRegistry closure observation correlated offline with session owner state; no atomic cross-owner snapshot or reclaim authority'
            invocationConsequenceSnapshot = 'StaticAdmission for quiescent exact-generation EndpointSessionInvocationRegistry possible-effect observation correlated offline with session and channel owner state; no closure or reclaim authority'
            firstMemoryVertical = 'RuntimeEnforced in the internal managed test contour'
            dmaDimension = 'StaticAdmission exact correlation only'
            failureDurabilityDimensions = 'RuntimeEnforced contract refinement on named managed contours; provider evidence remains ModelOnly or FutureGated'
            refinementTracePerformance = 'ModelOnly checker/projection overhead only; no physical instrumentation or gate-promotion claim'
            unimplementedDimensions = 'FutureGated'
            formalVerification = 'FutureGated'
            hardware = 'FutureGated'; production = 'FutureGated'
        }
        remainingBlockers = @(
            'V6-FORMAL-REFINEMENT remains OFF and v6 consumers are internal.',
            'No direct provider trace instrumentation binds failure and durability sidecars to physical observations.',
            'The selected owner snapshot does not carry a fresh mutable provider-generation vector at each transition; generation changes cannot be claimed as a complete cross-project trace.',
            'Direct-coherent publication and physical provider transitions are outside this staged-owner projection qualification.',
            'No external finite-model run is tuple-bound into this artifact.',
            'Passing the checker does not promote hardware or production claims.',
            'DMA active submission trace ends at post-completion visibility. Grant revoke owns later closure, but the generic trace validator requires Published and Settled before Released; DMA owner has no such publication/settlement transitions. A Released projection would invent evidence, so final grant closure remains outside the trace scope.'
            'The additive session trace covers EndpointSessionRegistry state changes only. Exact-generation ChannelRegistry and EndpointSessionInvocationRegistry snapshots can be correlated after quiescence; actual channel closure and invocation consequence are not yet events in one atomic cross-owner stream. An accepted failed-inline effect remains possible after both session and channel close. Exact provider closure is absent and cannot be inferred from Closed.'
            'The bounded managed session owner journal is always on for this contour; its per-transition overhead is not separately characterized, so no session instrumentation performance claim is made.'
        )
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        rollbackFallback = 'Keep the gate OFF and fresh-admit on the existing staged reference path.'
        isaImpact = 'NONE'
    }
    $artifact.requirementIds += 'P05-TRACE-DMA-PAGE-FAULT-QUARANTINE-01'
    $artifact.testIds += @('PlatformDmaSubmissionTests.V6BoundDmaMalformedPageFaultTraceQuarantinesExactSubmission',
        'PlatformDmaSubmissionTests.V6BoundDmaPageFaultResetTraceMarksGenerationAndQuarantine')
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPageFault.cs',
        'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDmaPageFault.cs')
    $artifact.coverage.trace += 'direct v6 bound DMA page-fault malformed evidence and provider reset quarantine'
    $artifact.requirementIds += 'P05-TRACE-DMA-BACKEND-RESET-01'
    $artifact.testIds += 'PlatformDmaSubmissionTests.V6BoundDmaBackendResetTraceMarksLocalEpochAndQuarantine'
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.BackendEpoch.cs',
        'src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBackendReset.cs')
    $artifact.coverage.trace += 'trusted backend reset changes local epoch digest and quarantines active DMA trace'
    $artifact.requirementIds += 'P05-TRACE-PRIVATE-SEMANTIC-ERASURE-01'
    $artifact.testIds += @('SemanticTraceContractsV1Tests.ProviderPrivateLabelCannotEraseSemanticTransition',
        'SemanticTraceContractsV1Tests.ProviderPrivateLabelCannotHideCrossOperationOrReorderedEvidence',
        'SemanticTraceContractsV1Tests.ProviderPrivatePrefixCannotConcealGenerationDrift',
        'SemanticAdmissionSentryTests.ProviderAdmissionRevokedDuringRuntimeLegalityFailsBeforeSubmit')
    $artifact.changedFiles += @('contracts/SingPlus.Contracts/SemanticTraceContracts.cs',
        'tests/SingPlus.Tests/Contracts/SemanticTraceContractsV1Tests.cs')
    $artifact.coverage.trace += 'provider-private label cannot erase semantic lifecycle kinds, cross-operation events, reordered events, malformed metadata, or generation drift before the first semantic event'
    $artifact.requirementIds += 'P05-DIFFERENTIAL-INVALID-COUNTEREXAMPLE-01'
    $artifact.testIds += 'SemanticTraceContractsV1Tests.InvalidCandidateDifferenceIdIncludesTheValidationCounterexample'
    $artifact.coverage.trace += 'differential invalid-candidate ID incorporates the underlying validation counterexample while preserving source-tuple binding and deterministic replay'
    $artifact.requirementIds += 'P05-VALIDATION-COUNTEREXAMPLE-IDENTITY-01'
    $artifact.testIds += 'SemanticTraceContractsV1Tests.MixedOperationCounterexampleDistinguishesTheOffendingIdentity'
    $artifact.coverage.trace += 'validation counterexample ID binds the actual validated offending event, so distinct mixed-operation identities and their tuple-bound differential results do not collide at the same error index'
    $artifact.requirementIds += 'P05-SESSION-OWNER-STATE-TRACE-01'
    $artifact.testIds += @('SessionLifecycleTraceContractsV1Tests.ValidLifecycleRejectsMissingOrReorderedTransitions',
        'SessionLifecycleTraceContractsV1Tests.DifferentialCounterexampleIsTupleBoundAndCannotAuthorizeClosure',
        'ServiceDiscoverySessionTests.ExplicitCloseAndExpirationHaveTheSameObservedOwnerStateProjection',
        'ServiceDiscoverySessionTests.ConcurrentCloseAndProcessTeardownRecordOneDrainingTransition')
    $artifact.changedFiles += @('contracts/SingPlus.Contracts/SessionLifecycleTraceContractsV1.cs',
        'src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs',
        'src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs',
        'tests/SingPlus.Tests/Contracts/SessionLifecycleTraceContractsV1Tests.cs',
        'tests/SingPlus.Tests/Runtime/ServiceDiscoverySessionTests.cs')
    $artifact.coverage.trace += 'quiescent exact-generation channel-owner snapshot distinguishes Draining/open during explicit close or expiration from Draining/closed after process teardown; stale endpoint generation cannot be laundered as closure'
    $artifact.requirementIds += 'P05-SESSION-INVOCATION-CONSEQUENCE-OBSERVATION-01'
    $artifact.testIds += @('EndpointSessionCancellationTests.FailedAcceptedInlineSettlementDoesNotAssertEffectContainment',
        'EndpointSessionCancellationTests.AcceptedCancellationAndSessionCloseRetainPossibleEffectInEitherOrder')
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs',
        'src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs',
        'tests/SingPlus.Tests/Runtime/EndpointSessionCancellationTests.cs')
    $artifact.coverage.trace += 'exact invocation-generation owner snapshot retains possible effect after managed session and channel close, including failed accepted inline and cancellation/close race; observation does not authorize closure or reclaim'
    $artifact.requirementIds += 'P05-SESSION-FAILED-SETTLEMENT-EFFECT-PROJECTION-01'
    $artifact.testIds += 'EndpointSessionCancellationTests.FailedQueuedSettlementCallbackRetainsPossibleEffectWithoutServiceAcceptance'
    $artifact.coverage.trace += 'queued response callback failed result or exception after entry retains possible effect without service acceptance, and later successful publication does not erase the earlier ambiguity'
    $artifact.requirementIds += 'P05-TRACE-SUBMIT-EFFECT-ATOMIC-PREFIX-01'
    $artifact.testIds += 'SemanticTraceContractsV1Tests.GenerationDriftCannotSplitSubmitFromPossibleEffect'
    $artifact.testIds += 'SemanticTraceContractsV1Tests.QuarantineCannotSplitSubmitFromPossibleEffect'
    $artifact.coverage.trace += 'generation drift cannot split the Submit and EffectPossible events of one committed owner boundary'
    $artifact.coverage.trace += 'quarantine cannot replace EffectPossible after Submit; the possible effect must remain explicit before fault consequence projection'
    $artifact.requirementIds += 'P05-TRACE-REPEATED-BACKEND-EPOCH-01'
    $artifact.testIds += 'PlatformDmaSubmissionTests.V6BoundDmaRepeatedBackendResetPreservesEveryEpochAfterQuarantine'
    $artifact.changedFiles += 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaTrace.cs'
    $artifact.coverage.trace += 'each distinct local backend reset epoch remains visible after an effect is already quarantined'
    $artifact.requirementIds += 'P05-TRACE-POST-QUARANTINE-PROVIDER-DRIFT-01'
    $artifact.testIds += 'PlatformDmaSubmissionTests.V6BoundDmaProviderDriftAfterQuarantineRetainsGenerationObservation'
    $artifact.coverage.trace += 'successive provider incarnation changes observed after fault quarantine remain explicit without completing or releasing the effect'
    $artifact.requirementIds += 'P05-TRACE-CANONICAL-OBSERVED-GENERATIONS-01'
    $artifact.testIds += 'PlatformDmaSubmissionTests.V6BoundDmaBackendResetDoesNotInventUnobservedProviderGeneration'
    $artifact.coverage.trace += 'generation digest derives from the last observed provider incarnation and current local backend epoch, including repeated backend reset after unobserved provider drift; backend-stale rejection does not invent a later provider generation'
    $artifact.testIds += 'PlatformDmaSubmissionTests.V6BoundDmaBackendResetPreservesLastObservedProviderGeneration'
    $artifact.coverage.trace += 'backend reset composes its local epoch with a provider incarnation observed before reset, while ignoring later incarnation drift after backend identity became stale'
    $artifact.requirementIds += 'P05-TRACE-PAGE-FAULT-OBSERVED-GENERATION-01'
    $artifact.testIds += 'PlatformDmaSubmissionTests.V6BoundDmaPageFaultBackendResetDoesNotObserveLaterProviderDrift'
    $artifact.coverage.trace += 'page-fault owner revalidation after backend reset stops before provider incarnation read; the local reset epoch is projected without later unobserved provider drift'
    $artifact.requirementIds += 'P05-TRACE-AMBIGUOUS-DMA-SUBMIT-01'
    $artifact.testIds += @('PlatformDmaSubmissionTests.V6AmbiguousSubmitTraceRetainsPossibleEffectAndQuarantine',
        'PlatformDmaSubmissionTests.V6DeniedSubmitQuarantinesPossibleEffectTrace',
        'PlatformDmaSubmissionTests.V6BoundTransportUnavailableRetainsPossibleWriteAndBlocksRevoke',
        'PlatformDmaSubmissionTests.V6AmbiguousSubmitTraceSinkReentersAfterOwnerLockAndCannotReleaseGrant',
        'PlatformDmaSubmissionTests.V6AmbiguousSubmitTraceSinkFailureCannotReleaseGrant',
        'PlatformDmaSubmissionTests.V6LostProviderIncarnationQuarantinesWithoutInventingGenerationDigest')
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaSubmission.cs',
        'src/Runtime/SingPlus.Runtime/V6/V6PlatformDmaSemanticSubmission.cs')
    $artifact.coverage.trace += 'ambiguous bound DMA submit exposes possible effect and owner quarantine, including observed reset generation; zero provider incarnation retains last known digest without synthetic generation marker; definite denial emits no possible effect; sink reentry and failure do not release fault pin'
    $artifact.requirementIds += 'P05-TRACE-NO-PUBLICATION-CLOSURE-AFTER-PUBLISH-01'
    $artifact.testIds += 'V6MemoryRuntimeEnforcementTests.ProjectionRejectsNoPublicationClosureAfterPublishedEffect'
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceProjection.cs',
        'tests/SingPlus.Tests/Runtime/V6MemoryRuntimeEnforcementTests.cs')
    $artifact.coverage.trace += 'an already-published owner effect cannot be projected as effect-closed-without-publication after a later quarantine marker'
    $artifact.requirementIds += 'P05-TRACE-PREFIX-LIFECYCLE-VALIDATION-01'
    $artifact.testIds += 'V6MemoryRuntimeEnforcementTests.ProjectionRejectsVisibilityBeforeOwnerCompletion'
    $artifact.requirementIds += 'P05-TRACE-OWNER-STATE-CONTINUITY-01'
    $artifact.testIds += 'V6MemoryRuntimeEnforcementTests.ProjectionRejectsDiscontinuousOwnerStatesAndContradictorySnapshotState'
    $artifact.coverage.trace += 'owner transition Prepared origin, contiguous sequence, From/To chain and final snapshot state must match before a semantic prefix is accepted'
    $artifact.coverage.trace += 'nonempty owner projections are validated as complete semantic prefixes before delivery; a pre-submit empty observation remains registrable and grants no authority'
    $artifact.requirementIds += 'P05-TRACE-SINK-PREFIX-FAILURE-01'
    $artifact.requirementIds += 'P05-PUBLISHED-SETTLEMENT-RELEASE-ORDER-01'
    $artifact.testIds += 'VNextPhase07ExternalOperationResourceBindingTests.PublishedOperationCannotReleaseRegionWhileResourceSettlementIsInFlight'
    $artifact.testIds += 'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossWithQuarantinedResourceBindingCannotReleaseRegion'
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
        'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs')
    $artifact.coverage.trace += 'the published external-operation owner refuses Region release before exact resource settlement, during settlement, and after a failed settlement transition'
    $artifact.coverage.trace += 'provider loss with a quarantined resource binding remains a valid Submit/EffectPossible/Quarantined prefix; no Released event, overlapping Region acquire, loan, backing lease, platform mapping, transfer or reclaim is inferred'
    $artifact.coverage.trace += 'the owner-bound Region use cannot be released by the public use API or a different operation generation, preserving the quarantined owner prefix until exact owner closure'
    $artifact.testIds += @('V6MemoryRuntimeEnforcementTests.FailedDirectTraceDeliveryNeverEmitsAnUnobservedSuffix',
        'V6MemoryRuntimeEnforcementTests.ReentrantDirectTraceSinkDrainsNewOwnerTransitionAfterOriginalPrefix',
        'V6MemoryRuntimeEnforcementTests.ReentrantReleaseFromSettlementTraceDeliversFinalOwnerEvent',
        'V6MemoryRuntimeEnforcementTests.ConcurrentReleaseFromSettlementTraceDoesNotBlockOwnerOrLoseFinalEvent')
    $artifact.changedFiles += 'src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceStream.cs'
    $artifact.coverage.trace += 'false or throwing direct sink delivery permanently stops that registration before a later semantic event can form an unobserved suffix; reentrant and concurrent owner release from a sink callback drain the final event without waiting on an observation lock'
    $artifact.requirementIds += 'P05-REFINEMENT-SINGLE-OWNER-SUBMIT-CALLBACK-01'
    $artifact.testIds += @('SemanticAdmissionSentryTests.BindingAwareSubmitPassesOnlyCommittedSingNextOperationToProviderCallback',
        'SemanticAdmissionSentryTests.BindingAwareCallbackIsNotInvokedWhenIndependentLegalityDenies',
        'SemanticAdmissionSentryTests.BindingAwareProviderFailureRetainsPossibleEffectAndBudgetQuarantine',
        'V6MemoryRuntimeEnforcementTests.StagedMemoryBindingAwareSubmitUsesOneCommittedOperation')
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs',
        'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs',
        'src/Runtime/SingPlus.Runtime/V6/V6MemorySemanticBinding.cs',
        'tests/SingPlus.Tests/Runtime/SemanticAdmissionSentryTests.cs')
    $artifact.coverage.trace += 'the binding-aware internal submit callback receives the exact already-submitted SingNext operation only after independent gates and owner commit; provider failure retains possible effect and quarantine'
    $artifact.requirementIds += 'P05-RESOURCE-ASSURANCE-INCOMPARABILITY-01'
    $artifact.testIds += @('SemanticRefinementV1Tests.GuaranteedReservationDoesNotRefineEnforcedUpperBound',
        'TemporalSemanticsV1Tests.SmallerUpperBoundRefinesLargerButReservationUsesCapacityDirection')
    $artifact.changedFiles += @('contracts/SingPlus.Contracts/SemanticRefinementContracts.cs',
        'contracts/SingPlus.Contracts/TemporalSemanticsV1.cs',
        'tests/SingPlus.Tests/Contracts/SemanticRefinementV1Tests.cs',
        'tests/SingPlus.Tests/Contracts/TemporalSemanticsV1Tests.cs',
        'docs/SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-002-RESOURCE-RESERVATION-AND-UPPER-BOUND.md')
    $artifact.coverage.dimensions += 'reservation capacity and enforced consumption upper bounds are incomparable; temporal amounts use opposite directions only within their own assurance branch'
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'; $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P05 executable refinement evidence

- Result: $passed/$total selected partial-order, sidecar binding, sentry, trace, and differential tests passed.
- Claim: refinement and trace checks are RuntimeEnforced only on the named managed contours; DMA correlation is StaticAdmission.
- First vertical: reference V1 and internal v6 staged/exclusive paths have the same allowed owner-event projection.
- Cross-cutting dimensions: failure containment/recovery and durability/domain/integrity are canonical, fail-closed, generation-bound, and non-authoritative.
- Resource assurance (ADR-002): a guaranteed reservation cannot refine an enforced upper bound; temporal capacity and consumption amounts are compared only within compatible assurance branches.
- Counterexamples are deterministic and bound to explicit source/dependency tuple digests.
- Generation drift after a possible effect requires quarantine before completion, visibility, settlement, release, or publication can be accepted by the checker.
- A generation marker cannot split Submit from EffectPossible at the same committed owner boundary; the checker rejects that trace prefix.
- A published effect remains published through drift and quarantine; no-publication closure cannot be inferred afterward.
- The direct external-operation owner projection rejects no-publication closure after an already-published effect, including a later settlement quarantine.
- Every nonempty direct owner projection validates its semantic lifecycle prefix before delivery; empty pre-submit observations remain registrable.
- A direct sink that rejects or throws on an event receives no later suffix; reentrant and concurrent owner release from a sink callback drain the final event without an observation-lock wait or change to owner decisions.
- The binding-aware internal submit callback receives one exact SingNext-owned operation after refinement and independent legality; denied gates prevent callback, while ambiguous provider failure retains quarantine.
- Generation drift after settlement cannot reuse the old closure to project release.
- Provider-private tail or transient generation drift is rejected by projection before erasure.
- Provider-private labels cannot erase semantic lifecycle transitions, cross-operation events, reordered events, or malformed metadata.
- A second backend reset after DMA quarantine retains a distinct GenerationChanged observation without releasing the pinned effect.
- Successive provider incarnation changes after DMA quarantine retain ordered GenerationChanged observations without completion or release.
- DMA generation digest is canonical for the last observed provider incarnation and local backend epoch, including repeated backend reset after unobserved provider drift; backend-stale denial does not invent an unobserved provider generation.
- Ambiguous bound DMA submit emits possible effect and quarantine outside owner locks; observed reset generation is explicit, while definite provider denial emits no possible-effect trace.
- Direct v6 bound DMA emits committed submit/effect, completion, and post-completion visibility or reset-quarantine after owner locks. Malformed page-fault evidence, reset during page-fault resolution, and trusted backend reset emit quarantine; the latter also changes the local epoch digest. The observer handle retains ordered events across pending-record removal; sink failure cannot alter DMA decisions. Final grant closure remains outside this trace scope.
- Grant revoke after visibility has a distinct owner transition, while the generic validator requires Published and Settled before Released. No DMA owner transition proves those steps, so final grant closure is still excluded from the trace projection.
- Session expiration, inline pin draining, and channel closure currently have no P05 trace projection or differential consumer; managed lifecycle enforcement does not establish a refinement claim for those events.
- Gate: `V6-FORMAL-REFINEMENT` remains OFF.
- HybridCPU contour: executable Start emits non-authoritative `SemanticTraceEventV1` submit/effect/terminal observations; observation failure cannot alter execution outcome.
- Named-host medians: refinement decision = $([double]$performance.summary.refinementDecisionMedianNanoseconds) ns; trace validation = $([double]$performance.summary.traceValidateMedianNanoseconds) ns; differential comparison = $([double]$performance.summary.traceDifferentialMedianNanoseconds) ns; provider projection + validation + comparison = $([double]$performance.summary.providerProjectValidateDifferentialMedianNanoseconds) ns.
- Performance boundary: executable checker and in-memory projection CPU cost only; emission, sink dispatch, provider callbacks, physical instrumentation, and gate promotion are not inferred.
- Not claimed: full formal verification, physical provider trace binding, physical persistence, hardware, or production qualification.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    Get-ChildItem -LiteralPath $OutputDirectory -File |
        Where-Object { $_.Extension -in '.json', '.md' } |
        Sort-Object Name |
        ForEach-Object { "$(Get-Sha256 $_.FullName)  $($_.Name)" } |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
