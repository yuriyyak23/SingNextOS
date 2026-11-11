Describe 'Guest EFI capsule cryptography' {
    It 'verifies P384 and SHA384 in guest and rejects corrupt transport, manifest, signature and payload' {
        $report=& "$PSScriptRoot/Test-CxlCapsuleAccess.ps1" -WithEfiCrypto -TimeoutSeconds 300
        $report.Passed | Should Be $true
        $report.GuestEfiReaderExecuted | Should Be $true
        $report.GuestSignatureVerification | Should Be $true
        $report.GuestPayloadSha384Verification | Should Be $true
        $report.GuestSha384KnownAnswerPassed | Should Be $true
        $report.GuestP384Rfc6979KnownAnswersPassed | Should Be $true
        $report.GuestP384PrimitiveNegativeCasesPassed | Should Be 9
        $report.GuestInvalidSignatureRejected | Should Be $true
        $report.GuestEfiDamagedImageRejected | Should Be $true
        $report.GuestHeaderLengthAndKeyIdNegativesPassed | Should Be $true
        $report.GuestTrustRootIndependentlyProvisioned | Should Be $false
        $report.GuestAntiRollbackVerified | Should Be $false
        $report.BootIntegrationReady | Should Be $false
        $report.RuntimeKernelExecuted | Should Be $false
        $report.BootHandoffPerformed | Should Be $false
        $report.DecoderRetirementQualified | Should Be $false
    }
}
