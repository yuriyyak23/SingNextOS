[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$HybridRoot = '\Desktop\HybridCPU ISE',
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p00-baseline')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeInventory([string]$Root, [string[]]$Paths) {
    $files = foreach ($path in $Paths) {
        $absolute = Join-Path $Root $path
        if (Test-Path -LiteralPath $absolute -PathType Leaf) { Get-Item -LiteralPath $absolute }
        elseif (Test-Path -LiteralPath $absolute -PathType Container) {
            Get-ChildItem -LiteralPath $absolute -File -Recurse | Where-Object {
                $_.FullName -notmatch '[\\/](?:bin|obj|\.vs|artifacts)[\\/]'
            }
        }
    }
    @($files | Sort-Object FullName -Unique | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = Get-Sha256 $_.FullName
        }
    })
}

function Invoke-Captured([string]$WorkingDirectory, [string]$Executable, [string[]]$Arguments) {
    $output = & $Executable @Arguments 2>&1 | Out-String
    [ordered]@{ exitCode = $LASTEXITCODE; output = $output.TrimEnd() }
}

if (-not (Test-Path -LiteralPath $HybridRoot -PathType Container)) {
    throw "HybridCPU ISE root is missing: $HybridRoot"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Push-Location $RepositoryRoot
try {
    $head = (& git rev-parse HEAD).Trim()
    $branch = (& git branch --show-current).Trim()
    $status = @(& git status --short)
    $dotnet = Invoke-Captured $RepositoryRoot 'dotnet' @('--info')
    $architectureGuards = Invoke-Captured $RepositoryRoot 'dotnet' @(
        'test', 'tests/SingPlus.Tests/SingPlus.Tests.csproj', '--no-restore',
        '--filter', 'FullyQualifiedName~V6ArchitectureGuardTests', '--verbosity', 'quiet')
    $projects = @(Get-ChildItem -Path $RepositoryRoot -Filter '*.csproj' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|\.vs|artifacts)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName).Replace('\', '/') } |
        Sort-Object)
    $tests = @($projects | Where-Object { $_ -match '(?:Tests|Qualification)\.csproj$' })
    $locks = Get-RelativeInventory $RepositoryRoot @(
        'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'NuGet.Config',
        'SingNextOS.slnx', 'src\Runtime\SingPlus.Runtime\packages.lock.json',
        'tools\HybridCpu_ExecutableAdapter\packages.lock.json', '.packages'
    )

    $roadmapRoot = Join-Path $RepositoryRoot 'docs\SingNextOS-v6-roadmap-reworked-2026-09-23'
    $roadmapFailures = [Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content -LiteralPath (Join-Path $roadmapRoot 'SHA256SUMS.txt')) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { continue }
        $expected = $Matches[1]
        $name = $Matches[2]
        $path = Join-Path $roadmapRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $roadmapFailures.Add("missing:$name"); continue }
        $actual = Get-Sha256 $path
        if ($actual -ne $expected) { $roadmapFailures.Add("hash:${name}:$actual") }
    }

    Push-Location $HybridRoot
    try {
        $hybridGit = Invoke-Captured $HybridRoot 'git' @('rev-parse', 'HEAD')
    } finally { Pop-Location }
    $hybridInventory = Get-RelativeInventory $HybridRoot @(
        'global.json', 'Directory.Build.props', 'HybridCPU v2.slnx',
        'HybridCPU_ExternalRuntime', 'HybridCPU_ExternalRuntime.Contracts',
        'HybridCPU_ExternalRuntime.Tests'
    )

    $owners = [ordered]@{
        CapabilityAuthority = 'src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs'
        RegionAuthority = 'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs'
        ResourceBudgetAuthority = 'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs'
        ProcessRegistry = 'src/Runtime/SingPlus.Runtime/Processes/ProcessRegistry.cs'
        EndpointSessionRegistry = 'src/Runtime/SingPlus.Runtime/Sessions/EndpointSessionRegistry.cs'
        ExternalOperationAuthority = 'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs'
        RuntimeLegality = 'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs'
    }

    $baseline = [ordered]@{
        schema = 'singnext.v6.p00-baseline/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = 'P00'
        slice = 'live-baseline-freeze'
        contour = 'source-toolchain-contract-inventory'
        singNext = [ordered]@{ head = $head; branch = $branch; status = $status; projects = $projects; tests = $tests; lockedInputs = $locks }
        hybridCpu = [ordered]@{
            gitMetadataAvailable = ($hybridGit.exitCode -eq 0)
            reportedGitOutput = $hybridGit.output
            roadmapShaIsAssertedAsCurrentIdentity = $false
            inventory = $hybridInventory
        }
        toolchain = [ordered]@{ dotnetInfoExitCode = $dotnet.exitCode; dotnetInfo = $dotnet.output }
        owners = $owners
        featureGates = [ordered]@{
            registry = 'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs'
            state = if ($architectureGuards.exitCode -eq 0) { 'OFF' } else { 'UNVERIFIED' }
            allV6DefaultOff = ($architectureGuards.exitCode -eq 0)
            guardCommand = 'dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~V6ArchitectureGuardTests --verbosity quiet'
            guardExitCode = $architectureGuards.exitCode
            guardOutput = $architectureGuards.output
        }
        roadmapPackage = [ordered]@{ path = 'docs/SingNextOS-v6-roadmap-reworked-2026-09-23'; hashFailures = @($roadmapFailures); valid = ($roadmapFailures.Count -eq 0) }
        claims = [ordered]@{
            p00 = 'StaticAdmission'
            otherPhases = 'Not assessed by this identity freeze; use exact contour qualification artifacts.'
        }
        externalBlockers = @('No physical IOMMU/DMA/ordering/persistence/RAS/attestation campaign input was supplied.')
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        isaImpact = 'NONE'
    }
    if (-not $baseline.hybridCpu.gitMetadataAvailable) {
        $baseline.externalBlockers += 'HybridCPU ISE Git metadata is unavailable; identity is the recorded file inventory.'
    }

    $jsonPath = Join-Path $OutputDirectory 'baseline.json'
    $markdownPath = Join-Path $OutputDirectory 'baseline.md'
    $baseline | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    $markdown = @"
# SingNextOS v6 P00 baseline evidence

- SingNextOS: ``$head`` on ``$branch``.
- HybridCPU identity: reproducible file inventory; Git metadata available = ``$($baseline.hybridCpu.gitMetadataAvailable)``.
- Projects: $($projects.Count); test/qualification projects: $($tests.Count).
- Roadmap hash validation: ``$($baseline.roadmapPackage.valid)``; failures: ``$($roadmapFailures -join ', ')``.
- V6 gate guard: ``$($baseline.featureGates.state)``; test exit code: ``$($architectureGuards.exitCode)``.
- P00 identity claim: ``StaticAdmission``. Other phase claims require their exact contour qualification artifacts.
- ISA/opcode/architectural CPU semantics impact: ``NONE``.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.

This artifact records identity/evidence only. It grants no execution, effect, publication, or release authority.
"@
    Set-Content -LiteralPath $markdownPath -Value $markdown -Encoding utf8NoBOM
    $sums = @($jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Value $sums -Encoding ascii
    if ($roadmapFailures.Count -gt 0) { exit 2 }
    if ($architectureGuards.exitCode -ne 0) { exit 3 }
} finally {
    Pop-Location
}
