Describe 'Guest authenticated CXL to System RAM preparation' {
    It 'holds LoaderData pages, emits compatible BootInfo and clears failed preparations' {
        $report=& "$PSScriptRoot/Test-CxlCapsuleAccess.ps1" -WithEfiRamCopy -TimeoutSeconds 300
        $report.Passed | Should Be $true
        $report.GuestSignatureVerification | Should Be $true
        $report.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $true
        $report.GuestDestinationSha384Verified | Should Be $true
        $report.GuestHybridBootInfoWireCompatible | Should Be $true
        $report.GuestRamFailureCleanupObserved | Should Be $true
        $report.GuestRamAllocationFailureInjected | Should Be $true
        $report.GuestResetAwareCopyQualified | Should Be $false
        $report.GuestBootInfoFullHandoffQualified | Should Be $false
        $report.RuntimeKernelExecuted | Should Be $false
        $report.BootHandoffPerformed | Should Be $false
        $report.DecoderRetirementQualified | Should Be $false
    }
}
