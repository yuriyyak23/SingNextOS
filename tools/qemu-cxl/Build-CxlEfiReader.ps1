[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ExpectedImage,
    [Parameter(Mandatory=$true)][UInt64]$CxlBase,
    [UInt64]$Offset=0,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$TrustRoot,
    [UInt64]$MinImageGeneration=1,
    [switch]$RamCopy,
    [switch]$NativeHdmCapsule,
    [switch]$MailboxGate,
    [ValidateRange(256,4096)][int]$ExpectedPmemMiB=256,
    [string]$LifecyclePublicKey,
    [string]$LifecycleDmaLayout,
    [switch]$LifecycleMapping
)
$ErrorActionPreference='Stop'
if($RamCopy -and !$TrustRoot){throw 'RAM stage requires guest crypto and a trust root.'}
if($NativeHdmCapsule -and !$TrustRoot){throw 'Native capsule profile requires crypto.'}
if($MailboxGate -and !$NativeHdmCapsule){throw 'MailboxGate requires NativeHdmCapsule.'}
if($LifecyclePublicKey -and (!$NativeHdmCapsule -or !$RamCopy -or !$MailboxGate)){throw 'Lifecycle lab requires existing native RAM/mailbox composition.'}
if($LifecycleDmaLayout -and (!$LifecyclePublicKey -or !(Test-Path -LiteralPath $LifecycleDmaLayout))){throw 'Isolation profile requires pinned lifecycle key and existing DMA layout header.'}
if($LifecycleMapping -and !$LifecycleDmaLayout){throw 'Mapping profile requires the existing isolation profile.'}
if($ExpectedPmemMiB -ne 256 -and !$MailboxGate){throw 'ExpectedPmemMiB requires MailboxGate.'}
# A PowerShell 5.1 child may inherit PowerShell 7's PSModulePath.
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'EFI build output must be new.'}
if(!$CompilerPath){
    $base=Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio'
    $CompilerPath=Get-ChildItem $base -Filter cl.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object {$_.FullName -match 'Hostx64[\\/]x64[\\/]cl.exe$'} |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if(!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)){throw 'MSVC x64 compiler not found.'}
$linker=Join-Path (Split-Path $CompilerPath) 'link.exe'
if(!(Test-Path -LiteralPath $linker)){throw 'MSVC linker not found.'}
$bytes=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ExpectedImage))
if($bytes.Length -lt 1 -or $bytes.Length -gt 73728 -or $Offset -gt 268435456 -or $bytes.Length -gt 268435456-$Offset){throw 'EFI reader range invalid.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
if($LifecyclePublicKey){
    $point=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($LifecyclePublicKey))
    if($point.Length -ne 97 -or $point[0] -ne 4){throw 'Lifecycle public key must be one uncompressed P384 point.'}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'lifecycle-key.h'),('static const unsigned char lifecycle_point[97]={'+(($point|ForEach-Object{$_.ToString()}) -join ',')+'};'))
}
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/efi/CxlRead.c'
$header="#define CXL_BASE 0x$($CxlBase.ToString('x'))ULL`n#define CXL_OFFSET 0x$($Offset.ToString('x'))ULL`n#define EXPECTED_BYTES $($bytes.Length)ULL`nstatic const U8 expected[] = {" + (($bytes|ForEach-Object {$_.ToString()}) -join ',') + '};'
if($TrustRoot){$header=$header.Substring(0,$header.IndexOf('static const'))}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'expected.h'),$header)
$object=Join-Path $OutputDirectory 'CxlRead.obj'
$efi=Join-Path $OutputDirectory 'CXLREAD.EFI'
if($NativeHdmCapsule){$efi=Join-Path $OutputDirectory 'CXLHDM.EFI'}
$cryptoArguments=@();$cryptoLink=@()
if($TrustRoot){
    if($MinImageGeneration -eq 0){throw 'Generation floor must be nonzero.'}
    $trust=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($TrustRoot))
    if($trust.Length -ne 129 -or $trust[0] -ne 4){throw 'Trust root must contain 97-byte uncompressed P384 point and 32-byte key ID.'}
    $trustHeader='static const unsigned char trusted_point[97]={'+(($trust[0..96]|ForEach-Object{$_.ToString()}) -join ',')+'};'+[Environment]::NewLine+'static const unsigned char trusted_key_id[32]={'+(($trust[97..128]|ForEach-Object{$_.ToString()}) -join ',')+'};'
    $trustHeader+=[Environment]::NewLine+"#define TRUSTED_IMAGE_GENERATION_FLOOR $($MinImageGeneration.ToString([Globalization.CultureInfo]::InvariantCulture))ULL"
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'trust.h'),$trustHeader)
    $efiSource=Split-Path $source
    $vendor=Join-Path $efiSource 'vendor/bearssl-0.6'
    $provenancePath=Join-Path $efiSource 'vendor/bearssl-provenance.json'
    $provenance=Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    foreach($entry in $provenance.Files){
        $dependency=Join-Path $vendor $entry.Path
        if(!(Test-Path -LiteralPath $dependency) -or (Get-FileHash -LiteralPath $dependency).Hash -ne $entry.Sha256){throw "Vendored crypto hash mismatch: $($entry.Path)"}
    }
    $cryptoArguments=@('/DCXL_CRYPTO',"/I$efiSource/freestanding","/I$vendor/inc")
    if($NativeHdmCapsule){$cryptoArguments+='/DCXL_NATIVE_CAPSULE'}
    if($RamCopy){$cryptoArguments+='/DCXL_RAM_COPY'}
    if($LifecycleMapping){$cryptoArguments+=@('/DCXL_LIFECYCLE_MAPPING','/DCXL_LIFECYCLE_ISOLATION')}
    $common=@('/nologo','/c','/GS-','/Gs999999','/Zl','/Oi','/Os','/Gy','/W0','/TC','/DBR_MSC=0','/DBR_UMUL128=0','/DBR_INT128=0','/DBR_SSE2=0','/DBR_AES_X86NI=0',"/I$efiSource/freestanding","/I$vendor/inc","/I$vendor/src","/I$OutputDirectory")
    if($NativeHdmCapsule){$common+='/DCXL_NATIVE_CAPSULE'}
    if($LifecycleMapping){$common+=@('/DCXL_LIFECYCLE_MAPPING','/DCXL_LIFECYCLE_ISOLATION')}
    $objects=@()
    $sources=@($provenance.Files | Where-Object {$_.Path.EndsWith('.c')} | ForEach-Object {Join-Path $vendor $_.Path})+@((Join-Path $efiSource 'Crypto.c'),(Join-Path $efiSource 'Crt.c'),(Join-Path $efiSource 'P384Vectors.c'))
    if($RamCopy){$sources+=Join-Path $efiSource 'RamStage.c'}
    foreach($unit in $sources){
        $unitObject=Join-Path $OutputDirectory (([IO.Path]::GetFileNameWithoutExtension($unit))+'.obj')
        & $CompilerPath @common "/Fo$unitObject" $unit
        if($LASTEXITCODE -ne 0){throw "Crypto compilation failed: $unit"}
        $objects+= $unitObject
    }
    $library=Join-Path $OutputDirectory 'crypto.lib'
    & (Join-Path (Split-Path $CompilerPath) 'lib.exe') /nologo "/out:$library" @objects
    if($LASTEXITCODE -ne 0){throw 'Crypto archive failed.'}
    $cryptoLink=@($library)
}
$nativeObjects=@()
if($NativeHdmCapsule){
    $nativeDefines=@();if($RamCopy){$nativeDefines=@('/DCXL_RAM_COPY')}
    $nativeUnits=@('PciDiscovery','AcpiDiscovery','NativeHdm','NativeCapsule','NativePreparationOwner')
    if($LifecyclePublicKey){$nativeUnits+='NativeLifecycleClient';$nativeDefines+=@('/DCXL_LIFECYCLE_LAB',"/I$vendor/inc")}
    if($LifecycleDmaLayout){$nativeDefines+=@('/DCXL_LIFECYCLE_ISOLATION',("/I"+(Split-Path ([IO.Path]::GetFullPath($LifecycleDmaLayout)))))}
    if($LifecycleMapping){$nativeDefines+='/DCXL_LIFECYCLE_MAPPING'}
    if($MailboxGate){$nativeUnits+='NativeMailbox';$nativeDefines+=@('/DNATIVE_MAILBOX_LAB','/DCXL_MAILBOX_CAPACITY_GATE',"/DCXL_EXPECTED_PMEM_BYTES=$(([UInt64]$ExpectedPmemMiB*1048576).ToString([Globalization.CultureInfo]::InvariantCulture))ULL")}
    foreach($unit in $nativeUnits){
        $nativeObject=Join-Path $OutputDirectory ($unit+'.obj')
        & $CompilerPath /nologo /c /GS- /Zl /Oi /Os /W4 /WX /TC /DNATIVE_HDM_LAB /DCXL_NATIVE_CAPSULE @nativeDefines "/I$efiSource/freestanding" "/I$OutputDirectory" "/Fo$nativeObject" (Join-Path $efiSource ($unit+'.c'))
        if($LASTEXITCODE){throw "Native capsule compilation failed: $unit"}
        $nativeObjects+=$nativeObject
    }
}
& $CompilerPath /nologo /c /GS- /Zl /Oi /Os /W4 /WX /TC @cryptoArguments "/I$OutputDirectory" "/Fo$object" $source
if($LASTEXITCODE -ne 0){throw 'EFI reader compilation failed.'}
& $linker /nologo /machine:x64 /subsystem:efi_application /entry:EfiMain /nodefaultlib /opt:ref "/out:$efi" $object @nativeObjects @cryptoLink
if($LASTEXITCODE -ne 0){throw 'EFI reader link failed.'}
$pe=[IO.File]::ReadAllBytes($efi)
$nt=[BitConverter]::ToInt32($pe,0x3c)
if($nt -lt 64 -or $nt -gt $pe.Length-264 -or [BitConverter]::ToUInt32($pe,$nt) -ne 0x4550 -or
   [BitConverter]::ToUInt16($pe,$nt+4) -ne 0x8664 -or
   [BitConverter]::ToUInt16($pe,$nt+24) -ne 0x20b -or
   [BitConverter]::ToUInt16($pe,$nt+24+68) -ne 10 -or
   [BitConverter]::ToUInt32($pe,$nt+24+16) -eq 0 -or
   [BitConverter]::ToUInt32($pe,$nt+24+120) -ne 0){throw 'Unexpected PE32+ EFI ABI or imported host runtime.'}
$metadata=@{Scope='Byte identity reader; not crypto verifier';Compiler=$CompilerPath;SourceSha256=(Get-FileHash -LiteralPath $source).Hash;ExpectedImageSha256=(Get-FileHash -LiteralPath $ExpectedImage).Hash;EfiSha256=(Get-FileHash -LiteralPath $efi).Hash;CxlBase=$CxlBase;Offset=$Offset;ImageBytes=$bytes.Length}
$metadata.CompilerSha256=(Get-FileHash -LiteralPath $CompilerPath).Hash
$metadata.LinkerSha256=(Get-FileHash -LiteralPath $linker).Hash
$metadata.ExpectedHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'expected.h')).Hash
if($TrustRoot){
    $metadata.Scope='Guest SHA384/ECDSA-P384 verifier; strict laboratory capsule profile, compiled development trust anchor'
    $metadata.CryptoSourceSha256=(Get-FileHash -LiteralPath (Join-Path $efiSource 'Crypto.c')).Hash
    $metadata.CrtSourceSha256=(Get-FileHash -LiteralPath (Join-Path $efiSource 'Crt.c')).Hash
    $metadata.P384VectorsSha256=(Get-FileHash -LiteralPath (Join-Path $efiSource 'P384Vectors.c')).Hash
    $metadata.TrustRootSha256=(Get-FileHash -LiteralPath $TrustRoot).Hash
    $metadata.CompiledMinimumImageGeneration=$MinImageGeneration
    $metadata.VendorProvenanceSha256=(Get-FileHash -LiteralPath $provenancePath).Hash
    $metadata.FreestandingHeaders=@(Get-ChildItem (Join-Path $efiSource 'freestanding') -Filter '*.h' | ForEach-Object { @{Name=$_.Name;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
}
$metadata.UefiHeaderSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $source) 'UefiLab.h')).Hash
if($RamCopy){$metadata.RamStageSha256=(Get-FileHash -LiteralPath (Join-Path $efiSource 'RamStage.c')).Hash}
if($NativeHdmCapsule){$metadata.Scope='Native discovery, HDM and FAT-to-CXL lab provisioning, guest crypto; no kernel execution';$metadata.NativeHdmCapsule=$true;$metadata.DynamicCfmwBase=$true;$metadata.NativeInputs=@('PciDiscovery','AcpiDiscovery','NativeHdm','NativeCapsule','NativePreparationOwner')|ForEach-Object {@{Name=$_;Sha256=(Get-FileHash (Join-Path $efiSource ($_+'.c'))).Hash}};$metadata.NativeOwnerHeaderSha256=(Get-FileHash (Join-Path $efiSource 'NativePreparationOwner.h')).Hash}
if($MailboxGate){$metadata.MailboxGate=$true;$metadata.ExpectedPmemMiB=$ExpectedPmemMiB;$metadata.NativeInputs+=@{Name='NativeMailbox';Sha256=(Get-FileHash (Join-Path $efiSource 'NativeMailbox.c')).Hash}}
if($LifecyclePublicKey){$metadata.LifecycleLab=$true;$metadata.LifecycleAdmissionImplemented=$false;$metadata.LifecyclePublicKeySha256=(Get-FileHash -LiteralPath $LifecyclePublicKey).Hash;$metadata.NativeInputs+=@{Name='NativeLifecycleClient';Sha256=(Get-FileHash (Join-Path $efiSource 'NativeLifecycleClient.c')).Hash}}
if($LifecycleDmaLayout){$metadata.IsolationProfile=$true;$metadata.IsolationAdmissionImplemented=$true;$metadata.LifecycleDmaLayoutSha256=(Get-FileHash -LiteralPath $LifecycleDmaLayout).Hash;$metadata.MappingAuthorityAdmitted=$false}
if($LifecycleMapping){$metadata.MappingProviderEnabled=$true;$metadata.MappingAuthorityAdmitted=$null}
$metadata | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'build.json')
$efi
