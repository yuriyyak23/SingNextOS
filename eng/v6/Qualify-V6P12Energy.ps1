[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p12-energy')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$filter = 'FullyQualifiedName~EnergyResourceContractsV1Tests|FullyQualifiedName~V6EnergyBudgetTests|FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~VNextPhase03ResourceLeaseTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/EnergyResourceContracts.cs',
    'contracts/SingPlus.Contracts/OperabilityManifestContracts.cs',
    'contracts/SingPlus.Contracts/ResourceBudgetContracts.cs',
    'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6EnergyOperationSettlement.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tests/SingPlus.Tests/Contracts/EnergyResourceContractsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6EnergyBudgetTests.cs',
    'tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase03ResourceLeaseTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P12EnergyPerformanceQualification.cs',
    'eng/v6/Qualify-V6P12Energy.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P12 energy tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The P12 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value; $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value; $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 34 -or $skipped -ne 0 -or $total -ne 34) {
        throw "Unexpected P12 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p12-energy-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P12 energy performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p12-energy-performance/1' -or
        @($performance.summaries).Count -ne 2) {
        throw 'The P12 energy performance artifact is malformed.'
    }
    $measurementPerformance = @($performance.summaries | Where-Object arm -eq 'measurement-evidence')[0]
    $capPerformance = @($performance.summaries | Where-Object arm -eq 'managed-cap-evidence')[0]
    if ($null -eq $measurementPerformance -or $null -eq $capPerformance -or
        $measurementPerformance.supportsGatePromotion -or $capPerformance.supportsGatePromotion) {
        throw 'The P12 energy performance claim boundary is malformed.'
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
        phase = @('P12-A', 'P12-B', 'P12-C-model', 'P12-D-managed-cap', 'P12-E-managed-overhead')
        slice = 'energy-vocabulary-managed-counter-cap-existing-resource-ledger-settlement-and-managed-overhead'
        contour = 'single-host/managed-model-provider/EnergyMicrojoules/measurement-and-enforced-envelope-settlement'
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim(); sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim(); targetFramework = 'net11.0'
            provider = 'V6ManagedEnergyCounterProvider/ModelOnly; V6ManagedEnergyCapProvider/ModelOnly'
            dependency = 'ResourceBudgetAuthority/EnergyMicrojoules/ExternalEffect'
        }
        requirementIds = @('P12-POST-COMPLETION-GENERATION-01', 'P12-EXACT-TERMINAL-CHARGE-01',
            'P12-POST-SETTLEMENT-GENERATION-01')
        testIds = @('V6EnergyBudgetTests.ProviderResetDuringCompletionCannotSettleOldEnergyEvidence',
            'V6EnergyBudgetTests.IndependentTerminalBudgetChargeCannotBecomeEnergySettlementReceipt',
            'V6EnergyBudgetTests.BudgetChargeDuringCompletionCannotBeRelabeledAsProviderMeasurement',
            'V6EnergyBudgetTests.ResetObservedAfterTerminalChargeCannotProduceEnergyReceipt')
        requirementClassification = [ordered]@{
            VerifiedExisting = @('ResourceBudgetAuthority owns EnergyMicrojoules charge and settlement')
            Partial = @('provider measurements and managed cap have no physical producer or enforcement qualification',
                'provider reset and budget settlement have no joint atomic linearization; post-settlement drift can be detected but a terminal charge cannot be quarantined or undone')
            ExternalBlocked = @('physical energy counter, cap, and attribution producer are unavailable')
            FutureGated = @('physical energy upper bound and production power claim')
        }
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        commandsActuallyRun = @(
            'dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter <P12 selected filter>',
            'dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p12-energy-performance --output artifacts\v6\p12-energy\performance.json'
        )
        javaDependentChecks = [ordered]@{
            state = 'SkippedByInstruction'
            claimLimit = 'No Java-dependent cross-language confirmation is inferred; physical counter, cap and production claims remain FutureGated.'
        }
        featureGate = [ordered]@{ roadmapGate = 'V6-ENERGY-BUDGETS'; implementationGate = 'V6-ENERGY-BUDGETS'; state = 'OFF' }
        ownership = [ordered]@{
            resourceBudgetAuthority = 'committed EnergyMicrojoules reservations and settlement'
            provider = 'measurement, counter/reset generation, thermal and DVFS evidence'
            scheduler = 'policy consumer only'
            evidence = 'never budget or execution authority'
        }
        coverage = [ordered]@{
            distinctions = @('requested budget', 'measured energy', 'enforced upper bound',
                'thermal state', 'DVFS state', 'deadline/performance guarantee')
            accounting = @('reserve', 'pre-submit refund', 'possible-effect pin',
                'actual settlement', 'idempotent replay charging')
            managedProvider = @('named deterministic model counter', 'exact operation correlation',
                'provider and counter generation binding', 'measurement-to-existing-ledger settlement',
                'named deterministic per-operation cap admission', 'exact enforced-upper-bound evidence matching',
                'malformed token/evidence rejection', 'over-envelope quarantine',
                'post-completion provider/counter generation revalidation before charge',
                'terminal budget charge must equal the completed measurement before a receipt is returned',
                'post-settlement observed provider reset suppresses the receipt while preserving the terminal charge')
            negative = @('counter reset generation', 'provider reset generation', 'counter rollover',
                'wrong operation replay', 'false enforcement mechanism', 'cap violation pins full reservation',
                'cap provider reset', 'over envelope')
            performance = @('Release JIT named-host campaign', 'ledger-only baseline',
                'measurement evidence overhead', 'managed-cap evidence overhead',
                'alternating arm order', 'median/p95/p99 elapsed time', 'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            baseline = 'reserve-bind-consume-settle on existing EnergyMicrojoules ledger'
            measurementEvidenceMedianOverheadPercent = [double]$measurementPerformance.medianOverheadPercent
            managedCapEvidenceMedianOverheadPercent = [double]$capPerformance.medianOverheadPercent
            supportsGatePromotion = $false
            interpretation = 'Host-specific software accounting/evidence overhead only; elapsed time is not physical energy or power evidence.'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        changedFilesThisIteration = @('src/Runtime/SingPlus.Runtime/V6/V6EnergyOperationSettlement.cs',
            'tests/SingPlus.Tests/Runtime/V6EnergyBudgetTests.cs', 'eng/v6/Qualify-V6P12Energy.ps1')
        publicApiPackageSchemaDelta = 'No public API, package, or schema change; managed settlement validation only.'
        maximumSupportedClaim = [ordered]@{
            vocabulary = 'StaticAdmission'
            energyBudgetAccounting = 'RuntimeEnforced by existing ResourceBudgetAuthority on managed accounting contour'
            providerMeasurement = 'ModelOnly executable adapter connected to RuntimeEnforced ledger settlement'
            enforcedEnergyCap = 'EnforcedUpperBound for exact operation envelope on named managed model provider; no physical power claim'
            softwarePerformance = 'ModelOnly overhead characterization; no benefit or gate-promotion claim'
            thermalDeadlineOrPerformance = 'FutureGated'
            hardware = 'FutureGated'; production = 'FutureGated'
        }
        remainingBlockers = @(
            'V6-ENERGY-BUDGETS remains OFF.',
            'The named managed counter and cap providers are connected to settlement; no physical provider counter or cap adapter is integrated.',
            'EnforcedUpperBound evidence applies only to the deterministic managed operation envelope, not physical power or energy enforcement.',
            'The provider and budget owner have no shared atomic commit. A reset observed after settlement suppresses the receipt, but the exact terminal charge remains; a reset after the final read is outside this observation boundary.',
            'No power-cap dimension, physical energy attribution campaign, hardware, or production qualification exists.'
        )
        rollbackFallback = 'Keep the gate OFF; existing resource dimensions remain authoritative and provider thermal protection remains independent.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P12 energy/resource evidence

- Result: $passed/$total selected vocabulary, model-provider, accounting, reset, rollover, replay, and gate tests passed.
- Claims: vocabulary = StaticAdmission; measurement provider = ModelOnly; exact per-operation EnforcedUpperBound = managed model provider only; EnergyMicrojoules settlement = RuntimeEnforced on the managed contour.
- Safety: measurement is distinct from enforcement; thermal throttling grants no deadline/performance guarantee; evidence grants no authority.
- Settlement: provider/counter generation is rechecked after completion; an independently terminal budget charge cannot be relabeled as the reported measurement, including when it changes during completion.
- Post-settlement boundary: an observed reset suppresses the receipt while the exact terminal charge remains. Provider reset and budget settlement are independent; a reset after the final generation read is not excluded by this managed adapter.
- P12-E named-host overhead: measurement evidence = $([double]$measurementPerformance.medianOverheadPercent)% and managed-cap evidence = $([double]$capPerformance.medianOverheadPercent)% median delta versus the same ledger lifecycle.
- Performance boundary: managed software elapsed-time/allocation characterization only; Stopwatch results are not physical energy or power evidence and do not support gate promotion.
- Gate: `V6-ENERGY-BUDGETS` remains OFF.
- Java-dependent checks: skipped by instruction; no cross-language or physical-provider claim is inferred.
- Not claimed: physical provider counter/cap, power enforcement, hardware, production, or performance benefit.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($performancePath, $jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
