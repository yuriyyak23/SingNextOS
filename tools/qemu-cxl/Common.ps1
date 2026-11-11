Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Resolve-Qemu([string]$Path) {
    if (!$Path) { $Path = 'C:\Program Files\qemu\qemu-system-x86_64.exe'; if (!(Test-Path -LiteralPath $Path)) { $Path = 'qemu-system-x86_64.exe' } }
    (Get-Command $Path -ErrorAction Stop).Source
}
function Get-Root([string]$WorkDir) {
    if (!$WorkDir) { $WorkDir = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'CXL' }
    [IO.Path]::GetFullPath($WorkDir)
}
function Read-Config([string]$Root) { Get-Content -LiteralPath (Join-Path $Root 'config/qemu-cxl.json') -Raw | ConvertFrom-Json }
function Write-Json($Value,[string]$Path) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Lock-Environment([string]$Root) {
    [IO.File]::Open((Join-Path $Root '.operation.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}
function Get-OwnedProcess([string]$Root) {
    $path = Join-Path $Root 'config/instance.json'
    if (!(Test-Path -LiteralPath $path)) { return $null }
    $s = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $p = Get-Process -Id $s.Pid -ErrorAction SilentlyContinue
    if (!$p) { return $null }
    if ($p.StartTime.ToUniversalTime().Ticks.ToString() -ne $s.StartTicks -or $p.Path -ne $s.Executable) { throw 'PID identity mismatch; refusing to manage this process.' }
    $p
}
function Open-Backing($Config,[string]$Root) {
    $handles = @()
    try {
        foreach ($item in @(@('memory/cxl-pmem.raw',([long]$Config.CxlMiB * 1MB)),@('memory/cxl-lsa.raw',([long]$Config.LsaMiB * 1MB)))) {
            if ($item[1] -eq 0) { continue }
            $f = [IO.File]::Open((Join-Path $Root $item[0]),'Open','ReadWrite','None')
            $handles += $f
            if ($f.Length -ne $item[1]) { throw "Backing size mismatch: $($item[0])" }
        }
        return ,$handles
    } catch { foreach ($f in $handles) { $f.Dispose() }; throw }
}
function Assert-Offline($Config,[string]$Root) {
    if (Get-OwnedProcess $Root) { throw 'VM is running; stop it first.' }
    $handles = Open-Backing $Config $Root
    foreach ($f in $handles) { $f.Dispose() }
}
# Windows CommandLineToArgvW quoting, needed by ProcessStartInfo on Windows PowerShell 5.1.
function Quote-Native([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1') + '"'
}
function New-NativeProcess([string]$Exe,[string[]]$Arguments,[string]$Root) {
    $si = New-Object Diagnostics.ProcessStartInfo
    $si.FileName = $Exe
    $si.Arguments = ($Arguments | ForEach-Object { Quote-Native $_ }) -join ' '
    $si.WorkingDirectory = $Root
    $si.UseShellExecute = $false
    $si.CreateNoWindow = $true
    $si.RedirectStandardError = $true
    $si.RedirectStandardOutput = $true
    $p = New-Object Diagnostics.Process
    $p.StartInfo = $si
    [void]$p.Start()
    [pscustomobject]@{ Process=$p; Out=$p.StandardOutput.ReadToEndAsync(); Err=$p.StandardError.ReadToEndAsync() }
}
function Invoke-Probe([string]$Exe,[string[]]$Arguments,[int]$TimeoutMs=10000) {
    $r = New-NativeProcess $Exe $Arguments (Split-Path $Exe -Parent)
    if (!$r.Process.WaitForExit($TimeoutMs)) { $r.Process.Kill(); $r.Process.WaitForExit(); throw "Probe timeout: $($Arguments -join ' ')" }
    [pscustomobject]@{ ExitCode=$r.Process.ExitCode; Text=($r.Out.Result + $r.Err.Result) }
}
function Get-Capabilities([string]$Exe) {
    $v=Invoke-Probe $Exe @('--version')
    $d=Invoke-Probe $Exe @('-device','help')
    $t=Invoke-Probe $Exe @('-device','cxl-type3,help')
    $m=Invoke-Probe $Exe @('-machine','q35,cxl=on,help')
    $o=Invoke-Probe $Exe @('-object','help')
    $a=Invoke-Probe $Exe @('-accel','help')
    [pscustomobject]@{ Version=$v.Text.Trim(); Q35=($m.ExitCode -eq 0); Pxb=($d.Text -match '"pxb-cxl"'); RootPort=($d.Text -match '"cxl-rp"'); Type3=($t.ExitCode -eq 0); PersistentMemdev=($t.Text -match 'persistent-memdev'); Lsa=($t.Text -match '\blsa='); MemoryBackendFile=($o.Text -match 'memory-backend-file'); MemoryBackendRam=($o.Text -match 'memory-backend-ram'); WhpxCompiled=($a.Text -match 'whpx'); Type3Help=$t.Text; MachineHelp=$m.Text }
}
function Get-Firmware([string]$Exe,[string]$Override) {
    $share=Join-Path (Split-Path $Exe -Parent) 'share'
    $json=Join-Path $share 'firmware/60-edk2-x86_64.json'
    $secure=Join-Path $share 'firmware/50-edk2-x86_64-secure.json'
    if (!(Test-Path -LiteralPath $json)) { throw "Installed firmware descriptor missing: $json" }
    $descriptor=Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    $code=Join-Path $share ([IO.Path]::GetFileName($descriptor.mapping.executable.filename))
    $vars=Join-Path $share ([IO.Path]::GetFileName($descriptor.mapping.'nvram-template'.filename))
    if ($Override) { $code=[IO.Path]::GetFullPath($Override) }
    if (!(Test-Path -LiteralPath $code) -or !(Test-Path -LiteralPath $vars)) { throw 'Installed CODE/VARS pair unavailable.' }
    if ($code -match 'secure') { throw 'Secure firmware is outside this non-Secure-Boot profile.' }
    if ($descriptor.mapping.device -ne 'flash' -or $descriptor.mapping.executable.format -ne 'raw') { throw 'Unsupported firmware mapping.' }
    [pscustomobject]@{ Mode='pflash'; Code=$code; VarsTemplate=$vars; Descriptor=$json; SecureDescriptor=$secure; Metadata=$descriptor }
}
function Escape-Qemu([string]$Value) { $Value.Replace(',',',,') }
function Get-CxlProfile($Config) {
    if ($Config.PSObject.Properties['Profile']) { return [string]$Config.Profile }
    return 'NativeWindowsFile'
}
function Test-ProfileCapabilities($Capabilities,$Config) {
    $profile=Get-CxlProfile $Config
    $backend=if ($profile -eq 'NativeWindowsRam') { $Capabilities.MemoryBackendRam } else { $Capabilities.MemoryBackendFile }
    $backend -and $Capabilities.PersistentMemdev -and $Capabilities.Pxb -and $Capabilities.RootPort -and $Capabilities.Type3 -and $Capabilities.Q35 -and ($Config.LsaMiB -gt 0 -and $Capabilities.Lsa)
}
function Get-QemuArguments($c,[string]$Root,[int]$Port,[switch]$Headless,[switch]$Paused) {
    $pm='memory/cxl-pmem.raw'
    $a=@('-machine',"q35,cxl=on,cxl-fmw.0.targets.0=cxl.1,cxl-fmw.0.size=$($c.WindowMiB)M",'-accel',$c.Accel,'-m',"$($c.RamMiB)M",'-smp',"$($c.CpuCount)",'-object',"memory-backend-file,id=cxlmem,mem-path=$pm,size=$($c.CxlMiB)M,share=on",'-device','pxb-cxl,bus_nr=12,bus=pcie.0,id=cxl.1','-device','cxl-rp,port=0,bus=cxl.1,id=cxl-rp0,chassis=0,slot=2')
    $type='cxl-type3,bus=cxl-rp0,persistent-memdev=cxlmem,id=cxl0,sn=0x1'
    if ($c.LsaMiB -gt 0) {
        $lsa='memory/cxl-lsa.raw'
        $a+=@('-object',"memory-backend-file,id=cxllsa,mem-path=$lsa,size=$($c.LsaMiB)M,share=on")
        $type+=',lsa=cxllsa'
    }
    if ((Get-CxlProfile $c) -eq 'NativeWindowsRam') {
        for ($i=0;$i -lt $a.Count;$i++) {
            if ($a[$i] -like 'memory-backend-file,id=cxlmem,*') { $a[$i]="memory-backend-ram,id=cxlmem,size=$($c.CxlMiB)M" }
            if ($a[$i] -like 'memory-backend-file,id=cxllsa,*') { $a[$i]="memory-backend-ram,id=cxllsa,size=$($c.LsaMiB)M" }
        }
    }
    $a+=@('-device',$type,'-drive',"if=pflash,format=raw,unit=0,readonly=on,file=$(Escape-Qemu $c.Firmware.Code)",'-drive',"if=pflash,format=raw,unit=1,file=nvram/uefi-vars.fd",'-net','none','-monitor','none','-serial','none','-qmp',"tcp:127.0.0.1:$Port,server=on,wait=off",'-name','SingNextOS-CXL')
    if ($c.BootImage) { $a+=@('-drive',"file=$(Escape-Qemu $c.BootImage),format=raw,if=ide,readonly=on") }
    if ($Headless) { $a+=@('-display','none') }
    if ($Paused) { $a+='-S' }
    return ,([string[]]$a)
}
function Invoke-Qmp([string]$Root,[string]$Command,$Arguments=@{}) {
    if (!(Get-OwnedProcess $Root)) { throw 'No owned running VM.' }
    $s=Get-Content -LiteralPath (Join-Path $Root 'config/instance.json') -Raw | ConvertFrom-Json
    $client=New-Object Net.Sockets.TcpClient
    try {
        $task=$client.ConnectAsync('127.0.0.1',[int]$s.Port)
        if (!$task.Wait(3000)) { throw 'QMP connect timeout.' }
        $stream=$client.GetStream(); $stream.ReadTimeout=3000; $stream.WriteTimeout=3000
        $reader=New-Object IO.StreamReader($stream)
        $writer=New-Object IO.StreamWriter($stream); $writer.AutoFlush=$true
        $g=$reader.ReadLine() | ConvertFrom-Json
        if (!$g.QMP) { throw 'Invalid QMP greeting.' }
        foreach ($request in @(@{execute='qmp_capabilities';id='cap'},@{execute=$Command;arguments=$Arguments;id='request'})) {
            $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 10))
            do { $line=$reader.ReadLine(); if (!$line) { throw 'QMP closed.' }; $reply=$line | ConvertFrom-Json } while (!$reply.PSObject.Properties['id'] -or $reply.id -ne $request.id)
            if ($reply.PSObject.Properties['error']) { throw ($reply.error | ConvertTo-Json -Compress) }
        }
        $reply.return
    } finally { $client.Dispose() }
}
function Get-FileEvidence([string]$Path) { $i=Get-Item -LiteralPath $Path; [pscustomobject]@{ Path=$i.FullName; Bytes=$i.Length; SHA256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash } }
function Save-Manifest([string]$Root) {
    $c=Read-Config $Root
    $paths=@((Join-Path $Root 'config/qemu-cxl.json'),$c.Firmware.Code,$c.Firmware.VarsTemplate,$c.Firmware.Descriptor)
    if (Test-Path -LiteralPath $c.Firmware.SecureDescriptor) { $paths+=$c.Firmware.SecureDescriptor }
    Write-Json @($paths | ForEach-Object { Get-FileEvidence $_ }) (Join-Path $Root 'config/manifest.json')
}
function Assert-Profile($c,[string]$Root) {
    if ($c.LsaMiB -lt 1) { throw 'Persistent Type-3 requires nonzero LSA on the qualified QEMU build; use -LsaMiB 1.' }
    if ((Get-CxlProfile $c) -notin @('NativeWindowsFile','NativeWindowsRam')) { throw 'Unknown CXL profile.' }
    if ($c.SchemaVersion -ne 1 -or $c.CxlMiB -lt 256 -or $c.CxlMiB -gt 1048576 -or $c.CxlMiB % 256 -or $c.LsaMiB -lt 0 -or $c.LsaMiB -gt 1024 -or $c.RamMiB -lt 256 -or $c.RamMiB -gt 1048576 -or $c.CpuCount -lt 1 -or $c.CpuCount -gt 256 -or $c.WindowMiB % 256 -or $c.WindowMiB -lt $c.CxlMiB -or $c.Accel -notin @('tcg','whpx')) { throw 'Invalid memory/CPU/window/accelerator profile.' }
    foreach ($p in @($c.QemuPath,$c.Firmware.Code,$c.Firmware.VarsTemplate,(Join-Path $Root 'nvram/uefi-vars.fd'))) { if (!(Test-Path -LiteralPath $p -PathType Leaf)) {throw "Missing resource: $p"} }
    if ((Get-Item -LiteralPath (Join-Path $Root 'nvram/uefi-vars.fd')).Length -ne (Get-Item -LiteralPath $c.Firmware.VarsTemplate).Length) { throw 'VARS size mismatch.' }
    if ($c.BootImage) {
        $img=Join-Path (Split-Path $c.QemuPath -Parent) 'qemu-img.exe'
        $probe=Invoke-Probe $img @('info','--output=json',$c.BootImage)
        if ($probe.ExitCode -ne 0 -or ($probe.Text | ConvertFrom-Json).format -ne 'raw') {throw 'BootImage must be a raw disk/ISO image; it is attached read-only.'}
    }
}



