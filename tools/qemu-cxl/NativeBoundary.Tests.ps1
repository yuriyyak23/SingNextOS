param([string]$RamSummary,[string]$PhysicalResult,[string]$BoundsResult)
# These assertions require explicit preserved live-run evidence; see NATIVE-BOUNDARY-RU.md.
Describe 'Native maximum capsule at exact CXL backing end' {
    if(!$RamSummary -or !$PhysicalResult -or !$BoundsResult){throw 'Supply RamSummary, PhysicalResult and BoundsResult from documented boundary runs.'}
    $ram=Get-Content -LiteralPath $RamSummary -Raw|ConvertFrom-Json
    $physical=Get-Content -LiteralPath $PhysicalResult -Raw|ConvertFrom-Json
    $bounds=Get-Content -LiteralPath $BoundsResult -Raw|ConvertFrom-Json
    $physicalDir=Split-Path ([IO.Path]::GetFullPath($PhysicalResult))
    It 'places the full 73728-byte image exactly at the 256 MiB limit' {
        $ram.Passed | Should Be $true
        $ram.CapsuleImageBytes | Should Be 73728
        $ram.CapsuleOffsetBytes | Should Be 268361728
        ($ram.CapsuleOffsetBytes+$ram.CapsuleImageBytes) | Should Be 268435456
        $physical.CapsuleOffsetBytes | Should Be $ram.CapsuleOffsetBytes
        $physical.Passed | Should Be $true
        $source=Join-Path $physicalDir 'capsule-input.bin'
        $observed=Join-Path $physicalDir 'native-cxl-observed.bin'
        (Get-FileHash $observed).Hash | Should Be (Get-FileHash $source).Hash
        $physical.CapsuleLastByteHpa | Should Be '0x19fffffff'
    }
    It 'copies the 64 KiB payload into 17 owned System RAM pages after source retirement' {
        foreach($case in @($ram.Positive,$ram.Relocated)){
            $case.GuestAuthenticatedPayloadCopiedToSystemRam | Should Be $true
            $case.NativeRamHeldAfterCxlRetireVerified | Should Be $true
            $case.GuestDestinationSha384Verified | Should Be $true
            $case.GuestHybridBootInfoWireCompatible | Should Be $true
            (Get-Item (Join-Path $case.EvidenceDirectory 'native-owned-ram.bin')).Length | Should Be 69632
        }
        $ram.Positive.EfiSha256 | Should Be $ram.Relocated.EfiSha256
        $ram.Positive.EfiSha256 | Should Be $physical.EfiSha256
        $ram.DamagedSignature.GuestDamagedCapsuleRejected | Should Be $true
    }
    It 'rejects one-byte overflow and UInt64 maximum before VM creation' {
        $bounds.Passed | Should Be $true
        $bounds.StartsGuestVm | Should Be $false
        @($bounds.Cases).Count | Should Be 4
        foreach($case in $bounds.Cases){$case.Rejected | Should Be $true;$case.OutputNotCreated | Should Be $true}
    }
    It 'does not claim kernel handoff or full CXL isolation' {
        foreach($case in @($ram.Positive,$ram.Relocated,$physical)){
            foreach($field in @('DecoderIsolationQualified','GuestDmaIsolationQualified','RuntimeKernelExecuted','BootHandoffPerformed')){$case.$field | Should Be $false}
        }
    }
}
