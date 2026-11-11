[CmdletBinding()]
param([string]$WorkDir,[Parameter(Mandatory)][string]$Destination)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir; $c=Read-Config $root
$dest=[IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $dest) { throw 'Destination must be a new directory; backups never overwrite.' }
$lock=Lock-Environment $root
try {
    Assert-Offline $c $root
    $handles=Open-Backing $c $root
    try {
        $bytes=($handles | Measure-Object Length -Sum).Sum
        $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($dest))
        if ($drive.AvailableFreeSpace -lt $bytes+64MB) { throw 'Insufficient backup space.' }
        [void][IO.Directory]::CreateDirectory($dest)
        $files=@()
        foreach ($f in $handles) {
            $path=Join-Path $dest ([IO.Path]::GetFileName($f.Name))
            $out=[IO.File]::Open($path,'CreateNew','Write','None')
            try { $f.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
            $files+=Get-FileEvidence $path
        }
        Write-Json @{SchemaVersion=1;CxlMiB=$c.CxlMiB;LsaMiB=$c.LsaMiB;Files=$files} (Join-Path $dest 'backup.json')
        Get-FileEvidence (Join-Path $dest 'backup.json')
    } finally { foreach ($f in $handles) { $f.Dispose() } }
} finally { $lock.Dispose() }

