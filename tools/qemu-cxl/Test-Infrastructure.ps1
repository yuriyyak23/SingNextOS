[CmdletBinding()]
param([string]$QemuPath,[string]$OutputDirectory)
. "$PSScriptRoot/Common.ps1"
$base=if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path (Get-Root '') 'logs' }
$root=Join-Path $base ('selftest пробел, кавычки '+[guid]::NewGuid().ToString('N'))
$results=New-Object 'Collections.Generic.List[object]'
function Check([string]$Name,[scriptblock]$Action) { try { & $Action; $results.Add([pscustomobject]@{Name=$Name;Passed=$true;Error=$null}) } catch { $results.Add([pscustomobject]@{Name=$Name;Passed=$false;Error=$_.Exception.Message}) } }
function Must-Fail([scriptblock]$Action) { $failed=$false;try { & $Action | Out-Null } catch {$failed=$true};if (!$failed) {throw 'Expected rejection did not occur.'} }
Check 'Initialize scratch storage' { $null=& "$PSScriptRoot/Initialize-CxlEnvironment.ps1" -WorkDir $root -QemuPath $QemuPath }
Check 'Backing sizes' { if ((Get-Item -LiteralPath (Join-Path $root 'memory/cxl-pmem.raw')).Length -ne 268435456 -or (Get-Item -LiteralPath (Join-Path $root 'memory/cxl-lsa.raw')).Length -ne 1048576) {throw 'Actual byte lengths differ from MiB defaults'}; $c=Read-Config $root; $h=Open-Backing $c $root;foreach ($f in $h) {$f.Dispose()} }
Check 'Synthetic scratch sentinel survives reinitialization' {
    $f=[IO.File]::Open((Join-Path $root 'memory/cxl-pmem.raw'),'Open','ReadWrite','None');try {$f.Position=$f.Length-16;$f.WriteByte(0xA7);$f.Flush($true)} finally {$f.Dispose()}
    $before=Get-FileEvidence (Join-Path $root 'memory/cxl-pmem.raw')
    $null=& "$PSScriptRoot/Initialize-CxlEnvironment.ps1" -WorkDir $root
    $after=Get-FileEvidence (Join-Path $root 'memory/cxl-pmem.raw');if ($before.SHA256 -ne $after.SHA256) {throw 'Reinitialization destroyed data.'}
}
Check 'Reject resize during reinitialization' {Must-Fail {& "$PSScriptRoot/Initialize-CxlEnvironment.ps1" -WorkDir $root -CxlMiB 512}}
Check 'Reject invalid alignment' {Must-Fail {& "$PSScriptRoot/Initialize-CxlEnvironment.ps1" -WorkDir (Join-Path $root 'invalid') -CxlMiB 257}}
Check 'Arguments preserve Unicode spaces commas and CFMW dots' {
    $r=& "$PSScriptRoot/Start-QemuCxlPersistent.ps1" -WorkDir $root -DryRun
    if ($r.Arguments -notcontains 'q35,cxl=on,cxl-fmw.0.targets.0=cxl.1,cxl-fmw.0.size=4096M') {throw 'CFMW argument mismatch'}
    if ((Escape-Qemu 'пробел, кавычки') -ne 'пробел,, кавычки') {throw 'Comma/Unicode escaping failed'}
}
Check 'Reject locked backing file' {
    $f=[IO.File]::Open((Join-Path $root 'memory/cxl-pmem.raw'),'Open','ReadWrite','None');try {Must-Fail {& "$PSScriptRoot/Backup-CxlMemory.ps1" -WorkDir $root -Destination (Join-Path $root 'locked-backup')}} finally {$f.Dispose()}
}
Check 'Capture persistence baseline' { $null=& "$PSScriptRoot/Test-CxlPersistence.ps1" -WorkDir $root -Mode Capture }
Check 'Backup hashes' { $null=& "$PSScriptRoot/Backup-CxlMemory.ps1" -WorkDir $root -Destination (Join-Path $root 'backup') }
Check 'Reject backup overwrite' { Must-Fail {& "$PSScriptRoot/Backup-CxlMemory.ps1" -WorkDir $root -Destination (Join-Path $root 'backup')} }
Check 'Restore WhatIf preserves data' { & "$PSScriptRoot/Restore-CxlMemory.ps1" -WorkDir $root -Source (Join-Path $root 'backup') -WhatIf | Out-Null }
Check 'Restore into exclusively synthetic scratch files and verify' {
    $f=[IO.File]::Open((Join-Path $root 'memory/cxl-pmem.raw'),'Open','ReadWrite','None');try {$f.Position=$f.Length-16;$f.WriteByte(0xB8)} finally {$f.Dispose()}
    Must-Fail {& "$PSScriptRoot/Test-CxlPersistence.ps1" -WorkDir $root}
    $null=& "$PSScriptRoot/Restore-CxlMemory.ps1" -WorkDir $root -Source (Join-Path $root 'backup') -Confirm:$false
    $r=& "$PSScriptRoot/Test-CxlPersistence.ps1" -WorkDir $root
    if (!$r.HostSidePersistenceVerified) {throw 'Restored data mismatch'}
}
Check 'Reject tampered backup' {
    $p=Join-Path $root 'backup/cxl-lsa.raw';$f=[IO.File]::Open($p,'Open','Write','None');try {$f.WriteByte(0x3C)} finally {$f.Dispose()}
    Must-Fail {& "$PSScriptRoot/Restore-CxlMemory.ps1" -WorkDir $root -Source (Join-Path $root 'backup') -Confirm:$false}
}
Check 'Reject wrong PID ownership' {
    $p=Get-Process -Id $PID
    Write-Json @{Pid=$PID;StartTicks='0';Executable=$p.Path;Port=1} (Join-Path $root 'config/instance.json')
    Must-Fail {& "$PSScriptRoot/Stop-QemuCxl.ps1" -WorkDir $root}
    [IO.File]::Delete((Join-Path $root 'config/instance.json'))
}
Check 'Smoke probe plus deterministic process cleanup' {
    $r=& "$PSScriptRoot/Test-QemuCxl.ps1" -WorkDir $root -QemuPath $QemuPath -SmokeTest
    if (!$r.PSObject.Properties['SmokeCxlDeviceCreated'] -or !$r.SmokeCxlDeviceCreated -or $r.SmokeExitCode -ne 0) {throw ($r.Errors -join '; ')}
    if (Get-OwnedProcess $r.SmokeDirectory) {throw 'Smoke process leaked'}
    Write-Json $r (Join-Path $root 'logs/qualification.json')
}
Check 'Host hashes unchanged after smoke and stop' { $null=& "$PSScriptRoot/Test-CxlPersistence.ps1" -WorkDir $root }
Write-Json @($results.ToArray()) (Join-Path $root 'logs/selftest.json')
$results.ToArray() | Format-Table -AutoSize
Write-Output "Evidence retained: $root"
if (@($results | Where-Object {!$_.Passed}).Count) { throw 'Infrastructure self-tests failed; inspect selftest.json.' }



