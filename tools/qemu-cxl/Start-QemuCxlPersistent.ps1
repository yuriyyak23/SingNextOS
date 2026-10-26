[CmdletBinding()]
param([ValidateSet('NativeWindowsFile','NativeWindowsRam')][string]$Profile,[string]$QemuPath,[string]$WorkDir,[int]$CxlMiB,[int]$LsaMiB,[int]$RamMiB,[int]$CpuCount,[ValidateSet('tcg','whpx')][string]$Accel,[string]$FirmwarePath,[string]$BootImage,[switch]$DryRun,[switch]$Headless,[switch]$Paused)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$c=Read-Config $root
if (!$Profile) { $Profile=Get-CxlProfile $c }
$c | Add-Member -NotePropertyName Profile -NotePropertyValue $Profile -Force
if ($Profile -eq 'NativeWindowsRam') { Write-Information 'NativeWindowsRam: persistent-memdev uses volatile RAM. PMEM/LSA backing files are NOT attached; all CXL contents are lost when QEMU exits.' -InformationAction Continue }
foreach ($name in @('CxlMiB','LsaMiB','RamMiB','CpuCount','Accel')) { if ($PSBoundParameters.ContainsKey($name)) { $c.$name=Get-Variable $name -ValueOnly } }
if ($c.CxlMiB -lt 256 -or $c.CxlMiB % 256 -or $c.LsaMiB -lt 0 -or $c.RamMiB -lt 256 -or $c.CpuCount -lt 1 -or $c.CpuCount -gt 256 -or $c.WindowMiB -lt $c.CxlMiB) { throw 'Invalid memory/CPU sizes.' }
if ($QemuPath) { $c.QemuPath=Resolve-Qemu $QemuPath }
if ($FirmwarePath) { $c.Firmware=Get-Firmware $c.QemuPath $FirmwarePath }
if ($BootImage) { $c.BootImage=(Resolve-Path -LiteralPath $BootImage).Path }
if (!$c.BootImage) { Write-Verbose 'No boot image: installed EDK2 will boot without an OS.'; Write-Information 'No boot image: EDK2 only; absence of OS is not a CXL failure.' -InformationAction Continue }
$lock=Lock-Environment $root
try {
    Assert-Profile $c $root
    if (Get-OwnedProcess $root) { throw 'VM is running; stop it first.' }
    if ($Profile -eq 'NativeWindowsFile') { Assert-Offline $c $root }
    foreach ($f in @($c.QemuPath,$c.Firmware.Code,(Join-Path $root 'nvram/uefi-vars.fd'))) { if (!(Test-Path -LiteralPath $f -PathType Leaf)) { throw "Missing resource: $f" } }
    # Reserve a currently free loopback port. A bind race is reported as launch failure.
    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0); $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop()
    $arguments=Get-QemuArguments $c $root $port -Headless:$Headless -Paused:$Paused
    $caps=Get-Capabilities $c.QemuPath
    $supported=Test-ProfileCapabilities $caps $c
    if ($DryRun) { return [pscustomobject]@{ Profile=$Profile; BackingFilesAttached=($Profile -eq 'NativeWindowsFile'); ColdRestartPersistence=($Profile -eq 'NativeWindowsFile'); Executable=$c.QemuPath; Arguments=$arguments; ConfigurationValid=$supported; QemuDeviceCreated=$null; GuestCxlMemoryAccessible=$null } }
    if (!$supported) { throw "Required CXL features missing for $Profile; inspect Test-QemuCxl.ps1. No automatic profile fallback is used." }
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $stdout=Join-Path $root "logs/$stamp.stdout.log"; $stderr=Join-Path $root "logs/$stamp.stderr.log"
    $p=Start-Process -FilePath $c.QemuPath -ArgumentList (($arguments | ForEach-Object { Quote-Native $_ }) -join ' ') -WorkingDirectory $root -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        $s=[pscustomobject]@{ Pid=$p.Id; StartTicks=$p.StartTime.ToUniversalTime().Ticks.ToString(); Executable=$c.QemuPath; Port=$port; Arguments=$arguments; Stdout=$stdout; Stderr=$stderr; Configuration=$c }
        Write-Json $s (Join-Path $root 'config/instance.json')
        $ready=$false
        for ($i=0;$i -lt 30;$i++) {
            if ($p.HasExited) { throw "QEMU exited ($($p.ExitCode)): $(Get-Content -LiteralPath $stderr -Raw)" }
            try { $null=Invoke-Qmp $root 'query-status'; $ready=$true; break } catch { Start-Sleep -Milliseconds 200 }
        }
        if (!$ready) { throw 'QMP startup timeout.' }
        $tree=Invoke-Qmp $root 'human-monitor-command' @{ 'command-line'='info qtree' }
        if ($tree -notmatch 'cxl-type3' -or $tree -notmatch 'id "cxl0"') { throw 'QMP did not confirm cxl0 creation.' }
        Write-Json @{Profile=$Profile;BackingFilesAttached=($Profile -eq 'NativeWindowsFile');ColdRestartPersistence=($Profile -eq 'NativeWindowsFile');ConfigurationValid=$true;QemuDeviceCreated=$true;GuestCxlMemoryAccessible=$null;Qtree=$tree;Time=(Get-Date).ToString('o')} (Join-Path $root 'logs/device-evidence.json')
        $s
    } catch { if (!$p.HasExited) { $p.Kill(); $p.WaitForExit() }; throw }
} finally { $lock.Dispose() }



