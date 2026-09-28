param([string]$RuntimeIdentifier = 'win-x64', [switch]$OrdinaryOnly,
    [ValidateRange(1, 600)][int]$AdmissionTimeoutSeconds = 60)

if ($PSVersionTable.PSVersion.Major -lt 7 -or
    ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 7)) {
    throw 'Admission host-variant qualification requires PowerShell 7.7; select the user-installed WindowsApps/pwsh.exe explicitly.'
}
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeAotToolchain.ps1')
if (-not $OrdinaryOnly) {
    if ($RuntimeIdentifier -ne 'win-x64') { throw 'Explicit MSVC qualification supports win-x64 only.' }
    $toolchain = Get-NativeAotToolchain
    Assert-NativeAotToolchain $toolchain
    Write-Output "NativeAOT prerequisites validated; linker: $($toolchain.Linker)"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repoRoot 'tools\SingPlus.Admission\SingPlus.Admission.csproj'
$kernelProject = Join-Path $repoRoot 'src\Kernel\SingPlus.Kernel\SingPlus.Kernel.csproj'
$normalOut = Join-Path $repoRoot 'tools\SingPlus.Admission\obj\qualification\normal'
$aotOut = Join-Path $repoRoot 'tools\SingPlus.Admission\obj\qualification\aot'
$corpus = Join-Path $repoRoot 'src\Kernel\SingPlus.Kernel\bin\Release\net11.0\SingPlus.Kernel.dll'
$proofNormal = Join-Path $normalOut 'proof.json'
$proofAot = Join-Path $aotOut 'proof.json'
$proofBaseline = Join-Path $normalOut 'proof-baseline.json'
$rejectedCorpus = Join-Path $repoRoot 'tools\SingPlus.Admission\bin\Release\net11.0\SingPlus.Admission.dll'

function Invoke-AdmissionVariant {
    param([string]$Executable, [string[]]$PrefixArguments, [string]$Assembly,
        [string]$Root, [string]$Proof)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $repoRoot
    foreach ($argument in $PrefixArguments + @('verify', '--assembly', $Assembly, '--root', $Root,
            '--profile', 'KernelNoHeap', '--proof', $Proof)) {
        [void]$start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Admission variant could not start.' }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($AdmissionTimeoutSeconds * 1000)) {
            throw "Admission variant exceeded the $AdmissionTimeoutSeconds second qualification limit."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return @{ ExitCode = $process.ExitCode; StdOut = $stdout; StdErr = $stderr }
    }
    finally {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                [void]$process.WaitForExit(10000)
            }
        }
        finally { $process.Dispose() }
    }
}

function Assert-SameVariantEvidence {
    param([string]$Assembly, [string]$Root, [string]$Label, [int]$ExpectedExit)
    $ordinary = Invoke-AdmissionVariant -Executable 'dotnet' -PrefixArguments @((Join-Path $normalOut 'SingPlus.Admission.dll')) -Assembly $Assembly -Root $Root -Proof $proofNormal
    $native = Invoke-AdmissionVariant -Executable (Join-Path $aotOut 'SingPlus.Admission.exe') -PrefixArguments @() -Assembly $Assembly -Root $Root -Proof $proofAot
    if ($ordinary.ExitCode -ne $ExpectedExit -or $native.ExitCode -ne $ExpectedExit) {
        throw "${Label}: variant exit dispositions differ from expected $ExpectedExit."
    }
    if ($ordinary.StdOut -cne $native.StdOut -or $ordinary.StdErr -cne $native.StdErr) {
        throw "${Label}: host variants produced different diagnostics."
    }
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($proofNormal)) -cne
        [Convert]::ToHexString([IO.File]::ReadAllBytes($proofAot))) {
        throw "${Label}: host variants produced different canonical proof bytes."
    }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($proofNormal))).ToLowerInvariant()
    Write-Output "${Label}: ordinary/native exit=$ExpectedExit; stdout/stderr and raw canonical proof bytes identical; SHA256=$digest"
}

function Assert-SameOrdinaryEvidence {
    param([string]$Assembly, [string]$Root, [string]$Label, [int]$ExpectedExit)
    $built = Invoke-AdmissionVariant -Executable 'dotnet' -PrefixArguments @((Join-Path $repoRoot 'tools\SingPlus.Admission\bin\Release\net11.0\SingPlus.Admission.dll')) -Assembly $Assembly -Root $Root -Proof $proofBaseline
    $published = Invoke-AdmissionVariant -Executable 'dotnet' -PrefixArguments @((Join-Path $normalOut 'SingPlus.Admission.dll')) -Assembly $Assembly -Root $Root -Proof $proofNormal
    if ($built.ExitCode -ne $ExpectedExit -or $published.ExitCode -ne $ExpectedExit) {
        throw "${Label}: ordinary host exit dispositions differ from expected $ExpectedExit."
    }
    if ($built.StdOut -cne $published.StdOut -or $built.StdErr -cne $published.StdErr) {
        throw "${Label}: ordinary host diagnostics differ between build and publish."
    }
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($proofBaseline)) -cne
        [Convert]::ToHexString([IO.File]::ReadAllBytes($proofNormal))) {
        throw "${Label}: ordinary host canonical proof bytes differ between build and publish."
    }
}

& dotnet build $kernelProject -c Release -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Kernel corpus build failed.' }
& dotnet build $project -c Release -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Rejected host-tool corpus build failed.' }
& dotnet publish $project -c Release -r $RuntimeIdentifier -p:SingPlusNativeAotQualification=false -o $normalOut -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Normal admission publish failed.' }

Assert-SameOrdinaryEvidence $corpus 'SingPlus.Kernel.KernelEntryPoint::Run' 'AdmittedKernel' 0
Assert-SameOrdinaryEvidence $rejectedCorpus 'SingPlus.Admission.AdmissionTelemetry::Observe' 'RejectedHostTool' 2
if ($OrdinaryOnly) {
    Write-Output 'Ordinary Admission build/publish variants produced identical admitted/rejected diagnostics and canonical proof bytes. NativeAOT comparison was not run.'
    return
}

Invoke-NativeAotPublish -Toolchain $toolchain -Project $project -Output $aotOut

Assert-SameVariantEvidence $corpus 'SingPlus.Kernel.KernelEntryPoint::Run' 'AdmittedKernel' 0
Assert-SameVariantEvidence $rejectedCorpus 'SingPlus.Admission.AdmissionTelemetry::Observe' 'RejectedHostTool' 2
Write-Output 'Admission host variants produced identical admitted/rejected diagnostics and canonical proof bytes.'
