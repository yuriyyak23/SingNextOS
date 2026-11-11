# Optional, longer integration suite; requires the repository-pinned .NET SDK.
Describe 'CXL guest capsule round trip' {
    It 'creates, signs and rejects corrupted capsules and malformed dumps' {
        $project=Join-Path (Split-Path $PSScriptRoot -Parent) 'SingNext.CxlLab/SingNext.CxlLab.csproj'
        & dotnet build $project -c Release --nologo | Out-Null
        $LASTEXITCODE | Should Be 0
        $dll=Join-Path (Split-Path $project -Parent) 'bin/Release/net11.0/SingNext.CxlLab.dll'
        & dotnet $dll selftest | Out-Null
        $LASTEXITCODE | Should Be 0
    }
    It 'writes and reads an image through guest CXL HPA and rejects it after cold restart' {
        $report=& "$PSScriptRoot/Test-CxlCapsuleAccess.ps1" -SkipBuild
        $report.Passed | Should Be $true
        $report.Scope | Should Be 'GuestCapsuleRoundTrip'
        $report.GuestCxlMemoryAccessible | Should Be $true
        $report.HdmConfiguredByGuest | Should Be $true
        $report.CapsuleSha384VerifiedFromGuestReadback | Should Be $true
        $report.HostAdmissionFromBoundedGuestReadsVerified | Should Be $true
        $report.GuestSignatureVerification | Should Be $false
        $report.SecondCapsuleOverwriteVerified | Should Be $true
        $report.ZeroWordsExplicitlyWritten | Should Be $true
        $report.UsedDvsecWireContractsValidated | Should Be $true
        $report.GuestWriteAccess | Should Match '^CPU memory stores'
        $report.BootIntegrationReady | Should Be $false
        $report.DecoderRetirementQualified | Should Be $false
        $report.QtestAttachedDuringGuestTransfer | Should Be $false
        $report.AbsentCapsuleRejected | Should Be $true
        $report.RuntimeKernelExecuted | Should Be $false
        # Decoder-disable enforcement is a separate diagnostic, not a passing conformance claim.
        $report.FullCxlConformanceVerified | Should Be $false
    }
}
