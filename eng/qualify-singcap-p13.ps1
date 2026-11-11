[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'SingNextOS.slnx'
$performanceOutput = Join-Path $repositoryRoot 'artifacts/singcap-p13/SingCapPerformanceQualificationV1.json'

function Assert-LastExitCode([string]$message) {
    if ($LASTEXITCODE -ne 0) { throw $message }
}

Push-Location $repositoryRoot
try {
    dotnet restore $solution --locked-mode --nologo -v:minimal
    Assert-LastExitCode 'Locked restore failed.'

    dotnet build $solution --no-restore --nologo -v:minimal
    Assert-LastExitCode 'Deterministic solution build failed.'

    dotnet test $solution --no-build --nologo -v:minimal
    Assert-LastExitCode 'Unit/negative/property/integration/concurrency qualification failed.'

    & (Join-Path $repositoryRoot 'eng/qualify-singcap-p01.ps1') -RuntimeIdentifier $RuntimeIdentifier
    Assert-LastExitCode 'ManagedCap analyzer/full-module/NativeAOT qualification failed.'

    dotnet run --project 'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --output $performanceOutput | Out-Null
    Assert-LastExitCode 'Performance/contention qualification failed.'

    $report = Get-Content -Raw -LiteralPath $performanceOutput | ConvertFrom-Json
    if ($report.schema -ne 'SingCapPerformanceQualificationV1') { throw 'Unexpected performance report schema.' }
    foreach ($operation in @('capability-lookup', 'region-validate')) {
        foreach ($contention in @('shared', 'unrelated')) {
            foreach ($workers in @(1, 2, 4, 8, 16, 32)) {
                $rows = @($report.measurements | Where-Object {
                    $_.Operation -eq $operation -and $_.Contention -eq $contention -and $_.Workers -eq $workers
                })
                if ($rows.Count -ne 1 -or $rows[0].ThroughputPerSecond -le 0) {
                    throw "Missing or invalid performance row: $operation/$contention/$workers."
                }
            }
        }
    }

    git diff --check
    Assert-LastExitCode 'git diff --check failed.'
}
finally {
    Pop-Location
}
