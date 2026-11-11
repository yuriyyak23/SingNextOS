[CmdletBinding()]
param([string]$WorkDir,[ValidateSet('Capture','Verify')][string]$Mode='Verify')
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir; $c=Read-Config $root; $lock=Lock-Environment $root
try {
    Assert-Offline $c $root
    $handles=Open-Backing $c $root
    try {
        $entries=@()
        foreach ($f in $handles) { $sha=[Security.Cryptography.SHA256]::Create(); try { $entries+= [pscustomobject]@{Name=[IO.Path]::GetFileName($f.Name);Bytes=$f.Length;SHA256=([BitConverter]::ToString($sha.ComputeHash($f))).Replace('-','')} } finally { $sha.Dispose() } }
        $baseline=Join-Path $root 'config/persistence-baseline.json'
        if ($Mode -eq 'Capture') {
            if (Test-Path -LiteralPath $baseline) { throw 'Baseline already exists; preserve it or explicitly archive it before a new capture.' }
            Write-Json $entries $baseline
            [pscustomobject]@{BaselineCaptured=$true;HostSidePersistenceVerified=$null;GuestCxlMemoryAccessible=$null}
        } else {
            $before=Get-Content -LiteralPath $baseline -Raw | ConvertFrom-Json; $before=@($before)
            if ($before.Count -ne $entries.Count) { throw 'Backing set differs from baseline.' }
            foreach ($e in $entries) { $b=@($before | Where-Object Name -eq $e.Name); if ($b.Count -ne 1 -or $b[0].Bytes -ne $e.Bytes -or $b[0].SHA256 -ne $e.SHA256) { throw "Persistence mismatch: $($e.Name). Guest writes may legitimately change backing data." } }
            [pscustomobject]@{HostSidePersistenceVerified=$true;GuestCxlMemoryAccessible=$null;Notice='Host-side persistence verified; guest CXL.mem access not verified.';Files=$entries}
        }
    } finally { foreach ($f in $handles) { $f.Dispose() } }
} finally { $lock.Dispose() }


