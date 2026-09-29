[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\wave1-contract-trace')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$filter = 'FullyQualifiedName~SemanticExtensionContractsV1Tests|FullyQualifiedName~SemanticTraceContractsV1Tests|FullyQualifiedName~V6ArchitectureGuardTests|FullyQualifiedName~MemorySemanticsV1Tests|FullyQualifiedName~DmaExecutionBindingV1Tests'
$command = "dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter `"$filter`""
Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Wave 1 tests failed with exit code $exitCode.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value
    $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value
    $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 52 -or $skipped -ne 0 -or $total -ne 52) {
        throw "Unexpected Wave 1 test counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }

    $changed = @(
        'contracts/SingPlus.Contracts/SemanticExtensionContracts.cs',
        'contracts/SingPlus.Contracts/SemanticTraceContracts.cs',
        'contracts/SingPlus.Contracts/V6ClaimContracts.cs',
        'contracts/SingPlus.Contracts/MemorySemanticsV1.cs',
        'contracts/SingPlus.Contracts/DmaExecutionBindingV1.cs',
        'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
        'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
        'tests/SingPlus.Tests/Contracts/SemanticExtensionContractsV1Tests.cs',
        'tests/SingPlus.Tests/Contracts/SemanticTraceContractsV1Tests.cs',
        'tests/SingPlus.Tests/Contracts/MemorySemanticsV1Tests.cs',
        'tests/SingPlus.Tests/Contracts/DmaExecutionBindingV1Tests.cs',
        'eng/v6/Freeze-V6Baseline.ps1',
        'eng/v6/Qualify-V6Wave1.ps1'
    )
    $fileEvidence = @($changed | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $assembly = Join-Path $RepositoryRoot 'contracts\SingPlus.Contracts\bin\Debug\net11.0\SingPlus.Contracts.dll'
    if (-not (Test-Path -LiteralPath $assembly)) { throw 'Contract assembly was not produced.' }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('A0', 'C0', 'F0', 'P01-contract', 'P04-contract', 'P05-trace-core')
        slice = 'wave1-additive-contract-and-trace-substrate'
        contour = 'managed-contract/static-admission/offline-trace'
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim()
            sdk = (& dotnet --version).Trim()
            targetFramework = 'net11.0'
            hybridCpuIdentity = 'unchanged; P00 inventory only; no Git metadata'
        }
        owners = @('CapabilityAuthority', 'RegionAuthority', 'ResourceBudgetAuthority',
            'ProcessRegistry', 'EndpointSessionRegistry', 'ExternalOperationAuthority', 'IRuntimeLegalityService')
        nonAuthoritativeEvidence = @('semantic extension sidecars', 'binding digest', 'semantic trace',
            'DMA generation correlation', 'claim tuple')
        featureGate = [ordered]@{ registry = 'V6FeatureGates'; state = 'OFF'; enabled = @() }
        commandsActuallyRun = @($command)
        tests = [ordered]@{ environment = 'Windows win-x64; .NET 11 RC1; Debug'; passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        coverage = [ordered]@{
            positive = @('canonical round trip', 'exact refinement', 'valid staged lifecycle')
            negative = @('unknown mandatory', 'duplicate class', 'malformed/oversized input', 'publication before visibility',
                'provider-private label cannot hide semantic lifecycle, another operation, reordered event, or malformed metadata',
                'generation drift cannot split committed Submit from EffectPossible')
            race = @('generation drift represented', 'runtime final-sentry race integration qualified separately')
            fault = @('quarantine trace', 'truncation/trailing bytes')
            differential = @('canonical order and round-trip equivalence', 'tuple-bound trace differential counterexamples',
                'reference/v6 staged workload qualified separately')
        }
        changedFiles = $fileEvidence
        publicApiDelta = @('OperationSemanticExtensionsV1', 'ExecutionGuaranteeExtensionsV1',
            'SemanticBindingExtensionSetV1', 'SemanticExtensionClassId', 'SemanticExtensionRequirement',
            'CanonicalSemanticExtensionDigest', 'SemanticTraceEventV1', 'SemanticTraceDifferentialV1', 'MemorySemanticsV1',
            'DmaExecutionBindingV1', 'V6ClaimLevel', 'V6ClaimEvidenceTuple')
        binaryHashes = @([ordered]@{ path = 'contracts/SingPlus.Contracts/bin/Debug/net11.0/SingPlus.Contracts.dll'; sha256 = Get-Sha256 $assembly })
        remainingBlockers = @(
            'The live v6 sidecars remain internal and every v6 rollout gate is OFF.',
            'This initial Wave1 slice contains no direct provider stream; managed QV1 and HybridCPU executable-start streams are qualified by later phase artifacts.',
            'No physical DMA/IOMMU/order evidence is available.'
        )
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        externalOwner = @('HybridCPU provider/device/IOMMU qualification owner for physical evidence')
        missingEvidence = @('exact provider execution trace', 'physical hardware campaign', 'performance/security/operational qualification')
        maximumSupportedClaim = [ordered]@{
            a0 = 'StaticAdmission'; c0 = 'StaticAdmission'; f0 = 'ModelOnly'
            p01Contract = 'StaticAdmission'; p04Contract = 'StaticAdmission'
            p01RuntimeSidecar = 'RuntimeEnforced on the internal managed contour'; p04Runtime = 'FutureGated'; qv1 = 'FutureGated within Wave1; qualified separately on the managed staged contour'
        }
        rollbackFallback = 'All v6 gates remain OFF; existing V1 staged/reference paths are unchanged.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# v6 Wave 1 qualification evidence

- Scope: A0/C0/F0 plus contract-only P01/P04 substrate.
- Result: $passed/$total tests passed; gates remain OFF.
- Claims: C0/P01-contract/P04-contract = StaticAdmission; F0 trace model = ModelOnly.
- Runtime/provider QV1, physical DMA/IOMMU ordering, and production qualification are not claimed.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.
- Rollback: use unchanged V1 staged/reference paths.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally {
    Pop-Location
}
