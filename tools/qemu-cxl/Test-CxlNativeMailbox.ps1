[CmdletBinding()]
param([string]$WorkDir,[string]$OutputDirectory,[string]$EfiImage,[ValidateRange(4096,8192)][int]$RamMiB=4096,[ValidateRange(8,240)][int]$CxlBus=12,[ValidateRange(0,8)][int]$EduCount=0,[switch]$SkipBuild)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('logs/native-mailbox-'+[guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Native mailbox output must be new.'}
$project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
$dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
if(!$SkipBuild){& dotnet build $project -c Release --nologo|ForEach-Object {Write-Verbose $_};if($LASTEXITCODE){throw 'CXL Lab build failed.'}}
if(!$EfiImage){
    $build=Join-Path $root ('logs/native-mailbox-build-'+[guid]::NewGuid().ToString('N'))
    & "$PSScriptRoot/Build-CxlEfiDiscovery.ps1" -NativeMailbox -OutputDirectory $build|ForEach-Object {Write-Verbose $_}
    $EfiImage=Join-Path $build 'CXLMBX.EFI'
}
$arguments=@($dll,'native-mailbox','--config',(Join-Path $root 'config/qemu-cxl.json'),'--output',$OutputDirectory,'--efi-image',(Resolve-Path -LiteralPath $EfiImage).Path,'--timeout-seconds','240','--ram-mib',"$RamMiB",'--cxl-bus',"$CxlBus",'--edu-count',"$EduCount")
& dotnet @arguments|ForEach-Object {Write-Verbose $_}
if($LASTEXITCODE){throw "Native mailbox failed: $OutputDirectory"}
$report=Get-Content (Join-Path $OutputDirectory 'result.json') -Raw|ConvertFrom-Json
if(!$report.Passed){throw 'Mailbox qualification did not pass.'}
$report|Add-Member -NotePropertyName EvidenceDirectory -NotePropertyValue $OutputDirectory -PassThru
