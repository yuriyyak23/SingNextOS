param([string]$SummaryPath)
Describe 'Native RAM preparation fault injection' {
    if($SummaryPath){$r=Get-Content -LiteralPath $SummaryPath -Raw|ConvertFrom-Json}
    else{$r=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" -WithRamPreparation -WithRamFaults}
    It 'clears the entire owned allocation after injected payload-copy failure' {
        $cases=@($r.RamFaults|Where-Object NativeRamMode -eq '--ram-copy-fail-copy')
        $cases.Count | Should Be 1
        $case=$cases[0]
        $case.Passed | Should Be $true
        $case.ExpectedRamFailure | Should Be $true
        $case.GuestRamFailureRejected | Should Be $true
        $case.NativeRamClearedBeforeFreeVerified | Should Be $true
        $case.GuestRamReleaseCleared | Should Be $true
        $case.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $false
        $owned=[IO.File]::ReadAllBytes((Join-Path $case.EvidenceDirectory 'native-owned-ram.bin'))
        ($owned.Length -ge 8192) | Should Be $true
        @($owned|Where-Object {$_ -ne 0}).Count | Should Be 0
    }
    It 'clears payload and BootInfo after injected BootInfo CRC failure' {
        $cases=@($r.RamFaults|Where-Object NativeRamMode -eq '--ram-copy-fail-info')
        $cases.Count | Should Be 1
        $case=$cases[0]
        $case.Passed | Should Be $true
        $case.NativeRamClearedBeforeFreeVerified | Should Be $true
        $case.GuestRamFailureRejected | Should Be $true
        $case.GuestHybridBootInfoWireCompatible | Should Be $false
        $owned=[IO.File]::ReadAllBytes((Join-Path $case.EvidenceDirectory 'native-owned-ram.bin'))
        @($owned|Where-Object {$_ -ne 0}).Count | Should Be 0
        $text=Get-Content (Join-Path $case.EvidenceDirectory 'native-ram-ready.txt') -Raw
        $text.Contains('CXL_RAM_BOOTINFO_CRC_REJECTED') | Should Be $true
    }
    It 'rejects an injected pre-allocation failure without exposing a RAM result' {
        $cases=@($r.RamFaults|Where-Object NativeRamMode -eq '--ram-copy-fail-alloc')
        $cases.Count | Should Be 1
        $case=$cases[0]
        $case.Passed | Should Be $true
        $case.NativeAllocationFailureBeforeAllocateVerified | Should Be $true
        $case.GuestRamFailureRejected | Should Be $true
        $case.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $false
        $text=Get-Content (Join-Path $case.EvidenceDirectory 'native-ram-allocation-rejection.txt') -Raw
        $text.Contains('CXL_RAM_READY') | Should Be $false
    }
    It 'uses the same authenticated input and EFI with closed CXL routes in all fault cases' {
        $r.Passed | Should Be $true
        $r.Positive.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $true
        @($r.RamFaults).Count | Should Be 3
        foreach($case in $r.RamFaults){
            $case.EfiSha256 | Should Be $r.Positive.EfiSha256
            $case.CapsuleInputSha256 | Should Be $r.Positive.CapsuleInputSha256
            $case.GuestSignatureVerification | Should Be $true
            $case.GuestRamFailureCleanupObserved | Should Be $true
            $case.GuestNativeHdmFinalRetireVerified | Should Be $true
            $tree=Get-Content (Join-Path $case.EvidenceDirectory 'native-ram-final-mtree.txt') -Raw
            $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
            foreach($field in @('DecoderIsolationQualified','TransactionalHdmMappingRetirementQualified','GuestDmaIsolationQualified','RuntimeKernelExecuted','BootHandoffPerformed')){$case.$field | Should Be $false}
        }
    }
}
