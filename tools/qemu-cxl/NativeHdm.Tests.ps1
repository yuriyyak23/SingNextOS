param([string]$BaselineResult,[string]$RelocatedResult)
Describe 'Native EFI HDM programming and dynamic topology' {
    if($BaselineResult){$baseline=Get-Content -LiteralPath $BaselineResult -Raw|ConvertFrom-Json;$baseDir=Split-Path ([IO.Path]::GetFullPath($BaselineResult))}
    else{$baseline=& "$PSScriptRoot/Test-CxlNativeHdm.ps1";$baseDir=$baseline.EvidenceDirectory}
    if($RelocatedResult){$relocated=Get-Content -LiteralPath $RelocatedResult -Raw|ConvertFrom-Json;$relocDir=Split-Path ([IO.Path]::GetFullPath($RelocatedResult))}
    else{
        # Source image is retained by the runner for exact-binary reuse.
        $image=Join-Path $baseDir 'CXLHDM.EFI'
        if(!(Test-Path $image)){throw 'Baseline retained EFI image unavailable.'}
        $relocated=& "$PSScriptRoot/Test-CxlNativeHdm.ps1" -SkipBuild -EfiImage $image -RamMiB 6144 -CxlBus 24 -EduCount 4
        $relocDir=$relocated.EvidenceDirectory
    }
    It 'programs and retires real guest decoders without Shell MMIO writes' {
        foreach($r in @($baseline,$relocated)){
            foreach($field in @('Passed','ConfigurationValid','QemuDeviceCreated','GuestCxlMemoryAccessible','GuestHdmProgrammingByEfiVerified','GuestNativeHdmSentinelWriteObserved','GuestNativeHdmRetireWriteBlocked','GuestNativeHdmFinalRetireVerified')){
                $r.$field | Should Be $true
            }
            $r.GuestNativeDvsecParserMatchesManaged | Should Be $true
            $r.HostShellMmioProgrammingUsed | Should Be $false
            $r.QtestAttached | Should Be $false
        }
        foreach($dir in @($baseDir,$relocDir)){
            $tree=Get-Content (Join-Path $dir 'retired-mtree.txt') -Raw
            $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
            $serial=Get-Content (Join-Path $dir 'vm/logs/serial.txt') -Raw
            [regex]::IsMatch($serial,'Shell>\s+mm\s+[0-9a-f]+\s+[0-9a-f]+\s+-w\s+4\s+-MMIO') | Should Be $false
        }
    }
    It 'uses the same EFI image after BDF BAR and CFMW relocation' {
        $baseline.EfiSha256 | Should Be $relocated.EfiSha256
        $a=Get-Content (Join-Path $baseDir 'discovery.json') -Raw|ConvertFrom-Json
        $b=Get-Content (Join-Path $relocDir 'discovery.json') -Raw|ConvertFrom-Json
        ($a.Endpoint.Bus -ne $b.Endpoint.Bus) | Should Be $true
        ($a.Device.Address -ne $b.Device.Address) | Should Be $true
        ($a.Host.Address -ne $b.Host.Address) | Should Be $true
        ($a.Hpa -ne $b.Hpa) | Should Be $true
    }
    It 'does not equate scratch routing with a production SingBoot session' {
        foreach($r in @($baseline,$relocated)){
            foreach($field in @('DecoderIsolationQualified','TransactionalHdmMappingRetirementQualified','GuestDmaIsolationQualified','GuestCfmwAccessPolicyQualified','GuestCxlBiOperationVerified','RuntimeKernelExecuted','BootHandoffPerformed')){$r.$field | Should Be $false}
        }
    }
}
