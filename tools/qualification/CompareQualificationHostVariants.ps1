param([string]$EvidenceSingRepository, [string]$EvidenceHybridCpuRepository)

# Host qualification only; no external compiler/runtime execution.
if ($PSVersionTable.PSVersion.Major -lt 7 -or
    ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 7)) {
    throw 'Qualification host comparison requires the user-installed PowerShell 7.7.'
}
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeAotToolchain.ps1')
. (Join-Path $PSScriptRoot 'QualificationReportComparison.ps1')
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($EvidenceSingRepository) -ne [string]::IsNullOrWhiteSpace($EvidenceHybridCpuRepository)) {
    throw 'Successful report comparison requires both evidence repository roots.'
}
$reportPath = $null
if (-not [string]::IsNullOrWhiteSpace($EvidenceSingRepository)) {
    foreach ($root in @($EvidenceSingRepository, $EvidenceHybridCpuRepository)) {
        $resolved = (Resolve-Path -LiteralPath $root).Path
        if (-not ($resolved.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $resolved.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
            throw 'Evidence repositories must stay inside SingNextOS.'
        }
    }
    $EvidenceSingRepository = (Resolve-Path -LiteralPath $EvidenceSingRepository).Path
    $EvidenceHybridCpuRepository = (Resolve-Path -LiteralPath $EvidenceHybridCpuRepository).Path
    $reportPath = Join-Path $EvidenceSingRepository 'artifacts\hybridcpu-aot-qualification\SingPlusHybridCpuQualificationV1.json'
    if (Test-Path -LiteralPath $reportPath) { throw 'Use fresh evidence output; an existing report will not be overwritten by qualification setup.' }
}
$project = Join-Path $repoRoot 'tools\SingPlus.HybridCpuQualification\SingPlus.HybridCpuQualification.csproj'
$normalOut = Join-Path $repoRoot 'tools\SingPlus.HybridCpuQualification\obj\qualification\normal'
$nativeOut = Join-Path $repoRoot 'tools\SingPlus.HybridCpuQualification\obj\qualification\aot'
$toolchain = Get-NativeAotToolchain
Assert-NativeAotToolchain $toolchain
& dotnet publish $project -c Release -r win-x64 -p:SingPlusNativeAotQualification=false -o $normalOut -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Ordinary qualification publish failed.' }
Invoke-NativeAotPublish $toolchain $project $nativeOut

function Invoke-QualificationHost {
    param([string]$Executable, [string[]]$Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $repoRoot
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Qualification host could not start.' }
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { throw 'Qualification host exceeded the 60 second limit.' }
        return @{ Exit = $process.ExitCode; StdOut = $stdout.GetAwaiter().GetResult(); StdErr = $stderr.GetAwaiter().GetResult() }
    }
    finally {
        try {
            if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(10000) }
        }
        finally { $process.Dispose() }
    }
}

$verb = 'record-external-blocked'
$cases = [ordered]@{
    Empty = @()
    UnknownVerb = @('unsupported')
    MissingValue = @($verb, '--output')
    UnknownOption = @($verb, '--unknown', 'value')
    DuplicateOption = @($verb, '--output', 'unused', '--output', 'unused')
    MissingRequired = @($verb, '--output', 'unused')
}
# All options are syntactically complete, but revision refusal precedes any repository IO.
$wrongRevision = @($verb)
foreach ($name in @('sing-repository', 'hybridcpu-repository', 'expected-hybridcpu-revision',
        'dotnet-sdk-version', 'kernel-assembly', 'boot-assembly', 'admission-proof',
        'first-pass-kernel-assembly', 'first-pass-boot-assembly', 'first-pass-admission-proof', 'output')) {
    $wrongRevision += @("--$name", $(if ($name -eq 'expected-hybridcpu-revision') { '0' * 40 } else { 'unused' }))
}
$cases.WrongRevision = $wrongRevision
foreach ($case in $cases.GetEnumerator()) {
    $ordinary = Invoke-QualificationHost 'dotnet' (@((Join-Path $normalOut 'SingPlus.HybridCpuQualification.dll')) + $case.Value)
    $native = Invoke-QualificationHost (Join-Path $nativeOut 'SingPlus.HybridCpuQualification.exe') $case.Value
    if ($ordinary.Exit -ne 64 -or $native.Exit -ne 64 -or
        $ordinary.StdOut -cne '' -or $ordinary.StdOut -cne $native.StdOut -or
        $ordinary.StdErr -ceq '' -or $ordinary.StdErr -cne $native.StdErr) {
        throw "Qualification host mismatch: $($case.Key)."
    }
    Write-Output "$($case.Key): ordinary/native exit=64; identical separate stdout/stderr diagnostics."
}
if ($null -ne $reportPath) {
    $sdk = (Get-Content -LiteralPath (Join-Path $EvidenceSingRepository 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $arguments = @($verb, '--sing-repository', $EvidenceSingRepository, '--hybridcpu-repository', $EvidenceHybridCpuRepository,
        '--expected-hybridcpu-revision', '9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9', '--dotnet-sdk-version', $sdk,
        '--kernel-assembly', (Join-Path $EvidenceSingRepository 'src\Kernel\SingPlus.Kernel\bin\Release\net11.0\SingPlus.Kernel.dll'),
        '--boot-assembly', (Join-Path $EvidenceSingRepository 'src\Kernel\Boot\SingPlus.Boot\bin\Release\net11.0\SingPlus.Boot.dll'),
        '--admission-proof', (Join-Path $EvidenceSingRepository 'artifacts\hybridcpu-aot-qualification\SingPlusAdmissionProofV1.json'),
        '--first-pass-kernel-assembly', (Join-Path $EvidenceSingRepository 'artifacts\hybridcpu-aot-qualification\pass1\SingPlus.Kernel.dll'),
        '--first-pass-boot-assembly', (Join-Path $EvidenceSingRepository 'artifacts\hybridcpu-aot-qualification\pass1\SingPlus.Boot.dll'),
        '--first-pass-admission-proof', (Join-Path $EvidenceSingRepository 'artifacts\hybridcpu-aot-qualification\pass1\SingPlusAdmissionProofV1.json'),
        '--output', $reportPath)
    $ordinary = Invoke-QualificationHost 'dotnet' (@((Join-Path $normalOut 'SingPlus.HybridCpuQualification.dll')) + $arguments)
    if ($ordinary.Exit -ne 0) { throw "Ordinary report refused (exit $($ordinary.Exit)): $($ordinary.StdErr)" }
    $ordinaryBytes = [IO.File]::ReadAllBytes($reportPath)
    $native = Invoke-QualificationHost (Join-Path $nativeOut 'SingPlus.HybridCpuQualification.exe') $arguments
    if ($native.Exit -ne 0) { throw "Native report refused (exit $($native.Exit)): $($native.StdErr)" }
    $nativeBytes = [IO.File]::ReadAllBytes($reportPath)
    $digest = Assert-QualificationReportVariants $ordinary $native $ordinaryBytes $nativeBytes
    Write-Output "Successful ExternalBlocked report comparison passed; raw canonical SHA256=$digest. This is not external compiler/ISA execution support."
}
else {
    Write-Output 'Qualification native startup and CLI refusal corpus passed; successful external-blocked report/target semantics were not qualified.'
}
