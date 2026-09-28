[CmdletBinding()]
param(
    [string]$QemuPath = 'qemu-system-x86_64',
    [string]$BootImage,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$checks = [ordered]@{
    qemuExecutable = $false
    qemuStarts = $false
    cxlHostBridge = $false
    cxlRootPort = $false
    cxlType3 = $false
    bootImage = $false
}
$details = [ordered]@{}

try {
    $resolved = Get-Command -Name $QemuPath -ErrorAction Stop
    $executable = $resolved.Source
    $checks.qemuExecutable = $true
    $details.qemuPath = $executable

    $versionOutput = & $executable '--version' 2>&1
    $versionExit = $LASTEXITCODE
    $checks.qemuStarts = $versionExit -eq 0
    $details.versionExitCode = $versionExit
    $details.version = ($versionOutput | Select-Object -First 1) -as [string]

    if ($checks.qemuStarts) {
        $deviceOutput = (& $executable '-device' 'help' 2>&1) -join "`n"
        $checks.cxlHostBridge = $deviceOutput -match '(?im)\bpxb-cxl\b'
        $checks.cxlRootPort = $deviceOutput -match '(?im)\bcxl-rp\b'
        $checks.cxlType3 = $deviceOutput -match '(?im)\bcxl-type3\b'
    }
}
catch {
    $details.qemuError = $_.Exception.Message
}

if (-not [string]::IsNullOrWhiteSpace($BootImage)) {
    $resolvedImage = Resolve-Path -LiteralPath $BootImage -ErrorAction SilentlyContinue
    $checks.bootImage = $null -ne $resolvedImage -and -not (Get-Item -LiteralPath $resolvedImage).PSIsContainer
    if ($resolvedImage) { $details.bootImagePath = $resolvedImage.Path }
}
else {
    $details.bootImageError = 'A boot image path is required for executable qualification.'
}

$ready = -not ($checks.Values -contains $false)
$report = [ordered]@{
    schemaVersion = 1
    ready = $ready
    checks = $checks
    details = $details
}

if ($Json) {
    $report | ConvertTo-Json -Depth 4
}
else {
    $report.GetEnumerator() | Format-List
}

if (-not $ready) { exit 2 }
