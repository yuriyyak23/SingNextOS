[CmdletBinding()]
param(
    [string]$WorkDir,
    [string]$OutputDirectory,
    [string]$PayloadPath,
    [string]$IdentityDirectory,
    [UInt64]$ImageGeneration=1,
    [UInt64]$MinImageGeneration=1,
    [ValidateSet('MEM','MMIO')][string]$Access='MEM',
    [ValidateRange(0,240)][int]$OffsetMiB=0,
    [ValidateRange(60,600)][int]$TimeoutSeconds=180,
    [switch]$SkipBuild,
    [switch]$WithEfiReader,
    [switch]$WithEfiCrypto,
    [switch]$WithEfiRamCopy,
    [switch]$WithEfiPciDiscovery,
    [switch]$WithManagedHdm,
    [switch]$Json
)
. "$PSScriptRoot/Common.ps1"
if($WithManagedHdm -and !$WithEfiRamCopy){throw 'WithManagedHdm requires WithEfiRamCopy (includes guest crypto).'}
$root=Get-Root $WorkDir
$config=Join-Path $root 'config/qemu-cxl.json'
if (!(Test-Path -LiteralPath $config)) { throw 'Initialize the base QEMU environment first.' }
if (!$OutputDirectory) { $OutputDirectory=Join-Path $root ('logs/capsule-qualification-'+[guid]::NewGuid().ToString('N')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must not already exist; qualification never overwrites.' }
$project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
$dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
if (!$SkipBuild) {
    & dotnet build $project -c Release --nologo | ForEach-Object { Write-Verbose $_ }
    if ($LASTEXITCODE -ne 0) { throw 'CXL laboratory build failed.' }
}
if (!(Test-Path -LiteralPath $dll)) { throw 'CXL laboratory executable not built.' }
& dotnet $dll selftest | ForEach-Object { Write-Verbose $_ }
if ($LASTEXITCODE -ne 0) { throw 'Capsule/parser negative self-tests failed.' }
$arguments=@($dll,'qualify','--config',$config,'--output',$OutputDirectory,'--timeout-seconds',"$TimeoutSeconds",'--access',$Access,'--offset-mib',"$OffsetMiB")
if ($PayloadPath) { $arguments+=@('--payload',(Resolve-Path -LiteralPath $PayloadPath).Path) }
if ($WithEfiReader -or $WithEfiCrypto -or $WithEfiRamCopy) { $arguments+=@('--efi-builder',(Join-Path $PSScriptRoot 'Build-CxlEfiReader.ps1')) }
if ($WithEfiCrypto -or $WithEfiRamCopy) { $arguments+=@('--efi-crypto','true') }
if ($WithEfiRamCopy) { $arguments+=@('--efi-ram-copy','true') }
if($WithManagedHdm){$arguments+=@('--managed-hdm','true')}
if($WithEfiPciDiscovery){
    $pciBuild=Join-Path $root ('logs/efi-pci-build-'+[guid]::NewGuid().ToString('N'))
    & "$PSScriptRoot/Build-CxlEfiDiscovery.ps1" -OutputDirectory $pciBuild | ForEach-Object {Write-Verbose $_}
    $image=Join-Path $pciBuild 'CXLDISC.EFI'
    if(!(Test-Path -LiteralPath $image)){throw 'Discovery EFI build did not produce image.'}
    $arguments+=@('--efi-pci-image',$image)
}
if($IdentityDirectory){$arguments+=@('--identity',(Resolve-Path -LiteralPath $IdentityDirectory).Path)}
$arguments+=@('--image-generation',$ImageGeneration.ToString([Globalization.CultureInfo]::InvariantCulture),'--min-image-generation',$MinImageGeneration.ToString([Globalization.CultureInfo]::InvariantCulture))
& dotnet @arguments | ForEach-Object { Write-Verbose $_ }
if ($LASTEXITCODE -ne 0) { throw "CXL guest qualification failed. Evidence: $OutputDirectory" }
$reportPath=Join-Path $OutputDirectory 'result.json'
if ($Json) { Get-Content -LiteralPath $reportPath -Raw } else {
    $report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $report | Add-Member -NotePropertyName EvidenceDirectory -NotePropertyValue $OutputDirectory -PassThru
}
