[CmdletBinding()]
param([string]$WorkDir,[string]$OutputDirectory,[switch]$SkipBuild)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'trust/development'}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Identity already exists; initialization never replaces signing keys.'}
$project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
if(!$SkipBuild){
    & dotnet build $project -c Release --nologo | ForEach-Object {Write-Verbose $_}
    if($LASTEXITCODE -ne 0){throw 'CXL Lab build failed.'}
}
$dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
& dotnet $dll identity-init --output ([IO.Path]::GetFullPath($OutputDirectory))
if($LASTEXITCODE -ne 0){throw 'Development identity initialization failed.'}
