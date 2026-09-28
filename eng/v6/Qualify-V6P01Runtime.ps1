[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p01-staged-memory-runtime')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$filter = 'FullyQualifiedName~MemorySemanticsV1Tests|FullyQualifiedName~V6MemoryRuntimeEnforcementTests|FullyQualifiedName~VisibilityRegionLocalityV1Tests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$command = "dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter `"$filter`""
$adapterCommand = 'dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~SemanticTraceInstrumentationTests"'
$performanceCommand = 'dotnet run --project tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p01-staged-memory-performance --output artifacts/v6/p01-staged-memory-runtime/performance.json'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/SemanticExtensionContracts.cs',
    'contracts/SingPlus.Contracts/MemorySemanticsV1.cs',
    'docs/SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-001-MEMORY-ORDER-INCOMPARABILITY.md',
    'contracts/SingPlus.Contracts/SemanticTraceContracts.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6MemorySemanticBinding.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceProjection.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ExternalOperationTraceStream.cs',
    'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs',
    'src/Runtime/SingPlus.Runtime/VNext/RuntimeKernel.SemanticObligations.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs',
    'src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tests/SingPlus.Tests/Contracts/MemorySemanticsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6MemoryRuntimeEnforcementTests.cs',
    'tests/SingPlus.Tests/Runtime/VisibilityRegionLocalityV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/HybridCpu_ExecutableAdapter/Adapter/HybridCpuExecutableChildAdapter.cs',
    'tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj',
    'tools/HybridCpu_ExecutableAdapter.Tests/SemanticTraceInstrumentationTests.cs',
    'tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P01StagedMemoryPerformanceQualification.cs',
    'eng/v6/Qualify-V6P01Runtime.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "P01 runtime tests failed with exit code $exitCode.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The test runner summary could not be parsed.' }
    $adapterOutput = & dotnet test 'tools\HybridCpu_ExecutableAdapter.Tests\HybridCpu_ExecutableAdapter.Tests.csproj' --no-restore --filter 'FullyQualifiedName~SemanticTraceInstrumentationTests' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P01 HybridCPU adapter tests failed.`n$adapterOutput" }
    $adapterMatch = [regex]::Match($adapterOutput, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $adapterMatch.Success) { throw 'The P01 HybridCPU test summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value + [int]$adapterMatch.Groups[1].Value
    $passed = [int]$match.Groups[2].Value + [int]$adapterMatch.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value + [int]$adapterMatch.Groups[3].Value
    $total = [int]$match.Groups[4].Value + [int]$adapterMatch.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 60 -or $skipped -ne 0 -or $total -ne 60) {
        throw "Unexpected P01 runtime test counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p01-staged-memory-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P01 staged-memory performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p01-staged-memory-performance/1' -or
        @($performance.summaries).Count -ne 3 -or
        @($performance.summaries | Where-Object supportsDirectCoherentPromotion).Count -ne 0) {
        throw 'The P01 staged-memory performance artifact is malformed.'
    }
    $largePayload = @($performance.summaries | Where-Object payloadBytes -eq 65536)[0]
    if ($null -eq $largePayload) { throw 'The P01 64 KiB performance summary is missing.' }

    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $sourceSetPayload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($sourceSetPayload)))

    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('P01', 'P01-D-hybridcpu-trace-boundary', 'P01-E-managed-performance', 'P05-checker')
        slice = 'p01-staged-exclusive-managed-runtime-hybridcpu-start-trace-and-managed-performance'
        contour = 'single-host/read-only-input/exclusive-staged-output/publication-fence/hybridcpu-executable-start'
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim()
            sourceSetSha256 = $sourceSetDigest.ToLowerInvariant()
            sdk = (& dotnet --version).Trim()
            targetFramework = 'net11.0'
            hybridCpuIdentity = 'HybridCPU.ExternalRuntime.Contracts/1.14.0 executable adapter start boundary'
        }
        owners = @('RegionAuthority', 'ExternalOperationAuthority', 'ResourceBudgetAuthority',
            'CapabilityAuthority', 'ProcessRegistry')
        nonAuthoritativeEvidence = @('MemorySemanticsV1 sidecar', 'SemanticBindingExtensionSetV1',
            'V6MemorySemanticBindingV1', 'generation vector digest')
        featureGate = [ordered]@{
            roadmapGate = 'V6-MEMORY-SEMANTICS'
            implementationGate = 'V6-STAGED-EXCLUSIVE-MEMORY'
            registry = 'V6FeatureGates'
            state = 'OFF'
            productionConsumer = 'none'
        }
        commandsActuallyRun = @($command, $adapterCommand, $performanceCommand)
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        tests = [ordered]@{
            environment = 'Windows win-x64; .NET 11 RC1; Debug'
            passed = $passed
            failed = $failed
            skipped = $skipped
            total = $total
        }
        requirementClassification = [ordered]@{
            VerifiedExisting = @('staged/exclusive Region use and owner publication are executable in the named managed contour',
                'v6 memory order refinement treats SC and completion-before-visibility-fence as incomparable')
            Partial = @('HybridCPU adapter executes start/trace boundary but not a staged-memory visibility provider')
            Missing = @('physical CPU/device ordering and coherence evidence', 'production staged-memory consumer')
            Contradicted = @('sequential consistency alone proves device completion or a visibility fence')
            ExternalBlocked = @('named physical provider and ordering qualification')
            FutureGated = @('direct coherent output', 'shared atomic Region use', 'production deployment')
        }
        requirementIds = @('P01-MEMORY-ORDER-INCOMPARABILITY-01', 'P05-TRACE-OWNER-STATE-CONTINUITY-01', 'P10-MULTI-REGION-ATOMIC-RELEASE-01', 'P10-FAILED-ADMISSION-OWNER-PIN-01')
        testIds = @('MemorySemanticsV1Tests.StagedOutputCannotClaimSharedOwnershipOrSkipFenceVisibilityPublicationChain',
            'MemorySemanticsV1Tests.PartialOrderIsReflexiveAntisymmetricForDistinctComparableValuesAndTransitive',
            'V6MemoryRuntimeEnforcementTests.ProjectionRejectsDiscontinuousOwnerStatesAndContradictorySnapshotState',
            'ExternalOperationLifecycleTests.MultiRegionReleaseFailureLeavesEveryUsePinned',
            'ExternalOperationLifecycleTests.FailedAdmissionCleanupRetainsAcquiredUseUnderOperationOwner',
            'ExternalOperationLifecycleTests.FailedAdmissionWithSuccessfulCleanupLeavesNoRegionUse')
        coverage = [ordered]@{
            positive = @('exact staged/exclusive refinement', 'final revalidation before submit',
                'completion then visibility then publication')
            negative = @('unknown mandatory clause', 'incomparable direct-coherent guarantee',
                'SC cannot substitute for completion-before-visibility-fence ordering',
                'missing staged output', 'publication before visibility')
            race = @('provider generation drift during runtime legality',
                'Region mutation after visibility invalidates publication')
            authority = @('sidecar and binding expose no execution/effect/publication permission')
            trace = @('V1 staged and v6 sidecar paths have the same allowed owner projection',
                'ordered submit/effect/complete/visible/publish/settle/release trace',
                'direct managed owner-projected stream equals the committed offline projection',
                'direct managed sink failure cannot alter the authoritative lifecycle',
                'release without ordered settlement evidence fails closed', 'provider loss projects to quarantine',
                'missing owner origin, sequence gap, discontinuous states or contradictory snapshot state fail closed',
                'direct HybridCPU executable-start submit/effect/quarantine trace',
                'HybridCPU start failure matches the reference quarantine owner projection',
                'trace sink failure cannot change the executable outcome')
            performance = @('direct one-copy baseline', 'staged two-copy shape',
                'staged owner lifecycle with two copies', '64 B/4 KiB/64 KiB payloads',
                'rotating Release JIT named-host rounds', 'median/p95/p99 elapsed time',
                'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'
            sha256 = Get-Sha256 $performancePath
            payloads = @($performance.summaries)
            supportsDirectCoherentPromotion = $false
            interpretation = 'Managed staged-copy shape and existing owner-lifecycle overhead only; no v6 sidecar, device, physical fence, coherence, DMA, or hardware claim.'
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
            memoryContract = 'StaticAdmission'
            managedRefinementAndFinalSentry = 'RuntimeEnforced'
            stagedOwnerLifecycle = 'RuntimeEnforced'
            ownerTraceProjection = 'RuntimeEnforced checker; non-authoritative evidence'
            crossProjectAdapter = 'ExecutableAdapter for the exact HybridCPU external-runtime start trace boundary; staged memory execution remains the managed contour'
            softwarePerformance = 'ModelOnly conservative staged-path overhead characterization; no benefit or gate-promotion claim'
            physicalOrdering = 'FutureGated'
            production = 'FutureGated'
        }
        remainingBlockers = @(
            'V6-MEMORY-SEMANTICS remains OFF and has no production consumer.',
            'The concrete HybridCPU evidence covers the executable-start quarantine trace boundary, not a physical staged-memory execution.',
            'No physical CPU/device ordering or coherence evidence is included.'
        )
        rollbackFallback = 'Keep the v6 gate OFF and use the existing V1 staged path.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P01 staged-memory runtime qualification evidence

- Scope: managed single-host read-only input plus exclusive staged output.
- Result: $passed/$total selected tests passed; roadmap gate `V6-MEMORY-SEMANTICS` is represented by implementation gate `V6-STAGED-EXCLUSIVE-MEMORY`, which remains OFF.
- Runtime claim: exact sidecar refinement, final generation revalidation, and existing staged owner lifecycle are RuntimeEnforced for the managed contour.
- Memory-order correction (ADR-001): sequential consistency does not refine device completion-before-visibility-fence; staged output requires the explicit fence order. Enum values, serialization, and V1 APIs are unchanged.
- HybridCPU claim: the exact external-runtime executable-start boundary is an ExecutableAdapter and its submit/effect/quarantine owner projection matches the reference trace; observation failure cannot change execution outcome.
- Managed trace: committed QV1 owner transitions emit a direct best-effort stream equal to their offline projection; it carries no execution, effect, or publication authority.
- P01-E named-host 64 KiB result: staged two-copy delta = $([math]::Round([double]$largePayload.twoCopyOverheadPercent, 2))%; owner lifecycle over the two-copy shape = $([math]::Round([double]$largePayload.ownerLifecycleOverTwoCopyPercent, 2))%.
- Performance boundary: managed elapsed-time/allocation characterization only; it does not execute the v6 sidecar checker and cannot promote direct coherent output or physical ordering claims.
- Not claimed: physical staged-memory execution, physical ordering/coherence, DMA isolation, hardware, or production qualification.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.
- Rollback: keep the gate OFF and use the unchanged V1 staged path.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($performancePath, $jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally {
    Pop-Location
}
