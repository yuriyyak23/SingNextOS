param([string]$SummaryPath)
Describe 'Existing DirectBootCapsule reset-aware managed contract' {
    if($SummaryPath){$r=Get-Content -LiteralPath $SummaryPath -Raw|ConvertFrom-Json}
    else{$r=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" -WithRamPreparation -WithRamReset}
    $case=$r.RamReset
    $core=Get-Content (Join-Path $case.EvidenceDirectory 'core-reset-contract.json') -Raw|ConvertFrom-Json
    It 'runs the unchanged core method against captured guest payload and BootInfo' {
        $r.Passed | Should Be $true
        $case.ExistingPrepareKernelResetAwareExecuted | Should Be $true
        $case.CoreStableEpochPreparationVerified | Should Be $true
        $core.StableEpochSucceeded | Should Be $true
        $capsule=[IO.File]::ReadAllBytes((Join-Path $case.EvidenceDirectory 'capsule-input.bin'))
        $length=[BitConverter]::ToUInt64($capsule,4096+216)
        $payload=New-Object byte[] ([int]$length);[Array]::Copy($capsule,8192,$payload,0,[int]$length)
        $hash=[Security.Cryptography.SHA384]::Create()
        try{$digest=[BitConverter]::ToString($hash.ComputeHash($payload)).Replace('-','')}finally{$hash.Dispose()}
        $core.StableDestinationSha384 | Should Be $digest
    }
    It 'clears host RAM and BootInfo destinations when the final observation sees real reset' {
        $core.LateResetFailure | Should Be 'ResetObserved'
        ($core.ObservedResetSequence -gt $core.ExpectedReset.ResetSequence) | Should Be $true
        $case.CoreHostDestinationsClearedAfterReset | Should Be $true
        foreach($name in @('core-reset-host-destination.bin','core-reset-host-boot-info.bin')){
            $bytes=[IO.File]::ReadAllBytes((Join-Path $case.EvidenceDirectory $name))
            ($bytes.Length -gt 0) | Should Be $true
            @($bytes|Where-Object {$_ -ne 0}).Count | Should Be 0
        }
    }
    It 'distinguishes early refusal and fixture generations from production authority' {
        $core.StalePreconditionFailure | Should Be 'ResetObserved'
        $core.StalePreconditionLeavesDestinationsUntouched | Should Be $true
        $core.SyntheticGenerationFixture | Should Be $true
        $case.CoreSyntheticGenerationFixture | Should Be $true
        $core.GuestDestinationClearingAfterResetVerified | Should Be $false
        $core.ProductionBootAuthorityEstablished | Should Be $false
        $case.GuestRamReleaseCleared | Should Be $false
        $case.GuestBootInfoFullHandoffQualified | Should Be $false
        $case.RuntimeKernelExecuted | Should Be $false
        $case.BootHandoffPerformed | Should Be $false
    }
}
