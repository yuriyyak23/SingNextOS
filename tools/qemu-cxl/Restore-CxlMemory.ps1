[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='High')]
param([string]$WorkDir,[Parameter(Mandatory)][string]$Source)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir; $c=Read-Config $root
$sourceRoot=(Resolve-Path -LiteralPath $Source).Path
$b=Get-Content -LiteralPath (Join-Path $sourceRoot 'backup.json') -Raw | ConvertFrom-Json
if ($b.SchemaVersion -ne 1 -or $b.CxlMiB -ne $c.CxlMiB -or $b.LsaMiB -ne $c.LsaMiB) { throw 'Backup profile/size mismatch.' }
$names=@('cxl-pmem.raw'); if ($c.LsaMiB -gt 0) { $names+='cxl-lsa.raw' }
if (@($b.Files).Count -ne $names.Count) { throw 'Invalid backup file set.' }
$lock=Lock-Environment $root
$inputs=@(); $outputs=@()
try {
    Assert-Offline $c $root
    foreach ($name in $names) {
        $entry=@($b.Files | Where-Object { [IO.Path]::GetFileName($_.Path) -eq $name })
        if ($entry.Count -ne 1) { throw "Missing/duplicate backup entry: $name" }
        $input=[IO.File]::Open((Join-Path $sourceRoot $name),'Open','Read','Read')
        $inputs+=$input
        $expected=if ($name -eq 'cxl-pmem.raw') { [long]$c.CxlMiB*1MB } else { [long]$c.LsaMiB*1MB }
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $hash=([BitConverter]::ToString($sha.ComputeHash($input))).Replace('-','') } finally { $sha.Dispose() }
        if ($input.Length -ne $expected -or $entry[0].Bytes -ne $expected -or $entry[0].SHA256 -ne $hash) { throw "Backup integrity failure: $name" }
        $input.Position=0
    }
    if (!$PSCmdlet.ShouldProcess("$root/memory",'Overwrite persistent memory and LSA from verified backup (automatic rollback copy retained)')) { return }
    $outputs=Open-Backing $c $root
    $rollback=Join-Path $root ('logs/restore-rollback-'+[guid]::NewGuid().ToString('N'))
    $required=($outputs | Measure-Object Length -Sum).Sum
    $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($root))
    if ($drive.AvailableFreeSpace -lt $required+64MB) { throw 'Insufficient rollback space.' }
    [void][IO.Directory]::CreateDirectory($rollback)
    foreach ($f in $outputs) {
        $out=[IO.File]::Open((Join-Path $rollback ([IO.Path]::GetFileName($f.Name))),'CreateNew','Write','None')
        try { $f.CopyTo($out); $out.Flush($true); $f.Position=0 } finally { $out.Dispose() }
    }
    try {
        for ($i=0;$i -lt $outputs.Count;$i++) {
            $inputs[$i].CopyTo($outputs[$i]); $outputs[$i].Flush($true); $outputs[$i].Position=0
            $sha=[Security.Cryptography.SHA256]::Create()
            try { $hash=([BitConverter]::ToString($sha.ComputeHash($outputs[$i]))).Replace('-','') } finally { $sha.Dispose() }
            $entry=$b.Files | Where-Object { [IO.Path]::GetFileName($_.Path) -eq $names[$i] }
            if ($hash -ne $entry.SHA256) { throw 'Restored hash mismatch.' }
        }
    } catch {
        foreach ($f in $outputs) { $f.Position=0; $input=[IO.File]::OpenRead((Join-Path $rollback ([IO.Path]::GetFileName($f.Name)))); try { $input.CopyTo($f); $f.Flush($true) } finally { $input.Dispose() } }
        throw
    }
    Write-Output "Restore verified. Rollback copies: $rollback"
} finally { foreach ($f in @($inputs)+@($outputs)) { $f.Dispose() }; $lock.Dispose() }

