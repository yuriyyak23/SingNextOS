[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$CompilerPath,[switch]$NativeHdmLab,[switch]$NativeMailbox)
$ErrorActionPreference='Stop'
if($NativeHdmLab -and $NativeMailbox){throw 'Select one lab application profile.'}
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Discovery build directory must be new.'}
if(!$CompilerPath){$CompilerPath=Get-ChildItem (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio') -Filter cl.exe -Recurse -ErrorAction SilentlyContinue | Where-Object {$_.FullName -match 'Hostx64[\\/]x64[\\/]cl.exe$'} | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName}
if(!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)){throw 'MSVC x64 compiler not found.'}
$linker=Join-Path (Split-Path $CompilerPath) 'link.exe'
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/efi'
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$objects=@()
$names=@('PciDiscovery','AcpiDiscovery','Crt');$defines=@()
if($NativeHdmLab){$names+='NativeHdm';$defines=@('/DNATIVE_HDM_LAB')}
if($NativeMailbox){$names+='NativeMailbox';$defines=@('/DNATIVE_MAILBOX_LAB')}
foreach($name in $names){
    $object=Join-Path $OutputDirectory ($name+'.obj')
    & $CompilerPath /nologo /c /GS- /Zl /Oi /Os /W4 /WX /TC @defines "/I$source/freestanding" "/Fo$object" (Join-Path $source ($name+'.c'))
    if($LASTEXITCODE -ne 0){throw "Discovery compilation failed: $name"}
    $objects+=$object
}
$efi=Join-Path $OutputDirectory $(if($NativeHdmLab){'CXLHDM.EFI'}else{'CXLDISC.EFI'})
if($NativeMailbox){$efi=Join-Path $OutputDirectory 'CXLMBX.EFI'}
& $linker /nologo /machine:x64 /subsystem:efi_application /entry:EfiMain /nodefaultlib /opt:ref "/out:$efi" @objects
if($LASTEXITCODE -ne 0){throw 'Discovery EFI link failed.'}
$pe=[IO.File]::ReadAllBytes($efi);$nt=[BitConverter]::ToInt32($pe,0x3c)
if($nt -lt 64 -or $nt -gt $pe.Length-264 -or [BitConverter]::ToUInt32($pe,$nt) -ne 0x4550 -or [BitConverter]::ToUInt16($pe,$nt+4) -ne 0x8664 -or [BitConverter]::ToUInt16($pe,$nt+24) -ne 0x20b -or [BitConverter]::ToUInt16($pe,$nt+24+68) -ne 10 -or [BitConverter]::ToUInt32($pe,$nt+24+120) -ne 0){throw 'Unexpected discovery EFI PE ABI/imports.'}
$inputs=@(Get-Item (Join-Path $source 'PciDiscovery.c'),(Join-Path $source 'AcpiDiscovery.c'),(Join-Path $source 'Crt.c'),(Join-Path $source 'UefiLab.h');Get-ChildItem (Join-Path $source 'freestanding') -Filter '*.h') | ForEach-Object {@{Path=$_.FullName;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}}
if($NativeHdmLab){$f=Get-Item (Join-Path $source 'NativeHdm.c');$inputs+=@{Path=$f.FullName;Bytes=$f.Length;Sha256=(Get-FileHash -LiteralPath $f.FullName).Hash}}
if($NativeMailbox){$f=Get-Item (Join-Path $source 'NativeMailbox.c');$inputs+=@{Path=$f.FullName;Bytes=$f.Length;Sha256=(Get-FileHash -LiteralPath $f.FullName).Hash}}
$scope=if($NativeHdmLab){'Opt-in native HDM scratch lab; no DMA/cache isolation qualification'}else{'Read-only firmware PCI I/O discovery; no driver or DMA-isolation implementation'}
if($NativeMailbox){$scope='Native read-only Identify Memory Device command; MMIO command/doorbell writes; no DMA isolation qualification'}
@{Scope=$scope;NativeHdmLab=[bool]$NativeHdmLab;Compiler=$CompilerPath;CompilerSha256=(Get-FileHash -LiteralPath $CompilerPath).Hash;LinkerSha256=(Get-FileHash -LiteralPath $linker).Hash;Inputs=@($inputs);EfiSha256=(Get-FileHash -LiteralPath $efi).Hash;PinnedBdf=$false;PinnedBar=$false} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'build.json')
$efi
