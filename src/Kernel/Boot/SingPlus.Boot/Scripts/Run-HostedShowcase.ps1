# Reproducible host-debug evidence; run from any working directory.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$bootDirectory = Split-Path -Parent $PSScriptRoot
$bootProject = Join-Path $bootDirectory 'SingPlus.Boot.csproj'
$sampleProject = Join-Path $bootDirectory 'Samples/ManagedCap/ManagedCap.csproj'
$rootProject = Join-Path $bootDirectory 'Samples/VerifiedRoot/VerifiedRoot.csproj'
$bootAssembly = Join-Path $bootDirectory 'bin/Debug/net11.0/SingPlus.Boot.dll'
$rootAssembly = Join-Path $bootDirectory 'Samples/VerifiedRoot/bin/Debug/net11.0/VerifiedRoot.dll'
$generatedAssembly = Join-Path $bootDirectory 'Samples/ManagedCap/bin/Debug/net11.0/ManagedCap.dll'
$nextAssembly = Join-Path $bootDirectory 'Samples/VerifiedRoot/bin/Next/net11.0/VerifiedRoot.dll'
$rootMethod = 'SingPlus.Boot.Samples.Arithmetic::Increment'

function Invoke-CheckedDotnet {
    param([string]$Label, [string[]]$Arguments, [int]$ExpectedExit = 0, [string[]]$Evidence = @())
    Write-Host "[showcase] $Label"
    $lines = & dotnet @Arguments 2>&1
    $code = $LASTEXITCODE
    $output = $lines -join [Environment]::NewLine
    Write-Host $output
    if ($code -ne $ExpectedExit) { throw "$Label returned $code; expected $ExpectedExit." }
    foreach ($pattern in $Evidence) {
        if ($output -notmatch $pattern) { throw "$Label did not produce required evidence: $pattern" }
    }
    Write-Host "[showcase] verified: $Label"
}

Invoke-CheckedDotnet 'Boot build' @('build', $bootProject, '-v', 'quiet')
Invoke-CheckedDotnet 'Roslyn contract build' @('build', $sampleProject, '-v', 'quiet')
try {
    Invoke-CheckedDotnet 'Roslyn forbidden API refusal' @('build', $sampleProject, '-p:IncludeForbiddenApi=true', '-v', 'quiet') 1 @('SING5001')
}
finally {
    # Restore the positive sample even if the refusal gate changed or failed.
    Invoke-CheckedDotnet 'Restore positive Roslyn sample' @('build', $sampleProject, '-v', 'quiet')
}
Invoke-CheckedDotnet 'Static root build' @('build', $rootProject, '-v', 'quiet')
Invoke-CheckedDotnet 'Generation-2 static root build' @('build', $rootProject, '-c', 'Next', '-p:NextGeneration=true', '-v', 'quiet')
Invoke-CheckedDotnet 'Default boot' @($bootAssembly, '--no-shell')
Invoke-CheckedDotnet 'CLI help' @($bootAssembly, '--help') 0 @('--sip-root', '--recovery-journal')
# The current generated-contract closure cannot be qualified by this verifier.
Invoke-CheckedDotnet 'Generated sample static qualification boundary' @($bootAssembly, '--sip', $generatedAssembly, '--sip-root', $rootMethod, '--no-shell') 1 @('Static verification failed: Unresolved signature-qualified local call target')
Invoke-CheckedDotnet 'Hosted authority showcase' @($bootAssembly, '--platform', 'host-model', '--budget', '--trace', '--sip', $rootAssembly, '--sip-root', $rootMethod, '--replacement-sip', $nextAssembly, '--script', (Join-Path $PSScriptRoot 'hosted-showcase.txt')) 0 @(
    'ManagedCap static PASS', "'ipc' passed", "'regions' passed", "'caps' passed", "'borrows' passed",
    "'sessions' passed", 'expired session invocation rejected before enqueue: SessionTimedOut', "'generated-sip' passed", 'late scope cancellation after non-cancellable acceptance: TooLateEffectMayExist; real response publication remains permitted', 'runtime scope cancellation before acceptance: Cancelled, no payload, no dispatcher entry; next generated call: 10 -> 11', "'service-restart' passed", "'supervised-restart' passed", "'external' passed", "'compute' passed", 'ExternalOperations budget: used=0, expected=0')
Invoke-CheckedDotnet 'Host model Region mapping' @($bootAssembly, '--platform', 'host-model', '--budget', '--demo', 'platform-regions', '--no-shell') 0 @("'platform-regions' passed")
Invoke-CheckedDotnet 'HybridCPU model Region mapping' @($bootAssembly, '--platform', 'hybridcpu-model', '--budget', '--script', (Join-Path $PSScriptRoot 'platform-regions-showcase.txt')) 0 @("'platform-regions' passed", 'HybridCPU neutral model adapter')
Invoke-CheckedDotnet 'Deferred compute cancellation' @($bootAssembly, '--platform', 'host-model', '--defer-compute', '--budget', '--trace', '--demo', 'compute-cancel', '--no-shell') 0 @("'compute-cancel' passed")
Invoke-CheckedDotnet 'Faulted compute remains pinned (expected failure)' @($bootAssembly, '--platform', 'host-model', '--defer-compute', '--budget', '--trace', '--demo', 'compute-fault', '--no-shell') 1 @('injected exact Host model operation fault: observe=PlatformFaulted', 'Region release=PlatformBindingActive', 'Expected injected DSC1 model fault', 'shutdown incomplete: Process teardown is fault-contained', 'reclaim observation: blocked=True', 'reclaim blocker ProviderEffectUncontained')
Invoke-CheckedDotnet 'Visible reclaim blockers and exact release' @($bootAssembly, '--budget', '--no-shell', '--script', (Join-Path $PSScriptRoot 'reclaim-showcase.txt')) 0 @('pinned operation=1:1: blocked=True, reasons=2', 'ExternalOperationActive', 'RegionUseActive', 'reclaim pid=5000: blocked=True, reasons=1', 'reclaim pid=5000: blocked=False, reasons=0', 'process 5000 exited; teardown complete')
Invoke-CheckedDotnet 'Region borrow explanations' @($bootAssembly, '--no-shell', '--script', (Join-Path $PSScriptRoot 'region-explanations.txt')) 0 @('BorrowActive', 'why-move region=1: blocked=True', 'why-free region=1: blocked=True', 'why-move region=1: blocked=False', 'why-free region=1: blocked=False', 'region 1 released')
Invoke-CheckedDotnet 'Local Region backing lifetime' @($bootAssembly, '--budget', '--trace', '--no-shell', '--script', (Join-Path $PSScriptRoot 'backing-showcase.txt')) 0 @('replacement backing: fresh lease identity', 'wrong generation=StaleGeneration', 'wrong owner=WrongRegionOwner', 'exact replacement remains valid', 'BackingLeaseActive explained', 'released handle=PlatformBindingNotFound', "'backing' passed")
Invoke-CheckedDotnet 'Shell service/session lifecycle' @($bootAssembly, '--no-shell', '--script', (Join-Path $PSScriptRoot 'service-shell.txt')) 0 @('session 1:1 opened', 'availability set to Draining', 'session 1:1 closed', 'availability set to Accepting', 'session 2:1 opened', 'session 2:1 closed')
Invoke-CheckedDotnet 'service-missing-capability.txt (expected refusal)' @($bootAssembly, '--no-shell', '--script', (Join-Path $PSScriptRoot 'service-missing-capability.txt')) 1 @('MissingCapability', 'process 5000 terminated and reclaimed', 'process 5001 terminated and reclaimed')
Invoke-CheckedDotnet 'service-foreign-close.txt (expected refusal)' @($bootAssembly, '--no-shell', '--script', (Join-Path $PSScriptRoot 'service-foreign-close.txt')) 1 @('WrongSessionOwner', 'process 5000 terminated and reclaimed', 'process 5001 terminated and reclaimed')
Invoke-CheckedDotnet 'service-revoked-admission.txt (expected refusal)' @($bootAssembly, '--no-shell', '--script', (Join-Path $PSScriptRoot 'service-revoked-admission.txt')) 1 @('MissingCapability', 'process 5000 terminated and reclaimed', 'process 5001 terminated and reclaimed')
Invoke-CheckedDotnet 'service-draining-admission.txt (expected refusal)' @($bootAssembly, '--no-shell', '--script', (Join-Path $PSScriptRoot 'service-draining-admission.txt')) 1 @('ServiceNotFound', 'process 5000 terminated and reclaimed', 'process 5001 terminated and reclaimed')
Invoke-CheckedDotnet 'Existing HybridCPU executable child feature profile' @($bootAssembly, '--platform', 'hybridcpu-child', '--no-shell', '--script', (Join-Path $PSScriptRoot 'hybridcpu-child-profile.txt')) 0 @('ChildExecutableArtifact\s+1\s+Executable', 'platform profile\s+hybridcpu-child', 'advertised features do not prove guest execution', 'actual child-domain create/configure/destroy completed', 'child-domain probe owner and retained parent binding reclaimed')
Invoke-CheckedDotnet 'HybridCPU child profile Region lifecycle' @($bootAssembly, '--platform', 'hybridcpu-child', '--budget', '--demo', 'platform-regions', '--no-shell') 0 @('hybridcpu-child: same runtime Region/map/revoke/reclaim path', "'platform-regions' passed")
Invoke-CheckedDotnet 'HCEXE preflight schema refusal before guest effects' @($bootAssembly, '--platform', 'hybridcpu-child', '--demo', 'child-execute', '--no-shell') 1 @('HCEXE preflight refused', 'VersionSkew', 'Package schema version is unsupported', 'no guest mapping or artifact bind attempted', 'process 5000 terminated and reclaimed')
Write-Host '[showcase] All selected evidence gates passed. Full external plan remains incomplete: generated execution, provider loss/reconciliation, durable crash recovery, verified image hot reload, HybridCPU compute/executable and CXL paths are not qualified here.'
