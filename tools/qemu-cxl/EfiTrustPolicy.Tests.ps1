Describe 'Reusable development identity and guest static generation floor' {
    It 'keeps one signing identity across fresh VMs and rejects signed foreign/stale images' {
        $identity=Join-Path (Join-Path $PSScriptRoot '../../CXL/logs') ('trust-identity-'+[guid]::NewGuid().ToString('N'))
        & "$PSScriptRoot/Initialize-CxlDevelopmentIdentity.ps1" -OutputDirectory $identity | Out-Null
        $before=(Get-FileHash -LiteralPath (Join-Path $identity 'development-private.pem')).Hash
        { & "$PSScriptRoot/Initialize-CxlDevelopmentIdentity.ps1" -OutputDirectory $identity -SkipBuild } | Should Throw
        $reports=@()
        foreach($generation in @(3,4)){
            $report=& "$PSScriptRoot/Test-CxlCapsuleAccess.ps1" -WithEfiCrypto -SkipBuild -IdentityDirectory $identity -ImageGeneration $generation -MinImageGeneration 2 -TimeoutSeconds 600
            $report.Passed | Should Be $true
            $report.DevelopmentIdentityReused | Should Be $true
            $report.GuestTrustChallenge_foreign | Should Be $true
            $report.GuestTrustChallenge_stale | Should Be $true
            $report.GuestTrustChallenge_floor | Should Be $true
            $report.CompiledMinimumImageGeneration | Should Be 2
            $report.GuestSignatureVerification | Should Be $true
            $report.GuestAntiRollbackVerified | Should Be $false
            $report.GuestTrustRootIndependentlyProvisioned | Should Be $false
            $report.RuntimeKernelExecuted | Should Be $false
            $reports+=$report
        }
        $reports[0].DevelopmentTrustSpkiSha256 | Should Be $reports[1].DevelopmentTrustSpkiSha256
        (Get-FileHash -LiteralPath (Join-Path $identity 'development-private.pem')).Hash | Should Be $before
    }
}
