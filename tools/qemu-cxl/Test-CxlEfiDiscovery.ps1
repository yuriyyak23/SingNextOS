[CmdletBinding()]
param(
    [string]$WorkDir,[string]$OutputDirectory,[string]$EfiImage,
    [ValidateRange(4096,8192)][int]$RamMiB=4096,
    [ValidateRange(8,240)][int]$CxlBus=12,
    [ValidateRange(0,8)][int]$EduCount=0,
    [ValidateRange(60,600)][int]$TimeoutSeconds=180,
    [switch]$SkipBuild,[switch]$Json
)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$config=Join-Path $root 'config/qemu-cxl.json'
if(!(Test-Path -LiteralPath $config)){throw 'Initialize the CXL environment first.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('logs/efi-discovery-'+[guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Diagnostic output must be new.'}
$project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
$dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
if(!$SkipBuild){& dotnet build $project -c Release --nologo | ForEach-Object {Write-Verbose $_};if($LASTEXITCODE -ne 0){throw 'CXL Lab build failed.'}}
if($EduCount){
    $qemu=(Get-Content -LiteralPath $config -Raw|ConvertFrom-Json).QemuPath
    & $qemu -device edu,help 2>&1 | ForEach-Object {Write-Verbose $_.ToString()}
    if($LASTEXITCODE -ne 0){throw 'Local QEMU does not support requested EDU BAR consumers.'}
}
if(!$EfiImage){
    $build=Join-Path $root ('logs/efi-discovery-build-'+[guid]::NewGuid().ToString('N'))
    & "$PSScriptRoot/Build-CxlEfiDiscovery.ps1" -OutputDirectory $build | ForEach-Object {Write-Verbose $_}
    $EfiImage=Join-Path $build 'CXLDISC.EFI'
}
$EfiImage=(Resolve-Path -LiteralPath $EfiImage).Path
$arguments=@($dll,'pci-diagnostic','--config',$config,'--output',$OutputDirectory,'--efi-image',$EfiImage,'--ram-mib',"$RamMiB",'--cxl-bus',"$CxlBus",'--edu-count',"$EduCount",'--timeout-seconds',"$TimeoutSeconds")
& dotnet @arguments | ForEach-Object {Write-Verbose $_}
if($LASTEXITCODE -ne 0){throw "Read-only EFI discovery failed: $OutputDirectory"}
$path=Join-Path $OutputDirectory 'result.json'
if($Json){Get-Content -LiteralPath $path -Raw}else{
    $result=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    $result | Add-Member -NotePropertyName EvidenceDirectory -NotePropertyValue $OutputDirectory -PassThru
}
