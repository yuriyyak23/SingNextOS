param([string]$BaselineResult,[string]$RelocatedResult)
Describe 'Native CXL mailbox Identify Memory Device' {
    if($BaselineResult){$a=Get-Content -LiteralPath $BaselineResult -Raw|ConvertFrom-Json;$baseDir=Split-Path ([IO.Path]::GetFullPath($BaselineResult))}
    else{$a=& "$PSScriptRoot/Test-CxlNativeMailbox.ps1";$baseDir=$a.EvidenceDirectory}
    if($RelocatedResult){$b=Get-Content -LiteralPath $RelocatedResult -Raw|ConvertFrom-Json;$relocDir=Split-Path ([IO.Path]::GetFullPath($RelocatedResult))}
    else{$b=& "$PSScriptRoot/Test-CxlNativeMailbox.ps1" -SkipBuild -EfiImage (Join-Path $baseDir 'CXLMBX.EFI') -RamMiB 6144 -CxlBus 24 -EduCount 4;$relocDir=$b.EvidenceDirectory}
    It 'executes the 16-bit opcode and independently decodes the response snapshot' {
        foreach($r in @($a,$b)){
            $r.Passed | Should Be $true
            $r.IdentifyOpcode | Should Be 16384
            $r.MailboxOpcodeBits | Should Be 16
            $r.GuestMailboxIdentifyVerified | Should Be $true
            $r.GuestMailboxResponseMatchesQmp | Should Be $true
            $r.IdentifyTotalBytes | Should Be 268435456
            $r.IdentifyPersistentBytes | Should Be 268435456
            $r.IdentifyVolatileBytes | Should Be 0
            $r.IdentifyLsaBytes | Should Be 1048576
            $r.MailboxParserNegativeCasesPassed | Should Be 8
        }
        foreach($dir in @($baseDir,$relocDir)){
            $raw=[IO.File]::ReadAllBytes((Join-Path $dir 'identify-response.bin'))
            ($raw.Length -ge 67) | Should Be $true
            [BitConverter]::ToUInt64($raw,16) | Should Be 1
            [BitConverter]::ToUInt64($raw,32) | Should Be 1
            [BitConverter]::ToUInt32($raw,56) | Should Be 1048576
            $report=Get-Content (Join-Path $dir 'result.json') -Raw|ConvertFrom-Json
            $mb=[Convert]::ToUInt64($report.MailboxRegisterBase.Substring(2),16)
            $device=[Convert]::ToUInt64($report.DeviceRegisterBase.Substring(2),16)
            $registers=[IO.File]::ReadAllBytes((Join-Path $dir 'device-registers-after.bin'))
            $status=[BitConverter]::ToUInt64($registers,[int]($mb-$device+16))
            (($status -shr 32) -band 65535) | Should Be 0
        }
    }
    It 'rediscovers mailbox addresses after same-binary PCI relocation' {
        $a.EfiSha256 | Should Be $b.EfiSha256
        ($a.DeviceRegisterBase -ne $b.DeviceRegisterBase) | Should Be $true
        ($a.MailboxRegisterBase -ne $b.MailboxRegisterBase) | Should Be $true
        (Get-FileHash (Join-Path $baseDir 'identify-response.bin')).Hash | Should Be (Get-FileHash (Join-Path $relocDir 'identify-response.bin')).Hash
    }
    It 'separates mailbox I/O from CXL.mem and existing byte-opcode boot API' {
        foreach($r in @($a,$b)){
            $r.ExistingBootTransportOpcodeBits | Should Be 8
            ($null -eq $r.GuestCxlMemoryAccessible) | Should Be $true
            foreach($field in @('GuestDmaIsolationQualified','BootIntegrationReady','RuntimeKernelExecuted','BootHandoffPerformed','QtestAttached')){$r.$field | Should Be $false}
        }
        foreach($dir in @($baseDir,$relocDir)){
            $tree=Get-Content (Join-Path $dir 'mailbox-mtree.txt') -Raw
            $tree.Contains('cxl-direct-mapping-alias-') | Should Be $false
        }
    }
}
