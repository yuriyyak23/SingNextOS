param([string]$ResultPath)
# ResultPath validates preserved live evidence; omission starts fresh owned VMs.
Describe 'Managed HDM fault compensation and reset quarantine' {
    if($ResultPath){$report=Get-Content -LiteralPath $ResultPath -Raw|ConvertFrom-Json}
    else{$report=& "$PSScriptRoot/Investigate-CxlHdmDecoderGate.ps1" -HostBridgeHdm -Scenario transactional-commit-fault,transactional-reset-fault -Access MEM -TimeoutSeconds 240}
    It 'compensates an injected transport failure after a real complete commit' {
        $case=@($report.Cases|Where-Object Scenario -eq transactional-commit-fault)
        $case.Count | Should Be 1
        $case=$case[0]
        $case.Completed | Should Be $true
        $case.Injected | Should Be $true
        $case.MappingEvidenceDenied | Should Be $true
        $case.MapFailure | Should Be 'PartialCommit'
        $case.CompensatedRetrySucceeded | Should Be $true
        $case.WriteBlocked | Should Be $true
        $case.FinalRetireSucceeded | Should Be $true
        $before=$case.Snapshots|Where-Object Stage -eq '02-before-injected-fault'
        $after=$case.Snapshots|Where-Object Stage -eq '03-after-fault-compensation'
        @($before.DirectAliases).Count | Should Be 1
        @($after.DirectAliases).Count | Should Be 0
        $order=@($case.HardwareOperations|Where-Object Operation -eq Disable|Select-Object -ExpandProperty Name)
        ($order -join ',') | Should Be 'HostBridge,RootPort,Endpoint,HostBridge,RootPort,Endpoint'
    }
    It 'quarantines a real reset inside Map without reusing the stale route' {
        $case=@($report.Cases|Where-Object Scenario -eq transactional-reset-fault)
        $case.Count | Should Be 1
        $case=$case[0]
        $case.Completed | Should Be $true
        $case.Injected | Should Be $true
        $case.MappingEvidenceDenied | Should Be $true
        $case.MapFailure | Should Be 'Quarantined'
        $case.RealResetObserved | Should Be $true
        ($case.ResetSequenceAfter -gt $case.ResetSequenceBefore) | Should Be $true
        $case.QuarantinedRemapRejected | Should Be $true
        $case.GuestResetAuthorityQualified | Should Be $false
        # No stale-register Disable writes are permitted after the reset.
        @($case.HardwareOperations|Where-Object Operation -eq Disable).Count | Should Be 0
        $report.DecoderIsolationQualified | Should Be $false
        $report.TransactionalHdmMappingRetirementQualified | Should Be $false
    }
}
