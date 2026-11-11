# Dev-only policy42 collector. Never executes supplied commands or qualification tools.
# Input is a JSON document; values and provenance are supplied by the caller.
# Exit: 0 recorded passed, 1 recorded failed, 2 refused/error, 3 recorded pending/unavailable.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [string[]]$SourceDigestFiles = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-Text($Value, [string]$Name) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) { throw "$Name requires explicit nonempty text (use unavailable with provenance where appropriate)." }
}
function Require-Keys($Value, [string[]]$Required, [string[]]$Optional = @()) {
    if ($Value -isnot [System.Collections.IDictionary]) { throw 'Expected a JSON object.' }
    foreach ($key in $Required) { if (-not $Value.Contains($key)) { throw "Missing mandatory input: $key" } }
    foreach ($key in $Value.Keys) { if ($key -cnotin ($Required + $Optional)) { throw "Unknown mandatory/unsupported input: $key" } }
}
function Require-Array($Value, [string]$Name) {
    if ($Value -isnot [array]) { throw "$Name must be a JSON array." }
}
function Require-Observation($Value, [string]$Name) {
    Require-Keys $Value @('value', 'provenance')
    Require-Text $Value.value "$Name.value"
    Require-Text $Value.provenance "$Name.provenance"
}
function Assert-UniqueJsonKeys([Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $keys.Add($property.Name)) { throw "Duplicate JSON input key: $($property.Name)" }
            Assert-UniqueJsonKeys $property.Value
        }
    } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($value in $Element.EnumerateArray()) { Assert-UniqueJsonKeys $value }
    }
}
function Assert-NoReparse([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path refused: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Get-ScopedSource([string]$Relative) {
    Require-Text $Relative 'source path'
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)') { throw "Source path escape refused: $Relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source outside repository.' }
    Assert-NoReparse $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Source unavailable: $Relative" }
    return $path
}

try {
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'RepositoryRoot unavailable.' }
    Assert-NoReparse $root
    if ($OutputDirectory -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Output path escape refused.' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $artifactRoot = Join-Path $root 'artifacts'
    if (-not $output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $output -match '(?i)[\\/][^\\/]*Boot[^\\/]*([\\/]|$)') { throw 'Output must be a new directory below RepositoryRoot/artifacts, outside Boot.' }
    Assert-NoReparse $output
    if (Test-Path -LiteralPath $output) { throw 'Output collision: existing evidence is immutable; choose a new run directory.' }

    $rawInput = Get-Content -LiteralPath $EvidencePath -Raw
    $document = [Text.Json.JsonDocument]::Parse($rawInput)
    try { Assert-UniqueJsonKeys $document.RootElement } finally { $document.Dispose() }
    $inputData = $rawInput | ConvertFrom-Json -AsHashtable
    Require-Keys $inputData @('slice', 'contour', 'claimCeiling', 'affectedFiles', 'commands', 'sourceAttribution', 'tuple') @('phase', 'classification', 'requirements', 'tests', 'faults', 'owners', 'locks', 'boundaries', 'publicApiDelta', 'openExits')
    Require-Text $inputData.slice 'slice'
    Require-Text $inputData.contour 'contour'
    if ($inputData.claimCeiling -cnotin @('DevOnly', 'ModelOnly')) { throw 'Dev-only claim ceiling must be DevOnly or ModelOnly; promoted/physical/qualification claims refused.' }
    Require-Observation $inputData.sourceAttribution 'sourceAttribution'
    Require-Observation $inputData.tuple 'tuple'
    foreach ($key in @('phase', 'publicApiDelta')) { if ($inputData.Contains($key)) { Require-Text $inputData[$key] $key } }
    if ($inputData.Contains('classification') -and $inputData.classification -cnotin @('VerifiedExisting', 'Partial', 'Missing', 'Contradicted', 'ExternalBlocked', 'FutureGated')) { throw 'Unknown classification.' }
    foreach ($key in @('requirements', 'tests', 'faults', 'owners', 'locks', 'boundaries', 'openExits')) {
        if ($inputData.Contains($key)) { Require-Array $inputData[$key] $key; foreach ($value in $inputData[$key]) { Require-Text $value $key } }
    }
    Require-Array $inputData.affectedFiles 'affectedFiles'
    if ($inputData.affectedFiles.Count -eq 0) { throw 'affectedFiles cannot be empty.' }
    foreach ($file in $inputData.affectedFiles) { $null = Get-ScopedSource $file }
    Require-Array $inputData.commands 'commands'
    if ($inputData.commands.Count -eq 0) { throw 'commands cannot be empty: supply pending/unavailable explicitly.' }
    $status = 'passed'
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($command in $inputData.commands) {
        Require-Keys $command @('id', 'command', 'result', 'exitCode', 'provenance') @('counts', 'durationSeconds', 'scope')
        foreach ($key in @('id', 'command', 'provenance')) { Require-Text $command[$key] "command.$key" }
        if (-not $ids.Add($command.id)) { throw 'Duplicate command id.' }
        if ($command.result -cnotin @('passed', 'failed', 'pending', 'unavailable')) { throw 'Unknown command result.' }
        $code = $command.exitCode
        if ($null -ne $code -and $code -isnot [long] -and $code -isnot [int]) { throw 'exitCode must be an integer or null.' }
        if ($command.result -ceq 'passed' -and ($null -eq $code -or $code -ne 0)) { throw 'Passed result requires supplied exitCode 0.' }
        if ($command.result -cin @('pending', 'unavailable') -and $null -ne $code) { throw 'Unobserved result requires null exitCode.' }
        if ($command.Contains('durationSeconds') -and $null -ne $command.durationSeconds) {
            if ($command.durationSeconds -isnot [ValueType] -or $command.durationSeconds -is [bool] -or $command.durationSeconds -lt 0) { throw 'Invalid durationSeconds.' }
        }
        if ($command.Contains('scope')) { Require-Text $command.scope 'command.scope' }
        if ($command.Contains('counts') -and $null -ne $command.counts) {
            Require-Keys $command.counts @('passed', 'failed', 'skipped', 'total')
            foreach ($value in $command.counts.Values) { if (($value -isnot [long] -and $value -isnot [int]) -or $value -lt 0) { throw 'Counts must be supplied nonnegative integers or counts=null.' } }
            if ($command.counts.total -ne ($command.counts.passed + $command.counts.failed + $command.counts.skipped)) { throw 'Counts total mismatch.' }
            if ($command.result -cin @('pending', 'unavailable')) { throw 'Unobserved counts must be null or omitted.' }
            if ($command.result -ceq 'passed' -and $command.counts.failed -ne 0) { throw 'Failed counts cannot be green.' }
        }
        if ($command.result -ceq 'failed') { $status = 'failed' }
        elseif ($command.result -cin @('pending', 'unavailable') -and $status -cne 'failed') { $status = 'pending' }
    }
    $digests = @()
    foreach ($file in $SourceDigestFiles) {
        if ($file -notmatch '^(src|tests|eng|contracts|tools|sdk)[\\/]' -or $file -match '(?i)Boot|[\\/]Samples[\\/]' -or
            [IO.Path]::GetExtension($file) -cnotin @('.cs', '.ps1', '.cpp', '.c', '.h', '.rs')) { throw 'Digest opt-in accepts relevant source only; no docs, manifests, Boot or packages.' }
        $path = Get-ScopedSource $file
        $digests += [ordered]@{ path = $file; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); provenance = 'observed explicit opt-in file bytes; no stable epoch asserted' }
    }
    $audit = [ordered]@{ kind = 'DevOnly'; generatedUtc = [DateTimeOffset]::UtcNow.ToString('O'); status = $status; gates = 'OFF'; physical = 'FutureGated'; qualification = 'not established'; documentHashes = 'omitted under dev policy42'; java = 'skipped; not invoked'; isaOpcodeCpuImpact = 'NONE' }
    foreach ($key in $inputData.Keys) { $audit[$key] = $inputData[$key] }
    $audit.sourceDigests = $digests
    $audit.observationPolicy = 'Command results, source attribution and tuple are caller-supplied with provenance; collector does not execute or independently qualify them. Omitted counts/durations are unavailable.'
    $json = $audit | ConvertTo-Json -Depth 30
    $lines = @('# V6 Dev evidence', '', 'Dev-only record; no production/physical qualification or gate enablement.', '', "Slice: $($inputData.slice)", "Contour: $($inputData.contour)", "Claim ceiling: $($inputData.claimCeiling)", "Status: $status", '', 'Results (caller supplied; commands are never executed):')
    foreach ($command in $inputData.commands) {
        $exitText = if ($null -eq $command.exitCode) { 'unavailable' } else { [string]$command.exitCode }
        $lines += "- $($command.id): $($command.result); exit=$exitText; provenance=$($command.provenance)"
    }
    $lines += @('', 'Full supplied attribution, tuple, boundaries and results: audit.json.', 'Ordinary document hashes omitted under policy42. Security canonical/package digests remain with their consumers.', 'Gates OFF. Physical FutureGated. Java skipped. ISA impact NONE.')
    # Prepare privately, then publish with a non-overwriting move. Concurrent collectors
    # cannot both win; pre-existing output is never opened for writing.
    $parent = [IO.Path]::GetDirectoryName($output)
    $null = [IO.Directory]::CreateDirectory($parent)
    Assert-NoReparse $output
    $staging = Join-Path $parent ('.dev-evidence-' + [Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($staging)
    [IO.File]::WriteAllText((Join-Path $staging 'audit.json'), $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $staging 'report.md'), ($lines -join "`n"), [Text.UTF8Encoding]::new($false))
    [IO.Directory]::Move($staging, $output)
    Write-Output "Dev evidence recorded: $output ($status)"
    if ($status -ceq 'failed') { exit 1 }
    if ($status -ceq 'pending') { exit 3 }
    exit 0
} catch {
    [Console]::Error.WriteLine("Dev evidence refused: $($_.Exception.Message)")
    exit 2
}
