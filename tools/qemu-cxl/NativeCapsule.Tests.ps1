param([string]$SummaryPath)
Describe 'Native discovery HDM and DirectBootCapsule guest verification' {
    if($SummaryPath){$r=Get-Content -LiteralPath $SummaryPath -Raw|ConvertFrom-Json}
    else{$r=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" -WithRelocation}
    It 'stages FAT input and verifies actual CXL bytes inside native EFI' {
        $r.Passed | Should Be $true
        foreach($case in @($r.Positive,$r.Relocated)){
            foreach($field in @('Passed','GuestCxlMemoryAccessible','CapsuleStagedByNativeEfiFromFat','GuestSignatureVerification','GuestPayloadSha384Verification','NativeCapsuleSourceBaseDiscovered','GuestNativeHdmFinalRetireVerified')){$case.$field | Should Be $true}
            $case.HostShellMmioProgrammingUsed | Should Be $false
            $case.QtestAttached | Should Be $false
        }
    }
    It 'rejects damaged signature and tears down the native mapping' {
        $r.DamagedSignature.Passed | Should Be $true
        $r.DamagedSignature.ExpectedCapsuleRejection | Should Be $true
        $r.DamagedSignature.GuestDamagedCapsuleRejected | Should Be $true
        $r.DamagedSignature.GuestSignatureVerification | Should Be $false
        $r.DamagedSignature.GuestNativeHdmFinalRetireVerified | Should Be $true
        $tree=Get-Content (Join-Path $r.DamagedSignature.EvidenceDirectory 'retired-mtree.txt') -Raw
        $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
    }
    It 'reuses one EFI binary across relocation without claiming kernel execution' {
        $r.Positive.EfiSha256 | Should Be $r.Relocated.EfiSha256
        $r.Positive.EfiSha256 | Should Be $r.DamagedSignature.EfiSha256
        $a=Get-Content (Join-Path $r.Positive.EvidenceDirectory 'discovery.json') -Raw|ConvertFrom-Json
        $b=Get-Content (Join-Path $r.Relocated.EvidenceDirectory 'discovery.json') -Raw|ConvertFrom-Json
        ($a.Hpa -ne $b.Hpa) | Should Be $true
        ($a.Device.Address -ne $b.Device.Address) | Should Be $true
        foreach($case in @($r.Positive,$r.DamagedSignature,$r.Relocated)){
            foreach($field in @('DecoderIsolationQualified','TransactionalHdmMappingRetirementQualified','GuestDmaIsolationQualified','RuntimeKernelExecuted','BootHandoffPerformed')){$case.$field | Should Be $false}
        }
        $r.Positive.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $false
    }
}
