param([string]$SummaryPath)
Describe 'Native authenticated capsule RAM preparation after CXL retire' {
    if($SummaryPath){$r=Get-Content -LiteralPath $SummaryPath -Raw|ConvertFrom-Json}
    else{$r=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" -WithRamPreparation -WithRelocation}
    It 'holds authenticated payload and canonical BootInfo in System RAM after source retirement' {
        $r.Passed | Should Be $true
        foreach($case in @($r.Positive,$r.Relocated)){
            foreach($field in @('Passed','GuestSignatureVerification','GuestAuthenticatedPayloadCopiedToSystemRam','GuestDestinationSha384Verified','GuestHybridBootInfoWireCompatible','NativeRamHeldAfterCxlRetireVerified','GuestRamReleaseCleared','GuestNativeHdmFinalRetireVerified')){$case.$field | Should Be $true}
            $tree=Get-Content (Join-Path $case.EvidenceDirectory 'native-ram-retired-mtree.txt') -Raw
            $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
            $release=Get-Content (Join-Path $case.EvidenceDirectory 'native-ram-release.txt') -Raw
            $release.Contains('CXL_RAM_RELEASED CLEARED=1') | Should Be $true
            (Get-Item (Join-Path $case.EvidenceDirectory 'native-hybrid-boot-info.bin')).Length | Should Be 120
        }
    }
    It 'rejects bad signature before RAM preparation using the same EFI image' {
        $r.DamagedSignature.Passed | Should Be $true
        $r.DamagedSignature.GuestDamagedCapsuleRejected | Should Be $true
        $r.DamagedSignature.GuestSignatureVerification | Should Be $false
        $r.Positive.EfiSha256 | Should Be $r.DamagedSignature.EfiSha256
        $r.Positive.EfiSha256 | Should Be $r.Relocated.EfiSha256
        $text=Get-Content (Join-Path $r.DamagedSignature.EvidenceDirectory 'native-capsule-rejected.txt') -Raw
        $text.Contains('CXL_RAM_READY') | Should Be $false
    }
    It 'keeps reset authority full handoff and kernel execution unqualified' {
        foreach($case in @($r.Positive,$r.Relocated)){
            foreach($field in @('GuestResetAwareCopyQualified','GuestBootInfoFullHandoffQualified','GuestDmaIsolationQualified','DecoderIsolationQualified','TransactionalHdmMappingRetirementQualified','RuntimeKernelExecuted','BootHandoffPerformed')){$case.$field | Should Be $false}
        }
    }
}
