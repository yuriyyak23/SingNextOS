param([string]$SummaryPath)
Describe 'Native held RAM reset invalidation' {
    if($SummaryPath){$r=Get-Content -LiteralPath $SummaryPath -Raw|ConvertFrom-Json}
    else{$r=& "$PSScriptRoot/Test-CxlNativeCapsule.ps1" -WithRamPreparation -WithRamReset}
    It 'observes a real reset after verified allocated RAM and rejects the old witness' {
        $r.Passed | Should Be $true
        $r.Positive.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $true
        $case=$r.RamReset
        $case.Passed | Should Be $true
        $case.NativeRamHeldAfterCxlRetireVerified | Should Be $true
        $case.RealResetObserved | Should Be $true
        ($case.ResetSequenceAfter -gt $case.ResetSequenceBefore) | Should Be $true
        $case.OldRamWitnessFailure | Should Be 'ResetObserved'
        $case.OldRamWitnessRecheck | Should Be 'Quarantined'
        $case.HostRamObservationQuarantined | Should Be $true
        $case.NativeRamPreparationCurrent | Should Be $false
        $firmware=Get-Content (Join-Path $case.EvidenceDirectory 'reset-firmware.txt') -Raw
        $firmware.Contains('CXL_LAB_READY') | Should Be $true
        $firmware.Contains('Shell>') | Should Be $true
    }
    It 'does not reread the old allocation after system_reset' {
        $case=$r.RamReset
        $w=Get-Content (Join-Path $case.EvidenceDirectory 'pre-reset-ram-witness.json') -Raw|ConvertFrom-Json
        $entries=@(Get-Content (Join-Path $case.EvidenceDirectory 'vm/logs/qmp.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
        $reset=-1;for($i=0;$i -lt $entries.Count;$i++){if($entries[$i].execute -eq 'system_reset'){$reset=$i;break}}
        ($reset -ge 0) | Should Be $true
        $events=@($entries|Where-Object event -eq RESET)
        ($events.Count -ge 1) | Should Be $true
        $oldStart=[UInt64]$w.Address;$oldEnd=$oldStart+[UInt64]$w.Pages*4096
        $overlaps=0
        for($i=$reset+1;$i -lt $entries.Count;$i++){
            if($entries[$i].execute -eq 'pmemsave'){
                $start=[UInt64]$entries[$i].arguments.val;$size=[UInt64]$entries[$i].arguments.size
                if($start -lt $oldEnd -and $oldStart -lt $start+$size){$overlaps++}
            }
        }
        $overlaps | Should Be 0
        $case.OldAllocationReadAfterReset | Should Be $false
        $case.OldAllocationFreeAttemptedAfterReset | Should Be $false
    }
    It 'sends no stale application ACK and leaves ownership and handoff unqualified' {
        $case=$r.RamReset
        $case.OldApplicationAckSentAfterReset | Should Be $false
        $case.GuestRamReleaseCleared | Should Be $false
        (Test-Path (Join-Path $case.EvidenceDirectory 'native-ram-release.txt')) | Should Be $false
        $tree=Get-Content (Join-Path $case.EvidenceDirectory 'native-ram-final-mtree.txt') -Raw
        $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
        foreach($field in @('GuestResetAwareCopyQualified','GuestBootInfoFullHandoffQualified','GuestDmaIsolationQualified','DecoderIsolationQualified','RuntimeKernelExecuted','BootHandoffPerformed')){$case.$field | Should Be $false}
    }
}
