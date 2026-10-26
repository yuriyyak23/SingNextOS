[CmdletBinding()]
param([ValidateSet('NativeWindowsFile','NativeWindowsRam')][string]$Profile='NativeWindowsFile',[string]$QemuPath,[string]$WorkDir,[switch]$Json,[switch]$SmokeTest,[ValidateRange(1,30)][int]$SmokeSeconds=3)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$r=[ordered]@{Profile=$Profile;BackingFilesAttached=($Profile -eq 'NativeWindowsFile');ColdRestartPersistence=($Profile -eq 'NativeWindowsFile');ConfigurationValid=$false;QemuDeviceCreated=$null;GuestCxlMemoryAccessible=$null;Errors=@();Notice='Guest CXL.mem access not verified.'}
try {
    $exe=Resolve-Qemu $QemuPath
    $r.QemuPath=$exe; $r.Capabilities=Get-Capabilities $exe; $r.Firmware=Get-Firmware $exe ''
    $c=Read-Config $root
    $c | Add-Member -NotePropertyName Profile -NotePropertyValue $Profile -Force
    Assert-Profile $c $root
    if (Get-OwnedProcess $root) { throw 'VM is running; use Get-QemuCxlStatus for live diagnostics.' }
    if ($Profile -eq 'NativeWindowsFile') { Assert-Offline $c $root }
    $r.BackingFiles=@()
    if ($Profile -eq 'NativeWindowsFile') {
        $r.BackingFiles=@(Get-FileEvidence (Join-Path $root 'memory/cxl-pmem.raw'))
        if ($c.LsaMiB -gt 0) { $r.BackingFiles+=Get-FileEvidence (Join-Path $root 'memory/cxl-lsa.raw') }
    } else { $r.Notice='Volatile RAM behind persistent-memdev; backing files are not attached. Guest CXL.mem access not verified.' }
    $manifest=Get-Content -LiteralPath (Join-Path $root 'config/manifest.json') -Raw | ConvertFrom-Json; $manifest=@($manifest)
    $r.ConfigurationArtifactsValid=$true
    foreach ($entry in $manifest) { $actual=Get-FileEvidence $entry.Path; if ($actual.Bytes -ne $entry.Bytes -or $actual.SHA256 -ne $entry.SHA256) { $r.ConfigurationArtifactsValid=$false; $r.Errors+='Configuration/firmware manifest mismatch: '+$entry.Path } }
    $r.Arguments=Get-QemuArguments $c $root 4444 -Headless
    $r.ConfigurationValid=($r.ConfigurationArtifactsValid -and (Test-ProfileCapabilities $r.Capabilities $c))
    if ($Profile -eq 'NativeWindowsFile' -and !$r.Capabilities.MemoryBackendFile) { $r.Errors+='Installed QEMU lacks memory-backend-file. Persistent profile cannot launch.' }
    if ($SmokeTest) {
        $test=Join-Path $root ('logs/smoke-'+[guid]::NewGuid().ToString('N'))
        foreach ($d in @('config','nvram','memory','logs')) { [void][IO.Directory]::CreateDirectory((Join-Path $test $d)) }
        $tc=$c | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $tc.QemuPath=$exe; $tc.Accel='tcg'; $tc.RamMiB=512; $tc.CpuCount=1; $tc.BootImage=$null
        [IO.File]::Copy($c.Firmware.VarsTemplate,(Join-Path $test 'nvram/uefi-vars.fd'),$false)
        $r.SmokeDirectory=$test
        # NativeWindowsRam is explicit; NativeWindowsFile keeps a labelled RAM diagnostic when unavailable.
        $args=Get-QemuArguments $tc $test 4444 -Headless
        $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0); $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop()
        $args=Get-QemuArguments $tc $test $port -Headless
        $smokeUsesRam=($Profile -eq 'NativeWindowsRam' -or !$r.Capabilities.MemoryBackendFile)
        if ($smokeUsesRam) {
            for ($i=0;$i -lt $args.Count;$i++) { if ($args[$i] -like 'memory-backend-file,id=cxlmem,*') { $args[$i]="memory-backend-ram,id=cxlmem,size=$($tc.CxlMiB)M" }; if ($args[$i] -like 'memory-backend-file,id=cxllsa,*') { $args[$i]="memory-backend-ram,id=cxllsa,size=$($tc.LsaMiB)M" } }
            $r.SmokeProfile='Volatile diagnostic only; persistence NOT tested'
        } else {
            foreach ($item in @(@('cxl-pmem.raw',([long]$tc.CxlMiB * 1MB)),@('cxl-lsa.raw',([long]$tc.LsaMiB * 1MB)))) { if ($item[1] -eq 0) {continue}; $f=[IO.File]::Open((Join-Path $test "memory/$($item[0])"),'CreateNew','Write','None'); try {$f.SetLength($item[1])} finally {$f.Dispose()} }
            $r.SmokeProfile='Isolated file-backed persistent profile'
        }
        $smokeBefore=@()
        if (!$smokeUsesRam) { $smokeBefore=@(Get-ChildItem -LiteralPath (Join-Path $test 'memory') -Filter '*.raw' | ForEach-Object { Get-FileEvidence $_.FullName }) }
        $probe=New-NativeProcess $exe $args $test
        try {
            Write-Json @{Pid=$probe.Process.Id;StartTicks=$probe.Process.StartTime.ToUniversalTime().Ticks.ToString();Executable=$exe;Port=$port} (Join-Path $test 'config/instance.json')
            $ready=$false
            for ($i=0;$i -lt 30;$i++) { if ($probe.Process.HasExited) {throw $probe.Err.Result}; try {$r.SmokeStatus=Invoke-Qmp $test 'query-status';$ready=$true;break} catch {Start-Sleep -Milliseconds 200} }
            if (!$ready) {throw 'Smoke QMP timeout'}
            $r.SmokeQtree=Invoke-Qmp $test 'human-monitor-command' @{'command-line'='info qtree'}
            $r.SmokeCxlDeviceCreated=($r.SmokeQtree -match 'cxl-type3' -and $r.SmokeQtree -match 'id "cxl0"')
            $r.PersistentMemdev=Invoke-Qmp $test 'qom-get' @{path='/machine/peripheral/cxl0';property='persistent-memdev'}
            $r.CxlBackendSizeBytes=Invoke-Qmp $test 'qom-get' @{path='/objects/cxlmem';property='size'}
            if ($tc.LsaMiB -gt 0) { $r.LsaMemdev=Invoke-Qmp $test 'qom-get' @{path='/machine/peripheral/cxl0';property='lsa'} }
            $r.CfmwSyntaxAccepted=$true
            Start-Sleep -Seconds $SmokeSeconds
            try { $null=Invoke-Qmp $test 'screendump' @{filename=(Join-Path $test 'edk2.ppm')}; $r.FirmwareScreenshot=Join-Path $test 'edk2.ppm' } catch { $r.ScreenshotError=$_.Exception.Message }
            $null=Invoke-Qmp $test 'quit'
            if (!$probe.Process.WaitForExit(5000)) {throw 'Smoke quit timeout'}
            $r.SmokeExitCode=$probe.Process.ExitCode
            if ($smokeBefore.Count) { $r.SmokeBackingUnchanged=$true; foreach ($entry in $smokeBefore) { if ((Get-FileEvidence $entry.Path).SHA256 -ne $entry.SHA256) { $r.SmokeBackingUnchanged=$false } } }
            if (!$smokeUsesRam) { $r.MemoryBackendProperties=Invoke-Probe $exe @('-object','memory-backend-file,id=probe,size=1M,mem-path=unused,help') }
            if ($r.ConfigurationValid) { $r.QemuDeviceCreated=$r.SmokeCxlDeviceCreated }
        } finally { if (!$probe.Process.HasExited) { $probe.Process.Kill();$probe.Process.WaitForExit() }; $probe.Out.Result | Set-Content (Join-Path $test 'stdout.log'); $probe.Err.Result | Set-Content (Join-Path $test 'stderr.log') }
        if ($r.Capabilities.WhpxCompiled) {
            $whpx=New-NativeProcess $exe @('-machine','q35','-accel','whpx','-m','256','-display','none','-nodefaults','-S') $test
            try { $exited=$whpx.Process.WaitForExit(2000); $r.WhpxInitializes=(!$exited); if ($exited) {$r.WhpxError=$whpx.Err.Result} } finally { if (!$whpx.Process.HasExited) {$whpx.Process.Kill();$whpx.Process.WaitForExit()} }
        }
    }
} catch { $r.Errors+=$_.Exception.Message }
if ($Json) { $r | ConvertTo-Json -Depth 30 } else { [pscustomobject]$r }




