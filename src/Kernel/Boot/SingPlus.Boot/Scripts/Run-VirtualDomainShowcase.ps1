[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Run-Demo.ps1') -Scenario VirtualDomain -Configuration $Configuration -NoBuild:$NoBuild