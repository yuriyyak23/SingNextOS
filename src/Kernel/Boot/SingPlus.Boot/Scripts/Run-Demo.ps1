[CmdletBinding()]
param(
    [ValidateSet('Boot', 'Hosted', 'Memory', 'Compute', 'ComputeCancel', 'VirtualDomain', 'HybridCpu', 'Services', 'All')]
    [string]$Scenario = 'Hosted',
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
    if ($LASTEXITCODE -ne 0) { throw 'SingPlus.Boot build failed.' }
}
if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Build output missing: $assembly" }
$scenarios = [ordered]@{
    Boot = @{ Arguments = @('--no-shell'); Evidence = @('host-debug runtime ready') }
    Hosted = @{ Arguments = @('--script', (Join-Path $PSScriptRoot 'hosted-counter.txt'), '--budget', '--trace', '--no-shell'); Evidence = @('41 -> 42', '100 -> 101', '0 cooperative generated SIP') }
    Memory = @{ Arguments = @('--demo', 'regions,borrows,backing,external', '--budget', '--trace', '--no-shell'); Evidence = @("'regions' passed", "'borrows' passed", "'backing' passed", "'external' passed") }
    Compute = @{ Arguments = @('--platform', 'host-model', '--demo', 'platform-regions,compute', '--budget', '--trace', '--no-shell'); Evidence = @("'platform-regions' passed", "'compute' passed") }
    ComputeCancel = @{ Arguments = @('--platform', 'host-model', '--defer-compute', '--demo', 'compute-cancel', '--budget', '--no-shell'); Evidence = @("'compute-cancel' passed") }
    VirtualDomain = @{ Arguments = @('--platform', 'host-model', '--demo', 'virtual-domain', '--budget', '--trace', '--no-shell'); Evidence = @("'virtual-domain' passed", 'exact event delivered') }
    HybridCpu = @{ Arguments = @('--platform', 'hybridcpu-model', '--demo', 'platform-regions', '--budget', '--no-shell'); Evidence = @("'platform-regions' passed") }
    Services = @{ Arguments = @('--demo', 'sessions,generated-sip,hosted-service,service-restart', '--budget', '--trace', '--no-shell'); Evidence = @("'sessions' passed", "'generated-sip' passed", "'hosted-service' passed", "'service-restart' passed") }
}
$selected = if ($Scenario -eq 'All') { @($scenarios.Keys) } else { @($Scenario) }
foreach ($name in $selected) {
    $specification = $scenarios[$name]
    $arguments = $specification.Arguments
    Write-Host "[demo] $name ($Configuration)"
    $lines = & dotnet $assembly @arguments 2>&1
    $code = $LASTEXITCODE
    $output = $lines -join [Environment]::NewLine
    Write-Host $output
    if ($code -ne 0) { throw "$name failed with exit code $code." }
    foreach ($evidence in $specification.Evidence) {
        if (!$output.Contains($evidence)) { throw "$name missing evidence: $evidence" }
    }
    if ($output.Contains('shutdown incomplete')) { throw "$name did not finish cleanup." }
    Write-Host "[demo] verified $name"
}