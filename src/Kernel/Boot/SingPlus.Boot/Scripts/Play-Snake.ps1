[CmdletBinding()]
param(
    [switch]$Demo,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$bootDirectory = Split-Path -Parent $PSScriptRoot
$project = Join-Path $bootDirectory 'SingPlus.Boot.csproj'
$assembly = Join-Path $bootDirectory "bin/$Configuration/net11.0/SingPlus.Boot.dll"
if (!$NoBuild) {
    & dotnet build $project -c $Configuration -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Boot build failed.' }
}
if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Missing build output: $assembly" }
if ($Demo) {
    & dotnet $assembly --script (Join-Path $PSScriptRoot 'snake-showcase.txt') --budget --trace --no-shell
}
else {
    Write-Host 'WASD or arrow keys turn instantly; automatic movement every 500 ms. R restarts; Q or Esc exits.'
    & dotnet $assembly --game snake --budget --no-shell
}
if ($LASTEXITCODE -ne 0) { throw "Snake/Boot failed with exit $LASTEXITCODE." }