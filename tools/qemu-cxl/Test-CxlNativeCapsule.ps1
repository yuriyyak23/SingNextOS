[CmdletBinding()]
param([string]$WorkDir,[string]$OutputDirectory,[switch]$WithRelocation,[switch]$WithRamPreparation,[switch]$WithRamFaults,[switch]$WithFileFailures,[string]$PayloadPath,[UInt64]$CapsuleOffset=0,[switch]$AtBackingEnd,[switch]$WithRamReset,[switch]$WithMailboxGate)
. "$PSScriptRoot/Common.ps1"
if($WithRamReset -and !$WithRamPreparation){throw 'WithRamReset requires WithRamPreparation.'}
if($WithRamFaults -and !$WithRamPreparation){throw 'WithRamFaults requires WithRamPreparation.'}
$root=Get-Root $WorkDir
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('logs/native-capsule-'+[guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Native capsule output must be new.'}
$project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
& dotnet build $project -c Release --nologo|ForEach-Object {Write-Verbose $_}
if($LASTEXITCODE){throw 'CXL Lab build failed.'}
$dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
$fixture=Join-Path $OutputDirectory 'fixture'
$createArguments=@($dll,'create','--output',$fixture)
if($PayloadPath){$createArguments+=@('--payload',(Resolve-Path -LiteralPath $PayloadPath).Path)}
& dotnet @createArguments|ForEach-Object {Write-Verbose $_}
if($LASTEXITCODE){throw 'Fixture capsule creation failed.'}
$capsule=Join-Path $fixture 'capsule.bin'
$imageLength=(Get-Item -LiteralPath $capsule).Length
if($AtBackingEnd){if($CapsuleOffset -ne 0){throw 'AtBackingEnd and explicit offset are mutually exclusive.'};$CapsuleOffset=[UInt64](268435456-$imageLength)}
if($CapsuleOffset -gt 268435456 -or $imageLength -gt 268435456-$CapsuleOffset){throw 'CapsuleOffset + CapsuleImageBytes exceeds backing.'}
$build=Join-Path $OutputDirectory 'efi-build'
& "$PSScriptRoot/Build-CxlEfiReader.ps1" -NativeHdmCapsule -MailboxGate:$WithMailboxGate -RamCopy:$WithRamPreparation -ExpectedImage $capsule -TrustRoot (Join-Path $fixture 'development-trust-root.bin') -CxlBase 0 -Offset $CapsuleOffset -OutputDirectory $build|ForEach-Object {Write-Verbose $_}
$efi=Join-Path $build 'CXLHDM.EFI'
$argsCommon=@{WorkDir=$root;SkipBuild=$true;EfiImage=$efi;NativeRamCopy=[bool]$WithRamPreparation;CapsuleOffset=$CapsuleOffset}
$positive=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" @argsCommon -CapsulePath $capsule -OutputDirectory (Join-Path $OutputDirectory 'valid')
$bytes=[IO.File]::ReadAllBytes($capsule)
$bytes[6144]=$bytes[6144] -bxor 1
$bad=Join-Path $OutputDirectory 'bad-signature.bin';[IO.File]::WriteAllBytes($bad,$bytes)
$negative=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" @argsCommon -ExpectCapsuleRejection -CapsulePath $bad -OutputDirectory (Join-Path $OutputDirectory 'bad-signature')
$relocated=$null
if($WithRelocation){$relocated=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" @argsCommon -CapsulePath $capsule -RamMiB 6144 -CxlBus 24 -EduCount 4 -OutputDirectory (Join-Path $OutputDirectory 'relocated')}
$passed=$positive.Passed -and $positive.GuestSignatureVerification -and $negative.Passed -and $negative.GuestDamagedCapsuleRejected
if($WithRelocation){$passed=$passed -and $relocated.Passed -and $relocated.GuestSignatureVerification -and ($positive.EfiSha256 -eq $relocated.EfiSha256)}
if($WithRamPreparation){$passed=$passed -and $positive.NativeRamHeldAfterCxlRetireVerified; if($WithRelocation){$passed=$passed -and $relocated.NativeRamHeldAfterCxlRetireVerified}}
$ramFaults=@()
if($WithRamFaults){foreach($mode in @('CopyFailure','InfoFailure','AllocationFailure')){$case=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" @argsCommon -NativeRamMode $mode -CapsulePath $capsule -OutputDirectory (Join-Path $OutputDirectory $mode);$ramFaults+=,$case;$passed=$passed -and $case.Passed -and $case.GuestRamFailureRejected -and $case.GuestRamFailureCleanupObserved}}
$fileFailures=@()
if($WithFileFailures){
    $original=[IO.File]::ReadAllBytes($capsule)
    foreach($kind in @('Missing','Truncated','ExtraByte')){
        $fileArgs=@{}
        if($kind -ne 'Missing'){
            $length=if($kind -eq 'Truncated'){$original.Length-1}else{$original.Length+1}
            $data=New-Object byte[] $length;[Array]::Copy($original,$data,[Math]::Min($original.Length,$length))
            if($kind -eq 'ExtraByte'){$data[$length-1]=255}
            $path=Join-Path $OutputDirectory ($kind+'.bin');[IO.File]::WriteAllBytes($path,$data);$fileArgs.CapsulePath=$path
        }
        $case=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" @argsCommon @fileArgs -ExpectedFileFailure $kind -OutputDirectory (Join-Path $OutputDirectory $kind)
        $fileFailures+=,$case;$passed=$passed -and $case.Passed -and $case.GuestFileInputRejected -and $case.GuestNativeHdmFinalRetireVerified
    }
}
$ramReset=$null
if($WithRamReset){$ramReset=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" @argsCommon -NativeRamMode ResetWhileHeld -CapsulePath $capsule -OutputDirectory (Join-Path $OutputDirectory 'reset-held');$passed=$passed -and $ramReset.Passed -and $ramReset.RealResetObserved -and $ramReset.OldRamWitnessRejected -and $ramReset.HostRamObservationQuarantined}
$mailboxMismatch=$null
if($WithMailboxGate){
    $mismatchBuild=Join-Path $OutputDirectory 'mismatch-efi-build'
    & "$PSScriptRoot/Build-CxlEfiReader.ps1" -NativeHdmCapsule -MailboxGate -ExpectedPmemMiB 512 -RamCopy:$WithRamPreparation -ExpectedImage $capsule -TrustRoot (Join-Path $fixture 'development-trust-root.bin') -CxlBase 0 -Offset $CapsuleOffset -OutputDirectory $mismatchBuild|ForEach-Object {Write-Verbose $_}
    $mailboxMismatch=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" -WorkDir $root -SkipBuild -EfiImage (Join-Path $mismatchBuild 'CXLHDM.EFI') -CapsulePath $capsule -CapsuleOffset $CapsuleOffset -ExpectMailboxGateRejection -OutputDirectory (Join-Path $OutputDirectory 'capacity-mismatch')
    $passed=$passed -and $positive.NativeMailboxCapacityGatePassed -and $mailboxMismatch.Passed -and $mailboxMismatch.MailboxMismatchRejectedBeforeHdm
    if($WithRelocation){$passed=$passed -and $relocated.NativeMailboxCapacityGatePassed}
}
$summary=[pscustomobject]@{Scope='NativeCapsuleLabQualification';Passed=[bool]$passed;CapsuleOffsetBytes=$CapsuleOffset;CapsuleImageBytes=$imageLength;CxlBackingBytes=268435456;Positive=$positive;DamagedSignature=$negative;Relocated=$relocated;RamFaults=$ramFaults;FileFailures=$fileFailures;RamReset=$ramReset;MailboxMismatch=$mailboxMismatch;RuntimeKernelExecuted=$false;BootHandoffPerformed=$false;EvidenceDirectory=$OutputDirectory}
$summary|ConvertTo-Json -Depth 8|Set-Content (Join-Path $OutputDirectory 'summary.json')
if(!$passed){throw "Native capsule qualification failed: $OutputDirectory"}
$summary
