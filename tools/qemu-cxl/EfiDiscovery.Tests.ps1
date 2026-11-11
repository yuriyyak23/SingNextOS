Describe 'Read-only native EFI PCI and ACPI discovery' {
    It 'uses the same binary for relocated BDF, BAR, CHBS and CFMW' {
        $first=& "$PSScriptRoot/Test-CxlEfiDiscovery.ps1"
        $second=& "$PSScriptRoot/Test-CxlEfiDiscovery.ps1" -SkipBuild -EfiImage $first.EfiImagePath -RamMiB 6144 -CxlBus 24 -EduCount 4
        foreach($result in @($first,$second)){
            $result.Passed | Should Be $true
            $result.ConfigurationValid | Should Be $true
            $result.QemuDeviceCreated | Should Be $true
            $result.GuestReadOnlyPciDiscoveryVerified | Should Be $true
            $result.GuestNativeDvsecParserMatchesManaged | Should Be $true
            $result.GuestPciIoRawSnapshotsMatchQmp | Should Be $true
            $result.GuestCfmwDiscoveryVerified | Should Be $true
            $result.GuestNativePciParserNegativeCasesPassed | Should Be 13
            $result.GuestNativeAcpiParserNegativeCasesPassed | Should Be 10
            $result.QtestAttached | Should Be $false
            $result.GuestCxlMemoryAccessible | Should BeNullOrEmpty
            $result.GuestHdmProgrammingByEfiVerified | Should Be $false
            $result.GuestDmaIsolationQualified | Should Be $false
            $result.GuestCfmwAccessPolicyQualified | Should Be $false
            $result.GuestCxlBiOperationVerified | Should Be $false
            $result.BootHandoffPerformed | Should Be $false
        }
        $first.EfiSha256 | Should Be $second.EfiSha256
        $a=Get-Content (Join-Path $first.EvidenceDirectory 'discovery.json') -Raw|ConvertFrom-Json
        $b=Get-Content (Join-Path $second.EvidenceDirectory 'discovery.json') -Raw|ConvertFrom-Json
        $a.Endpoint.Bus | Should Not Be $b.Endpoint.Bus
        $a.Device.Address | Should Not Be $b.Device.Address
        $a.Host.Address | Should Not Be $b.Host.Address
        $a.Hpa | Should Not Be $b.Hpa
    }
    It 'coexists with guest capsule crypto and owned System RAM preparation' {
        $report=& "$PSScriptRoot/Test-CxlCapsuleAccess.ps1" -WithEfiPciDiscovery -WithEfiRamCopy -TimeoutSeconds 300
        $report.Passed | Should Be $true
        $report.GuestCfmwDiscoveryVerified | Should Be $true
        $report.GuestReadOnlyPciDiscoveryVerified | Should Be $true
        $report.GuestSignatureVerification | Should Be $true
        $report.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $true
        $report.GuestHybridBootInfoWireCompatible | Should Be $true
        $report.GuestDmaIsolationQualified | Should Be $false
        $report.GuestHdmProgrammingByEfiVerified | Should Be $false
        $report.RuntimeKernelExecuted | Should Be $false
        $report.BootHandoffPerformed | Should Be $false
    }
}
