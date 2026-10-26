# Optional investigation regression. Completion does not mean decoder isolation passed.
Describe 'HDM decoder gate observations' {
    It 'records endpoint uncommit and backing bounds using guest CPU accesses' {
        $report=& "$PSScriptRoot/Investigate-CxlHdmDecoderGate.ps1" -Scenario endpoint-uncommit,boundary-range -Access MEM
        $report.InvestigationCompleted | Should Be $true
        @($report.Cases).Count | Should Be 2
        $report.QtestAttached | Should Be $false
        $report.DecoderIsolationQualified | Should Be $false
        $report.TransactionalHdmMappingRetirementQualified | Should Be $false
        $gate=$report.Cases | Where-Object Scenario -eq 'endpoint-uncommit'
        $gate.Completed | Should Be $true
        if (!$gate.WriteBlocked) {
            $state=$gate.Snapshots | Where-Object Stage -eq '03-gate-state'
            (@($state.DirectAliases).Count -gt 0) | Should Be $true
            (@($state.FlatCxlLines | Where-Object { $_ -match 'nv-ram.*cxlmem' }).Count -gt 0) | Should Be $true
        }
        $bounds=$report.Cases | Where-Object Scenario -eq 'boundary-range'
        $bounds.LastQwordWritable | Should Be $true
        $bounds.OutOfBackingWriteRejected | Should Be $true
        $bounds.AdjacentValidDataPreserved | Should Be $true
    }
}
