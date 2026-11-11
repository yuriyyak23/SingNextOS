[CmdletBinding()]
param(
    [string]$WorkDir,
    [string]$OutputDirectory,
    [switch]$RunVm,
    [switch]$IncludeFaults,
    [switch]$IncludeReset
)

. "$PSScriptRoot/Common.ps1"

if($IncludeReset -and !$RunVm){throw '-IncludeReset requires -RunVm.'}
if($IncludeFaults -and !$RunVm){throw '-IncludeFaults requires -RunVm.'}

$labRoot=Get-Root $WorkDir
$config=Read-Config $labRoot
$qemu=Resolve-Qemu $config.QemuPath
$capabilities=Get-Capabilities $qemu
$profileValid=Test-ProfileCapabilities $capabilities $config
$labProject=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
$static=[ordered]@{
    Scope='QEMU/EDK2 CXL native EFI laboratory; not a production SingBoot startup'
    WorkRoot=$labRoot
    ConfigurationValid=[bool]$profileValid
    QemuVersion=$capabilities.Version
    Profile=Get-CxlProfile $config
    CxlMiB=$config.CxlMiB
    LsaMiB=$config.LsaMiB
    WindowMiB=$config.WindowMiB
    RamMiB=$config.RamMiB
    CpuCount=$config.CpuCount
    Accel=$config.Accel
    LabProject=$labProject
    NativeEfiComposition='CXLHDM.EFI: PCI/ACPI discovery, Identify 0x4000, HDM, FAT provisioning, guest SHA384/ECDSA, RAM preparation'
    GuestCxlMemoryAccessible=$null
    GuestCryptoAndRamBootInfoCompatible=$null
    RuntimeAuthorityAdmission=$false
    KernelHandoff=$false
    ProductionQualified=$false
    Limit='NativeWindowsRam is volatile at QEMU exit; local mapping retirement is not DMA/cache/remote closure.'
}

if(!$RunVm){
    [pscustomobject]$static
    return
}

if(!$OutputDirectory){$OutputDirectory=Join-Path $labRoot ('logs/cxl-boot-progress-'+[guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'OutputDirectory must be new.'}

# Test-CxlNativeCapsule owns its new output tree and private QEMU/OVMF instance.
# Passing the CXL work root (not the repository root) is required by its child
# script's config contract.
$arguments=@{
    WorkDir=$labRoot
    OutputDirectory=$OutputDirectory
    WithRamPreparation=$true
    WithMailboxGate=$true
}
if($IncludeFaults){$arguments.WithRamFaults=$true}
if($IncludeReset){$arguments.WithRamReset=$true}
$run=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" @arguments

$positive=$run.Positive
$reset=$run.RamReset
$summary=[ordered]@{
    Scope=$static.Scope
    ConfigurationValid=[bool]$positive.ConfigurationValid
    QemuDeviceCreated=[bool]$positive.QemuDeviceCreated
    GuestCxlMemoryAccessible=[bool]$positive.GuestCxlMemoryAccessible
    GuestCryptoAndRamBootInfoCompatible=([bool]$positive.GuestSignatureVerification -and [bool]$positive.GuestAuthenticatedPayloadCopiedToSystemRam -and [bool]$positive.GuestHybridBootInfoWireCompatible)
    MailboxIdentifyCapacityGate=[bool]$positive.NativeMailboxCapacityGatePassed
    LocalCpuRouteRetired=[bool]$positive.NativePreparationLocalCpuRouteRetired
    RemoteClosureProven=[bool]$positive.NativePreparationRemoteClosureProven
    ResetSessionQuarantined=if($reset){[bool]$reset.NativePreparationSessionResetRejected}else{$null}
    RuntimeAuthorityAdmission=$false
    KernelHandoff=$false
    ProductionQualified=$false
    Passed=[bool]$run.Passed
    EvidenceDirectory=$run.EvidenceDirectory
    EfiSha256=$positive.EfiSha256
    CapsuleSha256=$positive.CapsuleInputSha256
    Profile=(Get-CxlProfile $config)
    Limit=$static.Limit
}
[pscustomobject]$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'demo-summary.json') -Encoding UTF8
[pscustomobject]$summary
