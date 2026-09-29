[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\qv1-managed-staged-operation')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$filter = 'FullyQualifiedName~QualificationVerticalContractsV1Tests|FullyQualifiedName~V6MemoryRuntimeEnforcementTests|FullyQualifiedName~SemanticAdmissionSentryTests|FullyQualifiedName~SemanticExecutionBindingV1Tests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~VNextPhase08ComputePlanIndependentGatesTests|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests|FullyQualifiedName~DmaExecutionBindingV1Tests|FullyQualifiedName~HybridCpuExternalOperationProviderTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/QualificationVerticalContracts.cs',
    'contracts/SingPlus.Contracts/OperationObligations.cs',
    'contracts/SingPlus.Contracts/SemanticExtensionContracts.cs',
    'contracts/SingPlus.Contracts/SemanticTraceContracts.cs',
    'contracts/SingPlus.Contracts/MemorySemanticsV1.cs',
    'contracts/SingPlus.Contracts/DmaExecutionBindingV1.cs',
    'contracts/SingPlus.Contracts/ComputePlanning.cs',
    'src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs',
    'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs',
    'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6MemorySemanticBinding.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6SharedOperationSessionProvider.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceProjection.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceStream.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs',
    'tests/SingPlus.Tests/Contracts/QualificationVerticalContractsV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/DmaExecutionBindingV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6MemoryRuntimeEnforcementTests.cs',
    'tests/SingPlus.Tests/Runtime/SemanticAdmissionSentryTests.cs',
    'tests/SingPlus.Tests/Runtime/SemanticExecutionBindingV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs',
    'tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs',
    'tests/SingPlus.Tests/SingPlus.Tests.csproj',
    'tests/SingPlus.Tests/Runtime/VNextPhase08ComputePlanIndependentGatesTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj',
    'tools/HybridCpu_ExecutableAdapter/README.md',
    'tools/HybridCpu_ExecutableAdapter/packages.lock.json',
    'tools/HybridCpu_ExecutableAdapter.Tests/AdapterBoundaryTests.cs',
    'tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj',
    'eng/v6/Qualify-V6Qv1.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "QV1 tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The QV1 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value; $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value; $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 131 -or $skipped -ne 0 -or $total -ne 131) {
        throw "Unexpected QV1 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    $packageTestFilter = 'FullyQualifiedName~AdapterBoundaryTests.VersionedExternalRuntimePackageExposesAdapterSessionWithoutGrantingAuthority'
    $packageTestOutput = & dotnet test 'tools\HybridCpu_ExecutableAdapter.Tests\HybridCpu_ExecutableAdapter.Tests.csproj' --no-restore --filter $packageTestFilter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $packageTestOutput -notmatch 'Failed:\s+0, Passed:\s+1, Skipped:\s+0, Total:\s+1') {
        throw "QV1 versioned package surface test failed.`n$packageTestOutput"
    }
    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $payload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
    $externalRuntimeAssembly = Join-Path $env:USERPROFILE '.nuget/packages/hybridcpu.externalruntime/1.6.0/lib/net11.0/HybridCPU_ExternalRuntime.dll'
    if (-not (Test-Path -LiteralPath $externalRuntimeAssembly -PathType Leaf)) {
        throw 'Pinned HybridCPU.ExternalRuntime/1.6.0 assembly is absent; QV1 dependency tuple cannot be verified.'
    }
    $externalRuntimePackage = Join-Path $RepositoryRoot '.packages/HybridCPU.ExternalRuntime.1.6.0.nupkg'
    if (-not (Test-Path -LiteralPath $externalRuntimePackage -PathType Leaf)) {
        throw 'Pinned HybridCPU.ExternalRuntime/1.6.0 package is absent.'
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('QV1-A', 'QV1-B', 'QV1-C')
        slice = 'first-qualification-vertical-managed-staged-operation'
        contour = 'single-host/managed/hybridcpu-contract-1.14/staged-exclusive/no-dma'
        sliceStatus = 'managed-staged-owner-projection-qualified; packaged-provider-session-stage-contour-executable; full-semantic-vertical-partial'
        requirementClassification = [ordered]@{
            VerifiedExisting = @('managed staged owner chain and trace projection',
                'versioned ExternalRuntime/1.6.0 package contains ExternalOperationAdapterSession',
                'packaged session drives the existing SingNext provider lifecycle on the exact local tuple',
                'versioned session reserves admission correlation before provider callback and retains uncertain admission in-session',
                'provider reconfiguration retains an observed A-B-A drift during an in-flight ambiguous publication',
                'binding-aware internal submit passes one SingNext-owned operation after the owner commit')
            Partial = @('packaged session observes the full managed owner lifecycle and exact pre-submit cancellation through one shared-operation seam; post-submit cancel remains ambiguous and provider execution and physical visibility evidence remain unqualified')
            Missing = @('independently executing HybridCPU legality service and a qualified provider for the selected shared-operation lifecycle')
            Contradicted = @('packaged ExternalRuntime/1.3.0 provides the adapter session')
            ExternalBlocked = @('physical provider qualification')
            FutureGated = @('end-to-end executable QV1 and production deployment')
        }
        requirementIds = @('QV1-EFFECT-CLOSURE-TRACE-01', 'QV1-CANCEL-OBSERVATION-01', 'QV1-SHARED-SESSION-CANCEL-BOUNDARY-01', 'QV1-PRE-SUBMIT-BUDGET-COMPENSATION-01', 'QV1-PROVIDER-FINAL-REVALIDATION-01', 'QV1-VERSIONED-ADAPTER-SESSION-PACKAGE-01', 'QV1-PACKAGED-SESSION-PROVIDER-LIFECYCLE-01', 'QV1-SINGLE-OWNER-BINDING-CALLBACK-01', 'QV1-SHARED-OPERATION-SESSION-SUBMIT-01', 'QV1-PROVIDER-DEFERRED-GENERATION-ABA-01', 'QV1-ADMISSION-CORRELATION-RESERVATION-01', 'P10-MULTI-REGION-ATOMIC-RELEASE-01', 'P10-FAILED-ADMISSION-OWNER-PIN-01')
        testIds = @('V6MemoryRuntimeEnforcementTests.AmbiguousPublicationAndExactReconciliationRemainInDirectSemanticTrace', 'V6MemoryRuntimeEnforcementTests.PostSubmitCancelRequestRemainsPossibleEffectInDirectTrace', 'V6MemoryRuntimeEnforcementTests.ProjectionRejectsNoPublicationClosureAfterPublishedEffect', 'V6MemoryRuntimeEnforcementTests.ProjectionRejectsVisibilityBeforeOwnerCompletion', 'SemanticAdmissionSentryTests.ProviderAdmissionRevokedDuringRuntimeLegalityFailsBeforeSubmit', 'AdapterBoundaryTests.VersionedExternalRuntimePackageExposesAdapterSessionWithoutGrantingAuthority', 'HybridCpuExternalOperationProviderTests.PackagedAdapterSessionDrivesExactSingNextProviderStages', 'HybridCpuExternalOperationProviderTests.PackagedAdapterSessionDoesNotTreatAmbiguousCancelAsClosure', 'HybridCpuExternalOperationProviderTests.PackagedAdapterSessionKeepsPossibleEffectStaleAfterProviderReconfiguration', 'HybridCpuExternalOperationProviderTests.ReconfigureGenerationAbaInsideAmbiguousPublicationCannotReauthorizeOldRequest')
        changedFiles = @('tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj', 'tools/HybridCpu_ExecutableAdapter/packages.lock.json', 'tools/HybridCpu_ExecutableAdapter.Tests/AdapterBoundaryTests.cs', 'tools/HybridCpu_ExecutableAdapter/README.md', 'tests/SingPlus.Tests/SingPlus.Tests.csproj', 'tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs', 'eng/v6/Qualify-V6Qv1.ps1')
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim(); sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim(); targetFramework = 'net11.0'
            externalContract = 'HybridCPU.ExternalRuntime.Contracts/1.14.0'
            externalRuntimePackage = 'HybridCPU.ExternalRuntime/1.6.0'
            externalRuntimePackageSha256 = (Get-Sha256 $externalRuntimePackage)
            externalRuntimeAssemblySha256 = (Get-Sha256 $externalRuntimeAssembly)
        }
        commandsRun = @("dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter $filter",
            "dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter $packageTestFilter")
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        packageSurfaceTests = [ordered]@{ passed = 1; failed = 0; skipped = 0; total = 1 }
        featureGate = [ordered]@{
            roadmapGate = 'V6-FIRST-QUALIFICATION-VERTICAL'
            implementationGate = 'V6-FIRST-QUALIFICATION-VERTICAL'; state = 'OFF'
        }
        positiveChain = @('SIP session invocation', 'capability/session/Region admission', 'budget reserve',
            'V1 obligations plus memory sidecar', 'planner selection', 'provider admission',
            'V1 guarantees plus memory sidecar', 'refinement', 'semantic binding',
            'independent runtime legality', 'submit', 'device completion', 'visibility',
            'publication', 'budget settlement', 'Region/external-operation release', 'invocation settlement')
        transitionAuditFields = @('owner', 'input evidence digest', 'generation vector digest',
            'failure state', 'linearization point', 'rollback or compensation rule')
        negativeCoverage = [ordered]@{
            preSubmit = @('shared session cancellation requires owner release and CancelledPreSubmit budget lease before acknowledgement',
                'capability revoked', 'session closed', 'Region generation changed',
                'budget lease stale', 'provider generation changed', 'runtime legality denied',
                'provider admission revoked during runtime legality', 'mandatory memory refinement denied', 'stale translation tuple when DMA is evaluated')
            postSubmit = @('duplicate submit', 'cancel after possible effect remains an observed request until terminal evidence',
                'shared session post-submit cancel and provider loss do not prove closure',
                'ambiguous staged publication emits quarantine and exact reconciliation emits closure',
                'ambiguous provider cancellation retains the packaged session correlation and blocks late submit',
                'completion before visibility', 'publication before visibility',
                'visibility after publication attempt', 'provider loss after possible write')
            race = @('concurrent submit has one irreversible winner', 'cancel versus completion linearizes',
                'reentrant and concurrent owner release from trace callback preserve the final observed prefix',
                'cancel between owner submit and provider callback cannot reauthorize execution',
                'owner cancellation after binding attach but before packaged submit prevents managed execution')
            externalRuntimePackage = @('concurrent admission reserves correlation before provider callback', 'transport loss after possible admission retains correlation tombstone')
            differential = 'reference V1 and v6 staged traces have the same allowed owner-event projection'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tools/HybridCpu_ExecutableAdapter.Tests/bin/Debug/net11.0/HybridCpu_ExecutableAdapter.Tests.dll',
            '.packages/HybridCPU.ExternalRuntime.1.6.0.nupkg',
            '.packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary/package evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        maximumSupportedClaim = [ordered]@{
            singNextOwnerTransitions = 'RuntimeEnforced on the exact managed local contour'
            singleOwnerBindingCallback = 'RuntimeEnforced exact SingNext operation binding passed after one owner submit commit to an execution-only packaged session provider seam'
            packagedProviderSession = 'ExecutableAdapter only for the separate managed HybridCpuExternalOperationProvider stage lifecycle; this does not compose with the QV1 sentry callback'
            sharedOperationSession = 'RuntimeEnforced owner lifecycle and pre-submit cancellation boundary under the managed callback; callback execution is not qualified provider execution'
            crossProjectContractAdapter = 'StaticAdmission for the composed QV1 mapping on the exact package tuple; qualified provider execution and independent executing HybridCPU legality remain missing'
            transitionAudit = 'RuntimeEnforced shape; non-authoritative evidence only'
            managedSemanticTraceStream = 'RuntimeEnforced best-effort observation after committed owner transitions; sink outcomes are non-authoritative'
            formal = 'ModelOnly via executable trace/refinement properties'
            dma = 'StaticAdmission stale tuple contract only; DMA is unused by the selected provider'
            physicalOrdering = 'FutureGated'; physicalDma = 'FutureGated'; persistence = 'FutureGated'
            productionSecurity = 'FutureGated'; production = 'FutureGated'
        }
        exclusions = @('shared mutable or atomic Region', 'direct coherent output', 'SVA', 'durability',
            'hard deadline', 'stateful resume', 'generic IFC', 'production attestation',
            'partial hardware RAS', 'multi-host', 'energy guarantee', 'PCL requirement', 'new ISA')
        remainingBlockers = @(
            'V6-FIRST-QUALIFICATION-VERTICAL remains OFF.',
            'The selected provider is a managed qualification adapter, not a physical accelerator campaign.',
            'The packaged session reaches the managed semantic sentry through V6SharedOperationSessionProvider and executes only the exact committed SingNext binding. Poll emits each lifecycle receipt only after the owner records that transition. Ambiguous publication never becomes a success receipt.',
            'The shared session acknowledges cancellation only after owner-confirmed pre-submit cancel, release, and budget compensation. Post-submit cancellation and provider loss preserve possible effect and quarantine; cancellation between owner submit and callback cannot reauthorize execution.',
            'The existing HybridCpuExternalOperationProvider still prepares and submits its own operation and must not be used as this sentry callback; the shared-operation seam prevents that duplicate ownership for the selected execution boundary.',
            'The named QV1 tests use a managed runtime legality decision service; no independently executing HybridCPU legality service is wired to the selected session.',
            'An unresolved admission is quarantined only within the adapter session; cross-session reconciliation requires external owner evidence.',
            'Provider revalidation must read live provider-owned admission and generation; this managed test does not establish atomicity with a physical provider callback.',
            'The shared provider rechecks SingNext owner state immediately before invoking the managed callback; that read is not an atomic physical execution fence against a later owner transition.',
            'No physical DMA/IOMMU, ordering, security, performance, or operational qualification exists.',
            'The qualified direct semantic trace stream is managed and owner-projected; no physical production provider stream exists.'
        )
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        rollbackFallback = 'Keep the gate OFF and use the unchanged V1 staged/reference execution path.'
        isaImpact = 'NONE'
    }
    $artifact.testIds += @('SemanticAdmissionSentryTests.BindingAwareSubmitPassesOnlyCommittedSingNextOperationToProviderCallback',
        'SemanticAdmissionSentryTests.PackagedSessionExecutesTheSingleCommittedSingNextBinding',
        'SemanticAdmissionSentryTests.PackagedSessionCannotExecuteBeforeOwnerCommitOrAfterLegalityDenial',
        'SemanticAdmissionSentryTests.SharedSessionExecutionFailureQuarantinesTheSingleOwner',
        'SemanticAdmissionSentryTests.SharedSessionObservesOnlyCommittedOwnerLifecycleStages',
        'SemanticAdmissionSentryTests.SharedSessionDoesNotReportAmbiguousPublicationAsPublishedOrReleased',
        'SemanticAdmissionSentryTests.SharedSessionCancellationAcknowledgesOnlyBeforeSubmit',
        'SemanticAdmissionSentryTests.SharedSessionPostSubmitCancelAndProviderLossDoNotProveClosure',
        'SemanticAdmissionSentryTests.SharedSessionCancelAfterOwnerSubmitCannotReauthorizeExecution',
        'SemanticAdmissionSentryTests.SharedSessionRechecksOwnerAfterBindingAttachBeforeExecution',
        'SemanticAdmissionSentryTests.SharedSessionRejectsSemanticsOutsideExactStagedContour',
        'SemanticAdmissionSentryTests.BindingAwareCallbackIsNotInvokedWhenIndependentLegalityDenies',
        'SemanticAdmissionSentryTests.BindingAwareProviderFailureRetainsPossibleEffectAndBudgetQuarantine',
        'V6MemoryRuntimeEnforcementTests.StagedMemoryBindingAwareSubmitUsesOneCommittedOperation',
        'V6MemoryRuntimeEnforcementTests.ReentrantReleaseFromSettlementTraceDeliversFinalOwnerEvent',
        'V6MemoryRuntimeEnforcementTests.ConcurrentReleaseFromSettlementTraceDoesNotBlockOwnerOrLoseFinalEvent',
        'VNextPhase07ExternalOperationResourceBindingTests.PublishedOperationCannotReleaseRegionWhileResourceSettlementIsInFlight',
        'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossWithQuarantinedResourceBindingCannotReleaseRegion',
        'VNextPhase07ExternalOperationResourceBindingTests.FailedRegionInvalidationStillQuarantinesResourceBoundProviderLoss',
        'VNextPhase07ExternalOperationResourceBindingTests.FailedBudgetQuarantineReportsUncontainedProviderLossAndKeepsResourcePinned',
        'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossDuringExactSettlementRetainsLossAndCompletesBudgetReceipt',
        'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossAfterBudgetSettlementAcceptsExactChargeBeforeBindingCompletion',
        'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossBindingReadRacesExactSettlementWithoutFalseQuarantineFailure',
        'VNextPhase07ExternalOperationResourceBindingTests.PriorDifferentBudgetTerminalChargeCannotCompleteExactReceipt',
        'ExternalOperationLifecycleTests.MultiRegionReleaseFailureLeavesEveryUsePinned',
        'ExternalOperationLifecycleTests.ProviderLossInvalidatesWritableUseAtTerminalMutationEpochWithoutReclaim',
        'ExternalOperationLifecycleTests.ProviderLossWithBrokenRegionUseBindingReportsFailureAndRetainsOwnerPin',
        'ExternalOperationLifecycleTests.LateCancellationCannotEraseProviderLossConsequence',
        'ExternalOperationLifecycleTests.FailedAdmissionCleanupRetainsAcquiredUseUnderOperationOwner',
        'ExternalOperationLifecycleTests.FailedAdmissionWithSuccessfulCleanupLeavesNoRegionUse')
    $artifact.changedFiles += @('src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs',
        'src/Runtime/SingPlus.Runtime/V6/V6SharedOperationSessionProvider.cs',
        'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs',
        'src/Runtime/SingPlus.Runtime/V6/V6MemorySemanticBinding.cs',
        'tests/SingPlus.Tests/Runtime/SemanticAdmissionSentryTests.cs',
        'tests/SingPlus.Tests/Runtime/V6MemoryRuntimeEnforcementTests.cs')
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# QV1 managed staged-operation evidence

- Result: $passed/$total selected managed chain, packaged provider-session, negative, race, differential, and gate tests passed.
- Positive chain: session invocation through planner/admission/refinement/runtime legality, submit, complete, visible, publish, settle, release, and invocation settlement.
- Audit: every one of the 16 roadmap steps records owner, evidence, generation, failure, linearization, and rollback/compensation fields.
- Gate: `V6-FIRST-QUALIFICATION-VERTICAL` remains OFF.
- Claim: RuntimeEnforced SingNext owner transitions and StaticAdmission cross-project contract mapping for the exact managed tuple. The executable QV1 adapter path remains partial.
- Shared-operation contour: the packaged session consumes the exact QV1 sentry callback binding and observes owner-recorded completion, visibility, publication, and release in order. This is RuntimeEnforced owner behavior under a managed callback; qualified provider execution is missing. The separate managed provider lifecycle has its own ExecutableAdapter evidence.
- Single-owner seam: the binding-aware callback receives the exact SingNext operation after its one submit commit. The packaged session executes that binding once; denied legality invokes no callback and unknown execution outcome quarantines the possible effect. The V1 callback remains compatible.
- Composition boundary: the old provider constructs a separate SingNext operation and cannot serve as this callback. The shared-operation provider never calls a second owner submit. Ambiguous publication blocks successful receipts and release. Qualified provider execution and an independently executing HybridCPU legality service remain missing.
- Cancellation boundary: only owner-confirmed pre-submit cancel, release, and CancelledPreSubmit budget compensation receive a positive acknowledgement. Post-submit cancel and provider loss retain possible effect; a cancel between owner submit and callback prevents execution and leaves owner quarantine. Admission rejects cancellation semantics outside this exact staged contour.
- Binding handoff: packaged submit rechecks the exact active SingNext owner binding after attach; cancellation before that read prevents managed execution. The read does not establish an atomic fence with physical execution.
- Package tuple: `HybridCPU.ExternalRuntime.Contracts/1.14.0` plus local `HybridCPU.ExternalRuntime/1.6.0` exposes `ExternalOperationAdapterSession` (1/1 package-surface test). Package and assembly hashes are in the JSON artifact.
- Provider generation: observed A-B-A reconfiguration during an in-flight ambiguous publication invalidates the old request and preserves the possible effect; the latest generation value alone is not treated as proof that no drift occurred.
- The earlier ``executable-adapter-package-gap`` artifact records the historical 1.3.0 failure; package availability is now resolved, while executable QV1 integration remains partial.
- Direct trace: committed managed owner transitions emit a best-effort stream equivalent to the offline projection, including reentrant and concurrent release from a sink callback; published owner release waits for exact resource settlement. Sink rejection or failure is non-authoritative.
- Not claimed: physical provider, DMA/IOMMU, hardware ordering, persistence, security, performance, or production qualification.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    if ([IO.File]::ReadAllText($markdownPath) -match '[\x00-\x08\x0B\x0C\x0E-\x1F]') {
        throw 'QV1 Markdown evidence contains a control character.'
    }
    @($jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
