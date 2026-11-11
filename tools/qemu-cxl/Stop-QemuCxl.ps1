[CmdletBinding()]
param([string]$WorkDir)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$lock=Lock-Environment $root
try {
    $p=Get-OwnedProcess $root
    if (!$p) { Write-Output 'VM is stopped.'; return }
    $null=Invoke-Qmp $root 'quit'
    if (!$p.WaitForExit(10000)) { throw 'QMP quit timed out; no unrelated process was terminated.' }
    Write-Output 'Owned QEMU instance stopped via QMP quit (not guest ACPI shutdown).'
} finally { $lock.Dispose() }

