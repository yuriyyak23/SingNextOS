[CmdletBinding()]
param([string]$WorkDir,[string]$OutputDirectory,[string]$EfiImage,[string]$CapsulePath,[ValidateRange(4096,8192)][int]$RamMiB=4096,[ValidateRange(8,240)][int]$CxlBus=12,[ValidateRange(0,8)][int]$EduCount=0,[switch]$SkipBuild,[switch]$ExpectCapsuleRejection,[switch]$NativeRamCopy,[ValidateSet('Valid','CopyFailure','InfoFailure','AllocationFailure','ResetWhileHeld','PartialRetire','ObserverTimeout','PartialCommit','ClearFailure')][string]$NativeRamMode='Valid',[ValidateSet('Missing','Truncated','ExtraByte')][string]$ExpectedFileFailure,[UInt64]$CapsuleOffset=0,[switch]$ExpectMailboxGateRejection)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$config=Join-Path $root 'config/qemu-cxl.json'
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('logs/native-hdm-'+[guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Native HDM output must be new.'}
$project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
$dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
if(!$SkipBuild){& dotnet build $project -c Release --nologo|ForEach-Object {Write-Verbose $_};if($LASTEXITCODE){throw 'CXL Lab build failed.'}}
if(!$EfiImage){
    $build=Join-Path $root ('logs/native-hdm-build-'+[guid]::NewGuid().ToString('N'))
    & "$PSScriptRoot/Build-CxlEfiDiscovery.ps1" -NativeHdmLab -OutputDirectory $build|ForEach-Object {Write-Verbose $_}
    $EfiImage=Join-Path $build 'CXLHDM.EFI'
}
$EfiImage=(Resolve-Path -LiteralPath $EfiImage).Path
$arguments=@($dll,'native-hdm','--config',$config,'--output',$OutputDirectory,'--efi-image',$EfiImage,'--timeout-seconds','240','--ram-mib',"$RamMiB",'--cxl-bus',"$CxlBus",'--edu-count',"$EduCount")
if($CapsulePath){$arguments+=@('--capsule-file',(Resolve-Path -LiteralPath $CapsulePath).Path)}
if($ExpectCapsuleRejection){$arguments+=@('--expect-rejection','true')}
if($NativeRamCopy){$arguments+=@('--native-ram','true')}
if($NativeRamMode -ne 'Valid' -and !$NativeRamCopy){throw 'Native RAM failure mode requires NativeRamCopy.'}
if($NativeRamCopy){$modes=@{Valid='--ram-copy';CopyFailure='--ram-copy-fail-copy';InfoFailure='--ram-copy-fail-info';AllocationFailure='--ram-copy-fail-alloc';ResetWhileHeld='--ram-copy-reset-held';PartialRetire='--ram-copy-fail-retire';ObserverTimeout='--ram-copy-no-ack';PartialCommit='--ram-copy-fail-commit';ClearFailure='--ram-copy-fail-clear'};$arguments+=@('--native-ram-mode',$modes[$NativeRamMode])}
if($ExpectedFileFailure){$arguments+=@('--file-failure',$ExpectedFileFailure)}
$arguments+=@('--capsule-offset',$CapsuleOffset.ToString([Globalization.CultureInfo]::InvariantCulture))
if($ExpectMailboxGateRejection){$arguments+=@('--expect-mailbox-rejection','true')}
& dotnet @arguments|ForEach-Object {Write-Verbose $_}
if($LASTEXITCODE){throw "Native HDM diagnostic failed: $OutputDirectory"}
$report=Get-Content (Join-Path $OutputDirectory 'result.json') -Raw|ConvertFrom-Json
if(!$report.Passed){throw 'Native HDM did not pass.'}
$report|Add-Member -NotePropertyName EvidenceDirectory -NotePropertyValue $OutputDirectory -PassThru
