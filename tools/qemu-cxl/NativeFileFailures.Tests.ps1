param([string]$SummaryPath)
Describe 'Native EFI capsule file failure handling' {
    if($SummaryPath){$r=Get-Content -LiteralPath $SummaryPath -Raw|ConvertFrom-Json}
    else{$r=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" -WithRamPreparation -WithFileFailures}
    It 'rejects a missing capsule file at Open before verification' {
        $cases=@($r.FileFailures|Where-Object ExpectedFileFailure -eq Missing)
        $cases.Count | Should Be 1
        $cases[0].GuestFileInputRejected | Should Be $true
        $cases[0].GuestFileFailureReason | Should Be 5
        $cases[0].GuestFileReadBytesBeforeRejection | Should Be 0
        (Test-Path (Join-Path $cases[0].EvidenceDirectory 'capsule-input.bin')) | Should Be $false
    }
    It 'detects truncation after the exact available input length' {
        $cases=@($r.FileFailures|Where-Object ExpectedFileFailure -eq Truncated)
        $cases.Count | Should Be 1
        $case=$cases[0]
        $case.GuestFileFailureReason | Should Be 6
        $length=(Get-Item (Join-Path $case.EvidenceDirectory 'capsule-input.bin')).Length
        $case.GuestFileReadBytesBeforeRejection | Should Be $length
        $validLength=(Get-Item (Join-Path $r.Positive.EvidenceDirectory 'capsule-input.bin')).Length
        $length | Should Be ($validLength-1)
    }
    It 'rejects an extra byte at the expected EOF boundary' {
        $cases=@($r.FileFailures|Where-Object ExpectedFileFailure -eq ExtraByte)
        $cases.Count | Should Be 1
        $case=$cases[0]
        $case.GuestFileFailureReason | Should Be 7
        $validLength=(Get-Item (Join-Path $r.Positive.EvidenceDirectory 'capsule-input.bin')).Length
        $case.GuestFileReadBytesBeforeRejection | Should Be $validLength
        (Get-Item (Join-Path $case.EvidenceDirectory 'capsule-input.bin')).Length | Should Be ($validLength+1)
    }
    It 'tears down each rejected route without authentication or RAM readiness' {
        $r.Passed | Should Be $true
        $r.Positive.GuestSignatureVerification | Should Be $true
        $r.Positive.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $true
        @($r.FileFailures).Count | Should Be 3
        foreach($case in $r.FileFailures){
            $case.Passed | Should Be $true
            $case.EfiSha256 | Should Be $r.Positive.EfiSha256
            $case.GuestNativeHdmFinalRetireVerified | Should Be $true
            $case.GuestSignatureVerification | Should Be $false
            $case.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $false
            $text=Get-Content (Join-Path $case.EvidenceDirectory 'native-file-rejected.txt') -Raw
            $text.Contains('CXL_EFI_READER_ENTER') | Should Be $false
            $text.Contains('CXL_RAM_READY') | Should Be $false
            $text.Contains('CXL_NATIVE_CAPSULE_AUTHENTICATED') | Should Be $false
            $tree=Get-Content (Join-Path $case.EvidenceDirectory 'retired-mtree.txt') -Raw
            $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
            foreach($field in @('DecoderIsolationQualified','TransactionalHdmMappingRetirementQualified','RuntimeKernelExecuted','BootHandoffPerformed')){$case.$field | Should Be $false}
        }
    }
}
