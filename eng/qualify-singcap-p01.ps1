[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'SingNextOS.slnx'
$fixture = Join-Path $repositoryRoot 'tests/fixtures/SingCap.ManagedCap.NativeAotFixture/SingCap.ManagedCap.NativeAotFixture.csproj'

function Invoke-NativeAotPublish {
    param([string]$Project, [string]$Rid, [string]$OutputRoot)
    if (-not $IsWindows) {
        dotnet publish $Project -c Release -r $Rid --self-contained true --nologo -v:minimal -p:BaseOutputPath=$OutputRoot -p:SingPlusAdmissionToolBaseOutputPath=$OutputRoot
        return
    }
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe is required for the Windows NativeAOT lane.' }
    $installation = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -latest -property installationPath
    if (-not $installation) { throw 'Visual C++ x64 build tools are required for the Windows NativeAOT lane.' }
    $vsdev = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
    $link = Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Tools\MSVC') -Filter link.exe -Recurse |
        Where-Object { $_.FullName -match '\\bin\\Hostx64\\x64\\link\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $link) { throw 'Visual C++ x64 linker was not found.' }
    $script:nativeLinkerVersion = $link.VersionInfo.FileVersion.Split(' ')[0]
    $command = 'call "{0}" -arch=x64 -host_arch=x64 && set "PATH=C:\Windows\System32;C:\Program Files\dotnet;{1};%PATH%" && "C:\Program Files\dotnet\dotnet.exe" publish "{2}" -c Release -r {3} --self-contained true --nologo -v:minimal -p:BaseOutputPath={4} -p:SingPlusAdmissionToolBaseOutputPath={4}' -f $vsdev, $link.DirectoryName, $Project, $Rid, $OutputRoot
    & cmd.exe /d /c $command
}

Push-Location $repositoryRoot
try {
    dotnet test 'tests/SingPlus.Tests/SingPlus.Tests.csproj' --filter 'FullyQualifiedName~SingCapPhase01ToolchainTests' --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'P01 toolchain/profile gates failed.' }

    dotnet build $solution --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Default-language solution build failed.' }

    dotnet build $solution -p:SingPlusPreviewLanguage=true --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Preview-language qualification build failed.' }

    $nativeAotOutput = 'obj/singcap-p01-nativeaot/' + [Guid]::NewGuid().ToString('N') + '/'
    Invoke-NativeAotPublish -Project $fixture -Rid $RuntimeIdentifier -OutputRoot $nativeAotOutput
    if ($LASTEXITCODE -ne 0) { throw 'ManagedCap NativeAOT fixture publish failed.' }

    $golden = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'eng/singcap-toolchain-v1.json') | ConvertFrom-Json
    $assets = Get-Content -Raw -LiteralPath (Join-Path (Split-Path -Parent $fixture) 'obj/project.assets.json') | ConvertFrom-Json
    $compilerIdentities = @($assets.libraries.PSObject.Properties.Name |
        Where-Object { $_ -like 'Microsoft.DotNet.ILCompiler/*' })
    if ($compilerIdentities.Count -ne 1) { throw 'NativeAOT assets must contain exactly one ILCompiler identity.' }
    $compilerIdentity = $compilerIdentities[0]
    if ($compilerIdentity.Split('/')[1] -ne $golden.nativeAotCompilerVersion) {
        throw "NativeAOT compiler drift: expected $($golden.nativeAotCompilerVersion), got $compilerIdentity."
    }
    if ($IsWindows) {
        if ($script:nativeLinkerVersion -ne $golden.windowsNativeLinkerFileVersion) {
            throw "Native linker drift: expected $($golden.windowsNativeLinkerFileVersion), got $($script:nativeLinkerVersion)."
        }
    }
}
finally {
    Pop-Location
}
