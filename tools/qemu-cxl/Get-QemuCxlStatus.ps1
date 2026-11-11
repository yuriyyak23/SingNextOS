[CmdletBinding()]
param([string]$WorkDir,[switch]$Json)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$c=Read-Config $root
$r=[ordered]@{Configuration=$c;Running=([bool](Get-OwnedProcess $root));ConfigurationValid=$null;QemuDeviceCreated=$null;GuestCxlMemoryAccessible=$null;Notice='Guest CXL.mem access not verified.'}
if ($r.Running) {
    $instance=Get-Content -LiteralPath (Join-Path $root 'config/instance.json') -Raw | ConvertFrom-Json
    $c=$instance.Configuration
    $r.Configuration=$c
    $r.Profile=Get-CxlProfile $c
    $r.BackingFilesAttached=($r.Profile -eq 'NativeWindowsFile')
    $r.ColdRestartPersistence=($r.Profile -eq 'NativeWindowsFile')
    if ($r.Profile -eq 'NativeWindowsRam') { $r.Notice='Volatile RAM behind persistent-memdev; CXL contents lost at QEMU exit. Guest CXL.mem access not verified.' }
    $commands=Invoke-Qmp $root 'query-commands'
    $r.QmpStatus=Invoke-Qmp $root 'query-status'
    $r.QmpCommands=@($commands | ForEach-Object {$_.name})
    if ($r.QmpCommands -contains 'human-monitor-command') {
        $r.Qtree=Invoke-Qmp $root 'human-monitor-command' @{'command-line'='info qtree'}
        $r.QemuDeviceCreated=($r.Qtree -match 'cxl-type3' -and $r.Qtree -match 'id "cxl0"')
        $r.MemoryTree=Invoke-Qmp $root 'human-monitor-command' @{'command-line'='info mtree'}
        $r.Pci=Invoke-Qmp $root 'human-monitor-command' @{'command-line'='info pci'}
    }
    if ($r.QmpCommands -contains 'qom-get') {
        $r.PersistentMemdev=Invoke-Qmp $root 'qom-get' @{path='/machine/peripheral/cxl0';property='persistent-memdev'}
        if ($c.LsaMiB -gt 0) { $r.LsaMemdev=Invoke-Qmp $root 'qom-get' @{path='/machine/peripheral/cxl0';property='lsa'} }
        $r.CxlBackendSizeBytes=Invoke-Qmp $root 'qom-get' @{path='/objects/cxlmem';property='size'}
        $r.ConfigurationValid=($r.QemuDeviceCreated -and $r.PersistentMemdev -eq '/objects/cxlmem' -and $r.CxlBackendSizeBytes -eq ([long]$c.CxlMiB*1MB))
    }
    if ($r.QmpCommands -contains 'query-memory-devices') { $r.MemoryDevices=Invoke-Qmp $root 'query-memory-devices' }
}
if ($Json) { $r | ConvertTo-Json -Depth 30 } else { [pscustomobject]$r }

