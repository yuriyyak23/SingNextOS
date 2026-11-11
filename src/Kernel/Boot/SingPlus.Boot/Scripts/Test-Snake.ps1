[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$bootDirectory = Split-Path -Parent $PSScriptRoot
& dotnet run --project (Join-Path $bootDirectory 'Samples/Snake.Checks/Snake.Checks.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Snake service checks failed.' }
$assembly = Join-Path $bootDirectory "bin/$Configuration/net11.0/SingPlus.Boot.dll"
$output = & dotnet $assembly --script (Join-Path $PSScriptRoot 'snake-showcase.txt') --budget --trace --no-shell 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Host $output
if ($code -ne 0) { throw "Snake showcase failed: $code" }
foreach ($evidence in @('Score: 1', 'GAME OVER', 'runtime-accounted bytes', 'worker joined; runtime Region released before process reclaim')) {
    if (!$output.Contains($evidence)) { throw "Missing evidence: $evidence" }
}
if ($output.Contains('shutdown incomplete') -or $output.Contains('singplus.game.snake.service')) {
    throw 'Snake service cleanup did not finish.'
}
Write-Host 'Snake service and ASCII showcase checks passed.'