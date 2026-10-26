[CmdletBinding()]
param([string]$QemuPath,[string]$WorkDir,[ValidateRange(256,1048576)][int]$CxlMiB=256,[ValidateRange(1,1024)][int]$LsaMiB=1,[ValidateRange(256,1048576)][int]$WindowMiB=4096,[ValidateRange(256,1048576)][int]$RamMiB=4096,[ValidateRange(1,256)][int]$CpuCount=4,[ValidateSet('tcg','whpx')][string]$Accel='tcg',[string]$FirmwarePath)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
if ($CxlMiB % 256 -or $WindowMiB % 256 -or $WindowMiB -lt $CxlMiB) { throw 'CXL/window must be multiples of 256 MiB and window >= CXL.' }
foreach ($d in @('memory','nvram','logs','config')) { [void][IO.Directory]::CreateDirectory((Join-Path $root $d)) }
$lock=Lock-Environment $root
try {
    $exe=Resolve-Qemu $QemuPath
    $caps=Get-Capabilities $exe
    if (!$caps.Q35 -or !$caps.Pxb -or !$caps.RootPort -or !$caps.Type3 -or !$caps.PersistentMemdev) { throw 'Required QEMU CXL capabilities absent.' }
    if ($LsaMiB -and !$caps.Lsa) { throw 'Required LSA property unsupported by this persistent Type-3 profile.' }
    if (!$caps.MemoryBackendFile) { Write-Warning 'memory-backend-file absent: storage can be prepared, but persistent VM launch is blocked.' }
    $fw=Get-Firmware $exe $FirmwarePath
    $cfgPath=Join-Path $root 'config/qemu-cxl.json'
    if (Test-Path -LiteralPath $cfgPath) {
        $c=Read-Config $root
        Assert-Offline $c $root
        foreach ($name in @('CxlMiB','LsaMiB','WindowMiB','RamMiB','CpuCount','Accel')) {
            if ($PSBoundParameters.ContainsKey($name) -and $c.$name -ne (Get-Variable $name -ValueOnly)) { throw "Existing configuration differs: $name. Reinitialization never rewrites configuration/data." }
        }
        Write-Output $c
        return
    }
    $c=[pscustomobject]@{ SchemaVersion=1; Profile='NativeWindowsFile'; QemuPath=$exe; CxlMiB=$CxlMiB; LsaMiB=$LsaMiB; WindowMiB=$WindowMiB; RamMiB=$RamMiB; CpuCount=$CpuCount; Accel=$Accel; BootImage=$null; Firmware=$fw }
    $needed=0L
    foreach ($item in @(@('memory/cxl-pmem.raw',([long]$CxlMiB * 1MB)),@('memory/cxl-lsa.raw',([long]$LsaMiB * 1MB)))) {
        if ($item[1] -eq 0) { continue }
        $path=Join-Path $root $item[0]
        if (Test-Path -LiteralPath $path) { if ((Get-Item -LiteralPath $path).Length -ne $item[1]) { throw "Existing backing has incorrect size: $path" } } else { $needed+=$item[1] }
    }
    $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($root))
    if ($drive.AvailableFreeSpace -lt $needed+64MB) { throw 'Insufficient free space (including 64 MiB reserve).' }
    foreach ($item in @(@('memory/cxl-pmem.raw',([long]$CxlMiB * 1MB)),@('memory/cxl-lsa.raw',([long]$LsaMiB * 1MB)))) {
        $path=Join-Path $root $item[0]
        if ($item[1] -gt 0 -and !(Test-Path -LiteralPath $path)) { $f=[IO.File]::Open($path,'CreateNew','ReadWrite','None'); try { $f.SetLength($item[1]); $f.Flush($true) } finally { $f.Dispose() } }
    }
    $vars=Join-Path $root 'nvram/uefi-vars.fd'
    if (!(Test-Path -LiteralPath $vars)) { [IO.File]::Copy($fw.VarsTemplate,$vars,$false) }
    if ((Get-Item -LiteralPath $vars).Length -ne (Get-Item -LiteralPath $fw.VarsTemplate).Length) { throw 'VARS size mismatch.' }
    Write-Json $c $cfgPath
    Save-Manifest $root
    Write-Json $caps (Join-Path $root 'config/capabilities.json')
    $c
} finally { $lock.Dispose() }


