[CmdletBinding()]
param(
    [string]$WorkDir,
    [string]$OutputDirectory,
    [ValidateSet('MEM','MMIO')][string[]]$Access=@('MEM','MMIO'),
    [ValidateSet('before-first-commit','committed-control','endpoint-uncommit','rootport-uncommit','both-uncommit','endpoint-global-disable','rootport-global-disable','both-global-disable','hpa-remap','dpa-remap','boundary-range','hostbridge-uncommit','hostbridge-global-disable','host-first-uncommit','leaf-first-uncommit','warm-reset','warm-reset-host-uncommit','transactional-map-retire','transactional-commit-fault','transactional-reset-fault')][string[]]$Scenario,
    [ValidateRange(60,600)][int]$TimeoutSeconds=180,
    [switch]$SkipBuild,
    [switch]$HostBridgeHdm,
    [switch]$Json
)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$config=Join-Path $root 'config/qemu-cxl.json'
if (!$OutputDirectory) { $OutputDirectory=Join-Path $root ('logs/hdm-gate-'+[guid]::NewGuid().ToString('N')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Investigation output must be a new directory.' }
$project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
$dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
if (!$SkipBuild) {
    & dotnet build $project -c Release --nologo | ForEach-Object { Write-Verbose $_ }
    if ($LASTEXITCODE -ne 0) { throw 'CXL Lab build failed.' }
}
& dotnet $dll selftest | ForEach-Object { Write-Verbose $_ }
if ($LASTEXITCODE -ne 0) { throw 'Host contract/parser self-tests failed.' }
$arguments=@($dll,'investigate-hdm','--config',$config,'--output',$OutputDirectory,'--timeout-seconds',"$TimeoutSeconds",'--access-modes',($Access -join ','))
if ($Scenario) { $arguments+=@('--scenarios',($Scenario -join ',')) }
if ($HostBridgeHdm) { $arguments+=@('--host-bridge-hdm','true') }
& dotnet @arguments | ForEach-Object { Write-Verbose $_ }
if ($LASTEXITCODE -ne 0) { throw "HDM investigation failed: $OutputDirectory" }
$path=Join-Path $OutputDirectory 'result.json'
$report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if (!$report.InvestigationCompleted) { throw "HDM matrix incomplete; inspect $path" }
if ($Json) { Get-Content -LiteralPath $path -Raw } else {
    $report | Add-Member -NotePropertyName EvidenceDirectory -NotePropertyValue $OutputDirectory -PassThru
}
