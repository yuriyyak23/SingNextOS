param([string]$ResultPath)
# Explicit ResultPath checks saved live evidence; omitted starts a fresh run.
Describe 'Managed HDM with guest capsule authentication and RAM preparation' {
    if($ResultPath){$report=Get-Content -LiteralPath $ResultPath -Raw|ConvertFrom-Json;$evidence=Split-Path ([IO.Path]::GetFullPath($ResultPath))}
    else{
        $report=& "$PSScriptRoot/Test-CxlCapsuleAccess.ps1" -WithManagedHdm -WithEfiRamCopy -WithEfiPciDiscovery -TimeoutSeconds 240
        $evidence=$report.EvidenceDirectory
    }
    It 'authenticates a capsule and prepares owned System RAM under a managed mapping' {
        foreach($field in @('Passed','ConfigurationValid','QemuDeviceCreated','GuestCxlMemoryAccessible','ManagedTransactionalMappingExecuted','HostBridgeHdmForPassthroughEnabled','GuestSignatureVerification','GuestInvalidSignatureRejected','GuestReadOnlyPciDiscoveryVerified','GuestCfmwDiscoveryVerified','GuestAuthenticatedPayloadCopiedToSystemRam','GuestDestinationSha384Verified','GuestHybridBootInfoWireCompatible','GuestRamFailureCleanupObserved')){
            $report.$field | Should Be $true
        }
    }
    It 'retires the managed route and preserves capsule bytes across remapping' {
        foreach($field in @('ManagedRetireCpuWriteBlocked','ManagedRetireCapsulePreserved','ManagedMappingGenerationAdvanced','ManagedFinalRetireSucceeded','ColdRestartLossVerified','AbsentCapsuleRejected')){$report.$field | Should Be $true}
        $operations=Get-Content (Join-Path $evidence 'managed-hdm-operations.json') -Raw|ConvertFrom-Json
        $order=@($operations|Where-Object Operation -eq Disable|Select-Object -ExpandProperty Name)
        ($order -join ',') | Should Be 'HostBridge,RootPort,Endpoint,HostBridge,RootPort,Endpoint'
        $tree=Get-Content (Join-Path $evidence 'managed-retired-mtree.txt') -Raw
        $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
        $finalTree=Get-Content (Join-Path $evidence 'transfer/logs/memory-tree.txt') -Raw
        $finalTree.Contains('cxl-direct-mapping-alias-') | Should Be $false
    }
    It 'keeps boot and full isolation claims unqualified' {
        foreach($field in @('DecoderIsolationQualified','TransactionalHdmMappingRetirementQualified','GuestDmaIsolationQualified','GuestCfmwAccessPolicyQualified','GuestTrustRootIndependentlyProvisioned','GuestAntiRollbackVerified','BootIntegrationReady','RuntimeKernelExecuted','BootHandoffPerformed')){$report.$field | Should Be $false}
    }
}
