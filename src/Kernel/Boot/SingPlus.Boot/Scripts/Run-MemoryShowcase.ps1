[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Run-Demo.ps1') -Scenario Memory -Configuration $Configuration -NoBuild:$NoBuild