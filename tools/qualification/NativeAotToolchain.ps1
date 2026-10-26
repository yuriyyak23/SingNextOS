function Get-NativeAotToolchain {
    param(
        [string]$MsvcRoot = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\VC\Tools\MSVC\14.29.30133",
        [string]$SdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10",
        [string]$SdkVersion = '10.0.26100.0'
    )
    $requirements = [ordered]@{
        Linker = "$MsvcRoot\bin\Hostx64\x64\link.exe"
        ResourceCompiler = "$SdkRoot\bin\$SdkVersion\x64\rc.exe"
        ManifestTool = "$SdkRoot\bin\$SdkVersion\x64\mt.exe"
        MsvcLibrary = "$MsvcRoot\lib\x64\libcmt.lib"
        UcrtLibrary = "$SdkRoot\Lib\$SdkVersion\ucrt\x64\ucrt.lib"
        MsvcHeader = "$MsvcRoot\include\vcruntime.h"
        UcrtHeader = "$SdkRoot\Include\$SdkVersion\ucrt\stdio.h"
        WindowsHeader = "$SdkRoot\Include\$SdkVersion\um\Windows.h"
        SharedHeader = "$SdkRoot\Include\$SdkVersion\shared\sdkddkver.h"
    }
    # Import libraries named by the pinned .NET 11 RC Windows NativeAOT link response.
    foreach ($library in @('advapi32', 'bcrypt', 'crypt32', 'iphlpapi', 'kernel32', 'mswsock',
            'ncrypt', 'normaliz', 'ntdll', 'ole32', 'oleaut32', 'secur32', 'Synchronization',
            'user32', 'version', 'ws2_32')) {
        $requirements["WindowsLibrary.$library"] = "$SdkRoot\Lib\$SdkVersion\um\x64\$library.lib"
    }
    return @{
        Requirements = $requirements
        Linker = $requirements.Linker
        LIB = "$MsvcRoot\lib\x64;$SdkRoot\Lib\$SdkVersion\ucrt\x64;$SdkRoot\Lib\$SdkVersion\um\x64"
        INCLUDE = "$MsvcRoot\include;$SdkRoot\Include\$SdkVersion\ucrt;$SdkRoot\Include\$SdkVersion\um;$SdkRoot\Include\$SdkVersion\shared"
        ToolPath = "$MsvcRoot\bin\Hostx64\x64;$SdkRoot\bin\$SdkVersion\x64;$env:SystemRoot\System32"
    }
}

function Assert-NativeAotToolchain {
    param($Toolchain, [scriptblock]$Exists = { param($Path) Test-Path -LiteralPath $Path -PathType Leaf })
    $missing = @(foreach ($entry in $Toolchain.Requirements.GetEnumerator()) {
        if (-not (& $Exists $entry.Value)) { "$($entry.Key): $($entry.Value)" }
    })
    if ($missing.Count) { throw "NativeAOT prerequisites missing:`n$($missing -join "`n")" }
}

function Invoke-NativeAotPublish {
    param($Toolchain, [string]$Project, [string]$Output)
    Assert-NativeAotToolchain $Toolchain
    $saved = @{}
    try {
        foreach ($name in @('LIB', 'INCLUDE', 'PATH')) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
        $env:LIB = $Toolchain.LIB
        $env:INCLUDE = $Toolchain.INCLUDE
        $env:PATH = "$($Toolchain.ToolPath);$($saved.PATH)"
        & dotnet publish $Project -c Release -r win-x64 '-p:SingPlusNativeAotQualification=true' '-p:IlcUseEnvironmentalTools=true' "-p:CppLinker=$($Toolchain.Linker)" -o $Output '-v:quiet'
        if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed (exit $LASTEXITCODE); explicit prerequisites passed validation." }
    }
    finally {
        foreach ($name in $saved.Keys) {
            if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        }
    }
}
