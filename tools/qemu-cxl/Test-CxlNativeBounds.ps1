[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CapsuleRunDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$WorkDir)
. "$PSScriptRoot/Common.ps1"
$root=Get-Root $WorkDir
$CapsuleRunDirectory=(Resolve-Path -LiteralPath $CapsuleRunDirectory).Path
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Bounds report output must be new.'}
$capsule=Join-Path $CapsuleRunDirectory 'fixture/capsule.bin'
$efi=Join-Path $CapsuleRunDirectory 'efi-build/CXLHDM.EFI'
$metadata=Get-Content (Join-Path $CapsuleRunDirectory 'efi-build/build.json') -Raw|ConvertFrom-Json
$bytes=(Get-Item -LiteralPath $capsule).Length
$offset=[UInt64](268435456-$bytes)
if($bytes -lt 1 -or $bytes -gt 73728){throw 'Invalid bounded capsule input.'}
if((Get-FileHash -LiteralPath $capsule).Hash -ne $metadata.ExpectedImageSha256 -or (Get-FileHash -LiteralPath $efi).Hash -ne $metadata.EfiSha256){throw 'Bounds input hashes differ from build metadata.'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$checks=@()
foreach($case in @(@{Name='builder-one-byte';Offset=$offset+1},@{Name='builder-overflow';Offset=[UInt64]::MaxValue})){
    $target=Join-Path $OutputDirectory $case.Name;$rejected=$false;$message=''
    try{& "$PSScriptRoot/Build-CxlEfiReader.ps1" -NativeHdmCapsule -RamCopy -ExpectedImage $capsule -TrustRoot (Join-Path $CapsuleRunDirectory 'fixture/development-trust-root.bin') -CompilerPath $metadata.Compiler -CxlBase 0 -Offset $case.Offset -OutputDirectory $target|Out-Null}
    catch{$message=$_.Exception.Message;$rejected=$message -eq 'EFI reader range invalid.'}
    $checks+=[pscustomobject]@{Name=$case.Name;Rejected=$rejected;OutputNotCreated=!(Test-Path -LiteralPath $target);Message=$message}
}
$dll=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/bin/Release/net11.0/SingNext.CxlLab.dll'
foreach($case in @(@{Name='runner-one-byte';Offset=($offset+1).ToString([Globalization.CultureInfo]::InvariantCulture)},@{Name='runner-overflow';Offset='18446744073709551615'})){
    $target=Join-Path $OutputDirectory $case.Name
    $arguments=@($dll,'native-hdm','--config',(Join-Path $root 'config/qemu-cxl.json'),'--output',$target,'--efi-image',$efi,'--capsule-file',$capsule,'--capsule-offset',$case.Offset)
    $message=(& dotnet @arguments 2>&1|Out-String).Trim();$failed=$LASTEXITCODE -ne 0
    $checks+=[pscustomobject]@{Name=$case.Name;Rejected=$failed -and ($message -match 'outside backing|exceeds backing capacity');OutputNotCreated=!(Test-Path -LiteralPath $target);Message=$message}
}
$passed=@($checks|Where-Object {!$_.Rejected -or !$_.OutputNotCreated}).Count -eq 0
$report=[pscustomobject]@{Scope='NativeHostBoundsRejection';Passed=$passed;Cases=$checks;StartsGuestVm=$false;CapsuleOffsetBytes=$offset;CapsuleImageBytes=$bytes;EvidenceDirectory=$OutputDirectory}
$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $OutputDirectory 'result.json')
if(!$passed){throw "Native bounds rejection failed: $OutputDirectory"}
$report
