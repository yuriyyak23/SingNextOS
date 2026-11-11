#Requires -Version 7.0
<#
.SYNOPSIS
Converts var to explicit types, recursively, one C# project at a time.
.EXAMPLE
pwsh -File eng/Convert-VarToExplicitTypes.ps1
.EXAMPLE
pwsh -File eng/Convert-VarToExplicitTypes.ps1 -ListProjects
.EXAMPLE
pwsh -File eng/Convert-VarToExplicitTypes.ps1 -Project src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj -SkipPrepare
.NOTES
Requires the explicit-type IDE0008 preferences in the repository .editorconfig.
Only contracts/sdk/src/tests/tools are searched. No per-file validation, backups,
hashes or log files are produced. Validation is by project build; tests are not run.
The initial build prepares semantic inputs. SkipPrepare is for already built projects.
Multiple whole-project formatter passes settle dependent fixes without inspecting files.
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [string[]]$Project = @(),
    [ValidateRange(1, 10)][int]$Passes = 3,
    [switch]$SkipPrepare,
    [switch]$ListProjects
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Root = [IO.Path]::GetFullPath($Root)
$excluded = '(?i)(^|[\\/])(artifacts|bin|obj|TempEnv|\.git)([\\/]|$)'
$projects = @{}
foreach ($folder in @('contracts', 'sdk', 'src', 'tests', 'tools')) {
    $directory = Join-Path $Root $folder
    if (Test-Path -LiteralPath $directory) {
        Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.csproj' |
            Where-Object { $_.FullName -notmatch $excluded } |
            ForEach-Object { $projects[$_.FullName] = $_.FullName }
    }
}
if ($Project.Count -gt 0) {
    $selected = @{}
    foreach ($path in $Project) {
        $fullPath = [IO.Path]::GetFullPath((Join-Path $Root $path))
        if (!$projects.ContainsKey($fullPath)) { throw "Project is outside the five source folders or was not found: $path" }
        $selected[$fullPath] = $fullPath
    }
    $projects = $selected
}

# ProjectReference dependencies come first; no parallel graph runners.
$ordered = [Collections.Generic.List[string]]::new()
$visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$visiting = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Project([string]$path) {
    if ($visited.Contains($path)) { return }
    if (!$visiting.Add($path)) { throw "ProjectReference cycle: $path" }
    [xml]$definition = Get-Content -LiteralPath $path -Raw
    foreach ($reference in $definition.SelectNodes('//ProjectReference[@Include]')) {
        if ($reference.Include.Contains('$(')) { continue }
        $dependency = [IO.Path]::GetFullPath((Join-Path (Split-Path $path) $reference.Include))
        if ($projects.ContainsKey($dependency)) { Add-Project $dependency }
    }
    [void]$visiting.Remove($path)
    [void]$visited.Add($path)
    $ordered.Add($path)
}
foreach ($path in @($projects.Keys | Sort-Object)) { Add-Project $path }
if ($ListProjects) {
    $ordered | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_) }
    return
}

function Invoke-DotNet([string[]]$arguments) {
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(dotnet|testhost|MSBuild)\.exe$' -and $_.CommandLine -notmatch 'nodemode|pipename'
    })
    if ($running.Count -gt 0) { throw 'Another dotnet/MSBuild/testhost is running. Wait for it; no processes were stopped.' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) exited with code $LASTEXITCODE" }
}

$oldNodeReuse = $env:MSBUILDDISABLENODEREUSE
$oldSharedCompilation = $env:UseSharedCompilation
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
$failed = [Collections.Generic.List[string]]::new()
$index = 0
Push-Location $Root
try {
    foreach ($path in $ordered) {
        $index++
        $relative = [IO.Path]::GetRelativePath($Root, $path)
        Write-Host "[$index/$($ordered.Count)] $relative"
        try {
            $assets = Join-Path (Split-Path $path) 'obj/project.assets.json'
            $missingAssets = !(Test-Path -LiteralPath $assets)
            if ($missingAssets) {
                Invoke-DotNet @('restore', $path, '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '--verbosity', 'quiet')
            }
            $build = @('build', $path, '--no-restore', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '--verbosity', 'quiet', '--nologo')
            if (!$SkipPrepare -or $missingAssets) {
                Write-Host '  Prepare build'
                Invoke-DotNet $build
            }
            for ($pass = 1; $pass -le $Passes; $pass++) {
                Write-Host "  IDE0008 $pass/$Passes"
                Invoke-DotNet @('format', 'style', $path, '--diagnostics', 'IDE0008', '--severity', 'info', '--no-restore', '--verbosity', 'quiet')
            }
            Write-Host '  Build'
            Invoke-DotNet $build
            Write-Host '  OK'
        } catch {
            $failed.Add($relative)
            Write-Warning "$relative : $($_.Exception.Message)"
        }
    }
} finally {
    Pop-Location
    $env:MSBUILDDISABLENODEREUSE = $oldNodeReuse
    $env:UseSharedCompilation = $oldSharedCompilation
}
Write-Host "Projects: $($ordered.Count); succeeded: $($ordered.Count - $failed.Count); failed: $($failed.Count)"
if ($failed.Count -gt 0) { exit 1 }
