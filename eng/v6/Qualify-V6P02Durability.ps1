[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p02-durability')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$filter = 'FullyQualifiedName~PersistenceContractsV1Tests|FullyQualifiedName~V6ManagedDurableOutputModelTests|FullyQualifiedName~StagedDurableOutputFormalModelTests|FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~EffectPublicationSemanticsV1Tests|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.PublicationExceptionClosesProviderButQuarantinesPossiblePublicationAndRegionUses|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase07ExternalOperationResourceBindingTests.PublicationFailureAfterSettlementDoesNotUndoChargeOrReleaseRegion|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/PersistenceContracts.cs',
    'contracts/SingPlus.Contracts/ExternalOperations.cs',
    'contracts/SingPlus.Contracts/CheckpointContracts.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ManagedDurableOutputModel.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs',
    'src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs',
    'formal/v6/StagedDurableOutput.tla',
    'formal/v6/StagedDurableOutput.cfg',
    'tests/SingPlus.Tests/Contracts/PersistenceContractsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6ManagedDurableOutputModelTests.cs',
    'tests/SingPlus.Tests/Architecture/StagedDurableOutputFormalModelTests.cs',
    'tests/SingPlus.Tests/Runtime/Phase08OrdinaryCheckpointTests.cs',
    'tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs',
    'tests/SingPlus.Tests/Runtime/EffectPublicationSemanticsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P02DurabilityPerformanceQualification.cs',
    'eng/v6/Qualify-V6P02Durability.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P02 durability tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The P02 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value; $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value; $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 66 -or $skipped -ne 0 -or $total -ne 66) {
        throw "Unexpected P02 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p02-durability-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P02 durability performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p02-durability-performance/1' -or
        $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
        throw 'The P02 durability performance artifact is malformed.'
    }
    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $payload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('P02-A', 'P02-B', 'P02-C', 'P02-D',
            'P02-D-fresh-recovery-replay-high-watermarks',
            'P02-D-publication-recovery-quarantine',
            'P02-D-external-operation-owner-closure', 'P02-managed-performance')
        slice = 'named-managed-staged-durable-output-and-software-overhead'
        contour = 'single-host/managed/singnext:managed-durable-output-model'
        sliceStatus = 'managed-publication-ambiguity-closure-qualified; broader-P02-partial'
        requirementClassification = [ordered]@{
            VerifiedExisting = @('ExternalOperationAuthority owns publication truth',
                'managed durability model preserves ordered durable evidence before publication',
                'managed model revalidates provider/media/domain after publication callback before recording PublishedSequence',
                'provider loss during ambiguous publication closure invalidates the stale closure result')
            Partial = @('exact owner-decision closure composes in managed tests only',
                'fresh recovery admission remains internal')
            Missing = @('product durable-output caller and physical effect-closure producer',
                'named physical persist/flush/fence domain')
            Contradicted = @('prior staged callback exception incorrectly discarded a possibly published effect')
            ExternalBlocked = @('ADR/eADR/CXL power-fail evidence and physical media are unavailable')
            FutureGated = @('physical persistence', 'production qualification')
        }
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim(); sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim(); targetFramework = 'net11.0'
        }
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        requirementIds = @('P02-ORDER-01', 'P02-RECOVERY-FRESHNESS-01',
            'P02-PUBLICATION-AMBIGUITY-01', 'P02-PUBLICATION-OWNER-01', 'P02-EXACT-REPLAY-01',
            'P02-CLOSURE-PROVIDER-LOSS-RACE-01',
            'P02-POST-PUBLICATION-TUPLE-REVALIDATION-01')
        testIds = @('V6ManagedDurableOutputModelTests.RecoveryDuringPublicationQuarantinesPossibleEffectUntilExplicitClosure',
            'V6ManagedDurableOutputModelTests.PublicationFailureRequiresClosureBeforeFreshRetryWithoutRestaging',
            'V6ManagedDurableOutputModelTests.PublicationCallbackDriftRetainsPossibleEffectWithoutPublishedEvidence',
            'EffectPublicationSemanticsV1Tests.StagedProviderActionFailureRetainsPossibleEffectUntilExactClosure',
            'EffectPublicationSemanticsV1Tests.ManagedDurabilityReconciliationUsesExactPublicationOwnerDecision',
            'EffectPublicationSemanticsV1Tests.AmbiguousPublicationClosureRunsOutsideOwnerLockAndHasOneWinner',
            'EffectPublicationSemanticsV1Tests.ProviderLossDuringAmbiguousPublicationClosureCannotClearPossibleEffect',
            'CxlType2AcceleratorServiceTests.PublicationExceptionClosesProviderButQuarantinesPossiblePublicationAndRegionUses',
            'VNextPhase07ExternalOperationResourceBindingTests.PublicationFailureAfterSettlementDoesNotUndoChargeOrReleaseRegion')
        commandsActuallyRun = @(
            "dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter $filter",
            "dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p02-durability-performance --output $performancePath"
        )
        environment = [ordered]@{
            os = [Environment]::OSVersion.VersionString
            architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            shell = 'PowerShell'
        }
        changedFilesThisIteration = @(
            'contracts/SingPlus.Contracts/ExternalOperations.cs',
            'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
            'tests/SingPlus.Tests/Runtime/EffectPublicationSemanticsV1Tests.cs',
            'tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs',
            'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs',
            'eng/v6/Qualify-V6P02Durability.ps1',
            'src/Runtime/SingPlus.Runtime/V6/V6ManagedDurableOutputModel.cs',
            'tests/SingPlus.Tests/Runtime/V6ManagedDurableOutputModelTests.cs'
        )
        publicApiPackageSchemaDelta = 'This iteration changes only the internal managed model; the prior additive ExternalEffectBoundaryState.PossiblyExternallyVisible enum value remains, with no package or wire-format revision.'
        javaDependentChecks = [ordered]@{
            state = 'SkippedByInstruction'
            claimLimit = 'No Java-dependent TLA+ checker or cross-language confirmation is inferred; physical persistence remains FutureGated.'
        }
        featureGate = [ordered]@{ roadmapGate = 'V6-DURABLE-OUTPUT'; implementationGate = 'V6-DURABLE-OUTPUT'; state = 'OFF' }
        ownership = [ordered]@{
            persistenceProvider = 'named model data/metadata barriers and durable evidence'
            publicationOwner = 'sole published-truth transition after durable evidence'
            checkpointSubsystem = 'non-authoritative recovery correlation'
            regionAuthority = 'unchanged ownership authority; no persistence authority added'
        }
        coverage = [ordered]@{
            order = @('write', 'data persisted', 'metadata persisted', 'durable commit', 'published')
            crash = @('after write', 'torn data', 'after data persist', 'torn metadata',
                'after metadata persist', 'provider loss before durable', 'after durable before publication')
            freshness = @('provider generation', 'media generation', 'operation generation',
                'recovery generation', 'fresh runtime/process/provider admission after each crash',
                'unpublished retry revalidates provider/media/domain before publication')
            replay = @('same tuple idempotent', 'changed content or recovery generation rejected',
                'last committed value survives torn successor and older publication')
            publicationRace = @('recovery during publication quarantines possible effect',
                'provider generation, media generation, or persistence-domain drift during callback leaves publication ambiguous without PublishedSequence',
                'failed callback requires explicit no-publication closure',
                'fresh admission required after reconciliation',
                'recovery never invents PublishedSequence',
                'ExternalOperationAuthority holds possible visibility and denies cancellation/release/teardown before exact decision closure',
                'managed composition uses the existing publication owner for closure',
                'wrong decision denies before callback; concurrent closure has one winner outside owner lock',
                'provider loss during callback changes owner transition sequence and preserves possible effect')
            consumerRegression = @('Type-2 provider close does not release Region after ambiguous publication',
                'resource charge remains settled while possible publication and Region use remain held')
            specification = 'source-inspected safety transition model'
            performance = @('publication callback baseline', 'canonical durable-binding validation',
                'managed persist/evidence/publication bookkeeping', 'rotating Release JIT named-host rounds',
                'median/p95/p99 elapsed time', 'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            publicationCallbackMedianNanoseconds = [double]$performance.summary.publicationCallbackMedianNanoseconds
            contractValidationMedianNanoseconds = [double]$performance.summary.contractValidationMedianNanoseconds
            managedPersistEvidenceMedianNanoseconds = [double]$performance.summary.managedPersistEvidenceMedianNanoseconds
            contractValidationOverCallbackPercent = [double]$performance.summary.contractValidationOverCallbackPercent
            managedEvidenceOverContractPercent = [double]$performance.summary.managedEvidenceOverContractPercent
            supportsGatePromotion = $false
            interpretation = 'Named-model software bookkeeping only; no payload hashing, byte copy, media I/O, flush/barrier, or physical persistence work.'
        }
        evidenceInputs = $fileEvidence
        maximumSupportedClaim = [ordered]@{
            namedManagedProvider = 'ExecutableAdapter'
            crashOrdering = 'ModelOnly'
            recoveryFreshness = 'RuntimeEnforced on managed contour'
            softwarePerformance = 'ModelOnly managed-model bookkeeping overhead; no physical persistence or gate-promotion claim'
            adr = 'FutureGated'; eadr = 'FutureGated'; cxlPersistentMemory = 'FutureGated'
            physicalMedia = 'FutureGated'; production = 'FutureGated'
        }
        remainingBlockers = @(
            'V6-DURABLE-OUTPUT remains OFF.',
            'No named physical persistence domain or hardware flush/fence evidence is qualified.',
            'Managed model composes with ExternalOperationAuthority exact-decision closure, but no product durable-output caller or physical effect-closure producer is integrated.',
            'No ADR, eADR, or CXL persistent-memory power-fail campaign exists.',
            'The source safety model is not physical-media evidence.'
        )
        rollbackFallback = 'Keep the gate OFF; retain staged non-durable or separately qualified storage-specific paths.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P02 durable-output evidence

- Result: $passed/$total selected contract, crash, recovery, checkpoint, publication, and gate tests passed.
- Claim: ExecutableAdapter only for `singnext:managed-durable-output-model`; crash ordering remains ModelOnly.
- Safety: durable evidence grants no publication or region authority; recovery correlation restores no capability, session, or provider authority. Each crash-recovery publication attempt requires runtime/process/provider-admission generations above the last attempted tuple; exact replay and provider/media/domain are rechecked before any unpublished callback.
- Publication race: recovery during a callback and callback failure quarantine the possible effect. Retry requires explicit no-publication confirmation and fresh admission; recovery does not invent publication.
- Post-callback tuple: a successful callback is rechecked against owner-observed provider/media generations and domain before published evidence is recorded. Observed drift leaves a possible effect for reconciliation.
- Publication owner: `ExternalOperationAuthority` records possibly visible staged effects after callback failure and blocks cancellation, release, and teardown until exact-decision no-publication closure. The managed P02 model composes with that owner in tests; no product caller or physical closure producer is qualified.
- Named-host software medians: publication callback = $([double]$performance.summary.publicationCallbackMedianNanoseconds) ns; binding validation = $([double]$performance.summary.contractValidationMedianNanoseconds) ns; managed evidence/publish bookkeeping = $([double]$performance.summary.managedPersistEvidenceMedianNanoseconds) ns.
- Performance boundary: no payload hashing, media I/O, flush/barrier, or physical persistence is measured; the result cannot promote the gate or a hardware claim.
- Gate: `V6-DURABLE-OUTPUT` remains OFF.
- Java-dependent checks: skipped by instruction; the TLA+ source is not claimed as an externally checked proof.
- Not claimed: ADR, eADR, CXL persistent memory, physical-media power-fail behavior, hardware, or production qualification.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($performancePath, $jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
