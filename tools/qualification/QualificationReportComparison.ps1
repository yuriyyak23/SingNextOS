function Assert-QualificationReportVariants {
    param($Ordinary, $Native, [byte[]]$OrdinaryBytes, [byte[]]$NativeBytes)
    if ($Ordinary.Exit -ne 0 -or $Native.Exit -ne 0) {
        throw "Successful report comparison requires exit 0 from both hosts (ordinary=$($Ordinary.Exit), native=$($Native.Exit))."
    }
    if ($Ordinary.StdOut -cne $Native.StdOut -or $Ordinary.StdErr -cne $Native.StdErr) {
        throw 'Successful report hosts produced different stdout/stderr diagnostics.'
    }
    if ($null -eq $OrdinaryBytes -or $null -eq $NativeBytes -or $OrdinaryBytes.Length -eq 0 -or
        [Convert]::ToHexString($OrdinaryBytes) -cne [Convert]::ToHexString($NativeBytes)) {
        throw 'Successful report hosts produced missing/empty/different raw canonical report bytes.'
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($OrdinaryBytes)).ToLowerInvariant()
}
