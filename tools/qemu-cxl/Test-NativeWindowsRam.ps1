[CmdletBinding()]
param([string]$QemuPath,[string]$WorkDir,[ValidateRange(1,30)][int]$BootSeconds=15)
. "$PSScriptRoot/Common.ps1"
$base=Get-Root $WorkDir
$c=Read-Config $base
if ($QemuPath) { $c.QemuPath=Resolve-Qemu $QemuPath; $c.Firmware=Get-Firmware $c.QemuPath '' }
# This test needs no PMEM/LSA files, and cannot overwrite the real environment.
$test=Join-Path $base ('logs/variant-b-'+[guid]::NewGuid().ToString('N'))
foreach ($d in @('config','nvram','logs')) { [void][IO.Directory]::CreateDirectory((Join-Path $test $d)) }
$c | Add-Member -NotePropertyName Profile -NotePropertyValue 'NativeWindowsRam' -Force
$c.BootImage=$null; $c.Accel='tcg'
Write-Json $c (Join-Path $test 'config/qemu-cxl.json')
[IO.File]::Copy($c.Firmware.VarsTemplate,(Join-Path $test 'nvram/uefi-vars.fd'),$false)
Save-Manifest $test
$report=[ordered]@{Profile='NativeWindowsRam';WorkDir=$test;ConfigurationValid=$false;QemuDeviceCreated=$false;GuestCxlMemoryAccessible=$null;BackingFilesAttached=$false;ColdRestartPersistence=$false;Runs=@();Passed=$false}
function Must-Be-RunningGuard([string]$Root) {
    $rejected=$false
    try { $null=& "$PSScriptRoot/Start-QemuCxlPersistent.ps1" -WorkDir $Root -Profile NativeWindowsRam -Headless } catch {
        if ($_.Exception.Message -notmatch 'VM is running') {throw}
        $rejected=$true
    }
    if (!$rejected) { throw 'Duplicate instance was not rejected.' }
}
$owned=$null
try {
    $rejected=$false
    try { $null=& "$PSScriptRoot/Start-QemuCxlPersistent.ps1" -WorkDir $test -Profile NativeWindowsRam -LsaMiB 0 -DryRun } catch {
        if ($_.Exception.Message -notmatch 'requires nonzero LSA') {throw}
        $rejected=$true
    }
    if (!$rejected) { throw 'Missing LSA was not rejected.' }
    $report.LsaRequiredRejectionVerified=$true
    foreach ($run in @(1,2)) {
        $lsa=$c.LsaMiB
        $instance=& "$PSScriptRoot/Start-QemuCxlPersistent.ps1" -WorkDir $test -Profile NativeWindowsRam -LsaMiB $lsa -Headless
        $owned=Get-OwnedProcess $test
        if (($instance.Arguments -join '|') -match 'memory-backend-file|mem-path=') { throw 'RAM profile unexpectedly attaches files.' }
        $state=& "$PSScriptRoot/Get-QemuCxlStatus.ps1" -WorkDir $test
        if (!$state.Running -or !$state.QemuDeviceCreated -or $state.Profile -ne 'NativeWindowsRam' -or $state.BackingFilesAttached -or $state.ColdRestartPersistence) { throw 'Incorrect RAM profile/device evidence.' }
        if ($state.PersistentMemdev -ne '/objects/cxlmem' -or $state.CxlBackendSizeBytes -ne ([long]$c.CxlMiB*1MB)) { throw 'Persistent region backend linkage/size mismatch.' }
        $backendType=Invoke-Qmp $test 'qom-list' @{path='/objects'}
        $mem=@($backendType | Where-Object name -eq 'cxlmem')
        if ($mem.Count -ne 1 -or $mem[0].type -ne 'child<memory-backend-ram>') { throw 'Expected RAM memory backend type.' }
        if ($lsa -gt 0 -and $state.LsaMemdev -ne '/objects/cxllsa') { throw 'LSA backend link mismatch.' }
        if ($lsa -eq 0 -and @($backendType | Where-Object name -eq 'cxllsa').Count -ne 0) { throw 'Unexpected LSA backend.' }
        Must-Be-RunningGuard $test
        Start-Sleep -Seconds $BootSeconds
        $imageName="edk2-run-$run.ppm"
        $null=Invoke-Qmp $test 'screendump' @{filename=$imageName}
        Write-Json $state (Join-Path $test "logs/status-run-$run.json")
        $null=& "$PSScriptRoot/Stop-QemuCxl.ps1" -WorkDir $test
        if (Get-OwnedProcess $test) { throw 'RAM test instance leaked.' }
        $owned=$null
        $report.Runs+=@{Run=$run;LsaMiB=$lsa;RamMiB=$state.Configuration.RamMiB;CpuCount=$state.Configuration.CpuCount;PersistentMemdev=$state.PersistentMemdev;CxlBackendSizeBytes=$state.CxlBackendSizeBytes;QemuDeviceCreated=$state.QemuDeviceCreated;RunningVmGuardVerified=$true;QmpStopVerified=$true;FirmwareScreenshot=(Join-Path $test $imageName)}
    }
    if (Test-Path -LiteralPath (Join-Path $test 'memory')) { throw 'RAM test created unexpected backing storage.' }
    $report.ConfigurationValid=$true; $report.QemuDeviceCreated=$true; $report.Passed=$true
} finally {
    if ($owned -and !$owned.HasExited) { try {$null=& "$PSScriptRoot/Stop-QemuCxl.ps1" -WorkDir $test} finally {if (!$owned.HasExited) {$owned.Kill();$owned.WaitForExit()}} }
    Write-Json $report (Join-Path $test 'logs/variant-b-result.json')
}
[pscustomobject]$report
