# Deterministic missing-prerequisite regression; no installed toolchain is needed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeAotToolchain.ps1')
$toolchain = Get-NativeAotToolchain
Assert-NativeAotToolchain $toolchain -Exists { $true }
foreach ($entry in $toolchain.Requirements.GetEnumerator()) {
    $missingPath = $entry.Value
    $failure = $null
    try { Assert-NativeAotToolchain $toolchain -Exists { param($Path) $Path -cne $missingPath } }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains("$($entry.Key): $missingPath")) {
        throw "Missing $($entry.Key) was not diagnosed precisely."
    }
}
$failure = $null
try { Assert-NativeAotToolchain $toolchain -Exists { $false } }
catch { $failure = $_.Exception.Message }
foreach ($entry in $toolchain.Requirements.GetEnumerator()) {
    if ($null -eq $failure -or -not $failure.Contains("$($entry.Key): $($entry.Value)")) {
        throw 'Combined prerequisite failure omitted a diagnostic.'
    }
}
Write-Output "NativeAOT prerequisite self-check passed ($($toolchain.Requirements.Count) individual omissions and combined omission)."

# Replace only the publish command in this self-check process; exercise setup and restoration.
function dotnet {
    if ($env:LIB -cne 'test-lib' -or $env:INCLUDE -cne 'test-include' -or
        -not $env:PATH.StartsWith('test-tools;') -or
        $args -cnotcontains '-p:IlcUseEnvironmentalTools=true' -or
        $args -cnotcontains '-p:CppLinker=test-linker') { throw 'Explicit publish environment/arguments were lost.' }
    $global:LASTEXITCODE = $script:mockExit
}
$fake = @{ Requirements = [ordered]@{}; LIB = 'test-lib'; INCLUDE = 'test-include'; ToolPath = 'test-tools'; Linker = 'test-linker' }
$original = @{}
foreach ($name in @('LIB', 'INCLUDE', 'PATH')) { $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
foreach ($script:mockExit in @(0, 17)) {
    $failure = $null
    try { Invoke-NativeAotPublish $fake 'test-project' 'test-output' }
    catch { $failure = $_.Exception.Message }
    if (($mockExit -eq 0 -and $null -ne $failure) -or
        ($mockExit -eq 17 -and ($null -eq $failure -or -not $failure.Contains('exit 17')))) {
        throw "Publish disposition self-check failed: $failure"
    }
    foreach ($name in $original.Keys) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $original[$name]) {
            throw "Publish leaked process environment: $name"
        }
    }
}
Write-Output 'NativeAOT explicit publish arguments and environment restoration passed on success and failure.'
