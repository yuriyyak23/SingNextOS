param([string]$SummaryPath)
Describe 'Native Identify capacity gate before HDM and capsule preparation' {
    if($SummaryPath){$r=Get-Content -LiteralPath $SummaryPath -Raw|ConvertFrom-Json}
    else{$r=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" -WithRamPreparation -WithMailboxGate -WithRelocation}
    It 'qualifies Identify and capsule preparation in one native application' {
        $r.Passed | Should Be $true
        foreach($case in @($r.Positive,$r.Relocated)){
            foreach($field in @('Passed','NativeMailboxCapacityGatePassed','GuestMailboxIdentifyVerified','GuestMailboxResponseMatchesQmp','GuestSignatureVerification','GuestAuthenticatedPayloadCopiedToSystemRam','NativeRamHeldAfterCxlRetireVerified')){$case.$field | Should Be $true}
            $case.IdentifyPersistentBytes | Should Be 268435456
            $case.IdentifyLsaBytes | Should Be 1048576
            $text=Get-Content (Join-Path $case.EvidenceDirectory 'native-ram-ready.txt') -Raw
            $previous=-1
            foreach($marker in @('CXL_NATIVE_CAPACITY_GATE_PASSED','CXL_NATIVE_HDM_LAB_ENTER','CXL_NATIVE_CAPSULE_FILE_STAGED','CXL_EFI_SIGNATURE_AND_HASH_VERIFIED','CXL_RAM_READY')){
                $at=$text.IndexOf($marker)
                ($at -gt $previous) | Should Be $true
                $previous=$at
            }
        }
    }
    It 'rejects an explicit capacity-policy mismatch before programming HDM' {
        $case=$r.MailboxMismatch
        $case.Passed | Should Be $true
        $case.ExpectedMailboxCapacityRejection | Should Be $true
        $case.MailboxMismatchRejectedBeforeHdm | Should Be $true
        $case.GuestHdmProgrammingByEfiVerified | Should Be $false
        $case.GuestSignatureVerification | Should Be $false
        $case.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $false
        $registers=Get-Content (Join-Path $case.EvidenceDirectory 'mailbox-rejected-registers.json') -Raw|ConvertFrom-Json
        @($registers).Count | Should Be 3
        foreach($hop in $registers){foreach($value in $hop.Values){$value | Should Be 0}}
        $tree=Get-Content (Join-Path $case.EvidenceDirectory 'retired-mtree.txt') -Raw
        $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
    }
    It 'keeps separate wire opcode and trust domains during same-binary relocation' {
        $r.Positive.EfiSha256 | Should Be $r.Relocated.EfiSha256
        ($r.Positive.EfiSha256 -ne $r.MailboxMismatch.EfiSha256) | Should Be $true
        foreach($case in @($r.Positive,$r.Relocated)){
            $case.MailboxOpcodeBits | Should Be 16
            $case.ExistingBootTransportOpcodeBits | Should Be 8
            $case.GuestDmaIsolationQualified | Should Be $false
            $case.GuestBootInfoFullHandoffQualified | Should Be $false
            $case.RuntimeKernelExecuted | Should Be $false
            $case.BootHandoffPerformed | Should Be $false
        }
        $r.DamagedSignature.GuestDamagedCapsuleRejected | Should Be $true
    }
}
