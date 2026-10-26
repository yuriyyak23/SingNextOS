Describe 'Post-reset explicit host HDM uncommit' {
    It 'removes the stale alias by explicit host uncommit after reset in both access paths' {
        $report=& "$PSScriptRoot/Investigate-CxlHdmDecoderGate.ps1" -HostBridgeHdm -Scenario warm-reset-host-uncommit -Access MEM,MMIO -TimeoutSeconds 240
        $report.InvestigationCompleted | Should Be $true
        @($report.Cases).Count | Should Be 2
        foreach($case in $report.Cases){
            $case.Completed | Should Be $true
            $case.QmpWarmResetObserved | Should Be $true
            $case.ExplicitHostUncommitAfterReset | Should Be $true
            $case.WriteBlocked | Should Be $true
            $case.WarmResetSentinelRetentionVerified | Should Be $true
            $case.GuestResetAuthorityQualified | Should Be $false
            $reset=$case.Snapshots|Where-Object Stage -eq '03-after-warm-reset'
            $closed=$case.Snapshots|Where-Object Stage -eq '03b-explicit-host-uncommit'
            @($reset.DirectAliases).Count | Should Be 1
            @($closed.DirectAliases).Count | Should Be 0
        }
        $report.DecoderIsolationQualified | Should Be $false
        $report.TransactionalHdmMappingRetirementQualified | Should Be $false
    }
}

Describe 'Optional host-bridge HDM CPU route gate' {
    It 'observes host-first disable in both guest memory paths without claiming retirement isolation' {
        $report=& "$PSScriptRoot/Investigate-CxlHdmDecoderGate.ps1" -HostBridgeHdm -Scenario before-first-commit,committed-control,hostbridge-uncommit,host-first-uncommit,leaf-first-uncommit,hostbridge-global-disable
        $report.InvestigationCompleted | Should Be $true
        @($report.Cases).Count | Should Be 12
        $report.DecoderIsolationQualified | Should Be $false
        $report.TransactionalHdmMappingRetirementQualified | Should Be $false
        foreach($case in $report.Cases){
            $case.Completed | Should Be $true
            $case.HostHdmAdvertised | Should Be $true
            if($case.Scenario -eq 'committed-control'){$case.ControlWriteSucceeded | Should Be $true}
            if($case.Scenario -in @('before-first-commit','hostbridge-uncommit','host-first-uncommit')){$case.WriteBlocked | Should Be $true}
            if($case.Scenario -in @('hostbridge-uncommit','host-first-uncommit')){
                $state=$case.Snapshots|Where-Object Stage -eq '03-gate-state'
                @($state.DirectAliases).Count | Should Be 0
                if($case.Scenario -eq 'host-first-uncommit'){
                    @($state.Registers|Where-Object AdvertisesHdm).Count | Should Be 3
                    foreach($r in @($state.Registers|Where-Object AdvertisesHdm)){
                        ([Convert]::ToUInt64($r.Control.Substring(2),16) -band 0x600) | Should Be 0
                    }
                }
            }
        }
    }
    It 'records a real warm reset and retained sentinel without asserting guest reset authority' {
        $report=& "$PSScriptRoot/Investigate-CxlHdmDecoderGate.ps1" -SkipBuild -HostBridgeHdm -Scenario warm-reset -Access MEM -TimeoutSeconds 240
        $report.InvestigationCompleted | Should Be $true
        $case=$report.Cases[0]
        $case.QmpWarmResetObserved | Should Be $true
        ($case.ResetSequenceAfter -gt $case.ResetSequenceBefore) | Should Be $true
        $case.WarmResetSentinelRetentionVerified | Should Be $true
        $case.GuestResetAuthorityQualified | Should Be $false
        $report.DecoderIsolationQualified | Should Be $false
    }
}
