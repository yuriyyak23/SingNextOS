Describe 'Standalone EFI CXL CPU reader' {
    It 'loads in installed EDK2, matches bytes and rejects damaged CXL payload' {
        $report=& "$PSScriptRoot/Test-CxlCapsuleAccess.ps1" -WithEfiReader -TimeoutSeconds 240
        $report.Passed | Should Be $true
        $report.GuestEfiReaderExecuted | Should Be $true
        $report.GuestEfiByteIdentityVerified | Should Be $true
        $report.GuestEfiDamagedImageRejected | Should Be $true
        $report.GuestSignatureVerification | Should Be $false
        $report.RuntimeKernelExecuted | Should Be $false
        $report.BootHandoffPerformed | Should Be $false
        $report.DecoderRetirementQualified | Should Be $false
    }
}
