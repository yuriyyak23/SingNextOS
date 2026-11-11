$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QualificationReportComparison.ps1')
$success = @{ Exit = 0; StdOut = ''; StdErr = '' }
$bytes = [Text.Encoding]::UTF8.GetBytes('{"SchemaId":"comparison-self-check"}')
$expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
if ((Assert-QualificationReportVariants $success $success $bytes $bytes) -cne $expected) {
    throw 'Report identity did not hash the raw bytes.'
}
$cases = @(
    @{ Result = @{ Exit = 2; StdOut = ''; StdErr = '' }; Bytes = $bytes; Reason = 'exit 0' },
    @{ Result = @{ Exit = 0; StdOut = 'changed'; StdErr = '' }; Bytes = $bytes; Reason = 'diagnostics' },
    @{ Result = @{ Exit = 0; StdOut = ''; StdErr = 'changed' }; Bytes = $bytes; Reason = 'diagnostics' },
    @{ Result = $success; Bytes = [Text.Encoding]::UTF8.GetBytes('{ "SchemaId":"comparison-self-check"}'); Reason = 'bytes' },
    @{ Result = $success; Bytes = [byte[]]@(); Reason = 'bytes' },
    @{ Result = $success; Bytes = $null; Reason = 'bytes' }
)
foreach ($case in $cases) {
    foreach ($side in @('ordinary', 'native')) {
        $failure = $null
        try {
            if ($side -eq 'native') { [void](Assert-QualificationReportVariants $success $case.Result $bytes $case.Bytes) }
            else { [void](Assert-QualificationReportVariants $case.Result $success $case.Bytes $bytes) }
        }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure -or -not $failure.Contains($case.Reason)) { throw "Report comparison failed to reject $side mismatch: $failure" }
    }
}
Write-Output 'Raw report identity and 12 symmetric exit/diagnostic/byte refusal self-checks passed; no real Qualification report was generated.'
