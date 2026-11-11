param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../SingPlus.Boot.csproj'
$dll = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../bin/Debug/net11.0/SingPlus.Boot.dll'))
if (!$NoBuild) {
    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'Boot build failed.' }
}
function Check-Run([string[]]$Arguments, [int]$Expected, [string[]]$Evidence) {
    $output = & dotnet $dll @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    if ($code -ne $Expected) { throw "Expected exit $Expected, got $code. $output" }
    foreach ($text in $Evidence) {
        if (!$output.Contains($text)) { throw "Missing evidence '$text'. $output" }
    }
    if ($output.Contains('shutdown incomplete')) { throw "Incomplete cleanup. $output" }
}
Check-Run @('--no-shell') 0 @('host-debug runtime ready')
Check-Run @('--help') 0 @('hosted-service')
Check-Run @('--demo', 'hosted-service,generated-sip', '--budget', '--trace', '--no-shell') 0 @("'hosted-service' passed", "'generated-sip' passed")
Check-Run @('--script', (Join-Path $PSScriptRoot 'hosted-counter.txt'), '--budget', '--no-shell') 0 @('41 -> 42', '-2 -> -1', '100 -> 101', '0 cooperative generated SIP')
$cases = @(
    @{ Commands = @('counter-start auto', 'counter-call auto 7', 'exit'); Exit = 0; Evidence = @('7 -> 8', 'hosted application auto stopped and reclaimed') },
    @{ Commands = @('counter-start auto', 'counter-start auto'); Exit = 1; Evidence = @('Application name must be unique', 'hosted application auto stopped and reclaimed') },
    @{ Commands = @('counter-start auto', 'counter-call auto 2147483647'); Exit = 1; Evidence = @('Increment would overflow', 'hosted application auto stopped and reclaimed') },
    @{ Commands = @('counter-call absent 1'); Exit = 1; Evidence = @('Unknown hosted application') },
    @{ Commands = @('counter-start auto', 'revoke 1', 'counter-call auto 1'); Exit = 1; Evidence = @('CapabilityRevoked', 'hosted application auto stopped and reclaimed') },
    @{ Commands = @('counter-start auto', 'stop 5001', 'counter-stop auto', 'counters', 'exit'); Exit = 0; Evidence = @('0 cooperative generated SIP') },
    @{ Commands = @('counter-start auto', 'stop 5000', 'counter-stop auto', 'counters', 'exit'); Exit = 0; Evidence = @('0 cooperative generated SIP') }
)
foreach ($case in $cases) {
    $temporary = Join-Path $PSScriptRoot ("counter-smoke-" + [Guid]::NewGuid().ToString('N') + '.txt')
    try {
        [IO.File]::WriteAllText($temporary, ($case.Commands -join [Environment]::NewLine))
        Check-Run @('--script', $temporary, '--no-shell') $case.Exit $case.Evidence
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
Write-Host 'Hosted counter smoke checks passed.'