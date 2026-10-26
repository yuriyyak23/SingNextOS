[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$HybridRoot = '',
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p00-baseline')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($HybridRoot)) {
    $HybridRoot = Join-Path (Split-Path -Path $RepositoryRoot -Parent) 'HybridCPU ISE'
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeInventory([string]$Root, [string[]]$Paths) {
    $files = foreach ($path in $Paths) {
        $absolute = Join-Path $Root $path
        if (Test-Path -LiteralPath $absolute -PathType Leaf) { Get-Item -LiteralPath $absolute }
        elseif (Test-Path -LiteralPath $absolute -PathType Container) {
            Get-ChildItem -LiteralPath $absolute -File -Recurse | Where-Object {
                $_.FullName -notmatch '[\\/](?:bin|obj|\.vs|artifacts)[\\/]'
            }
        }
    }
    @($files | Sort-Object FullName -Unique | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = Get-Sha256 $_.FullName
        }
    })
}

function Get-MissingMandatoryInputs([string]$Root, [string[]]$Paths) {
    @($Paths | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Root $_) -PathType Leaf) })
}
function Test-InventoryStable([object[]]$Before, [object[]]$After) {
    (@($Before | Sort-Object path | ForEach-Object { "$($_.path) $($_.sha256)" }) -join "`n") -ceq
        (@($After | Sort-Object path | ForEach-Object { "$($_.path) $($_.sha256)" }) -join "`n")
}
function Test-LockedPackage([string]$Path, [string]$Id, [string]$Version, [string]$ContentHash) {
    $result = [ordered]@{ path = $Path; id = $Id; version = $Version; sha512 = $null; verified = $false; failure = $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $result.failure = 'missing-package'; return $result }
    try {
        $stream = [IO.File]::OpenRead($Path)
        try {
            $result.sha512 = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($stream))
            if ($result.sha512 -cne $ContentHash) { $result.failure = 'lock-content-hash-mismatch'; return $result }
            $stream.Position = 0
            $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
            try {
                $nuspecs = @($archive.Entries | Where-Object { $_.FullName -notmatch '[/\\]' -and $_.Name.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
                if ($nuspecs.Count -ne 1) { $result.failure = 'nuspec-count'; return $result }
                $entryStream = $nuspecs[0].Open()
                try {
                    $settings = [Xml.XmlReaderSettings]::new()
                    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
                    $settings.XmlResolver = $null
                    $reader = [Xml.XmlReader]::Create($entryStream, $settings)
                    try {
                        $document = [Xml.XmlDocument]::new()
                        $document.XmlResolver = $null
                        $document.Load($reader)
                    } finally { $reader.Dispose() }
                } finally { $entryStream.Dispose() }
                $ids = $document.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="id"]')
                $versions = $document.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="version"]')
                if ($ids.Count -ne 1 -or $versions.Count -ne 1 -or
                    $ids[0].InnerText -cne $Id -or $versions[0].InnerText -cne $Version) {
                    $result.failure = 'nuspec-identity-mismatch'
                    return $result
                }
                $result.verified = $true
            } finally { $archive.Dispose() }
        } finally { $stream.Dispose() }
    } catch { $result.failure = 'package-read-or-format-failure' }
    return $result
}
function Test-LockIdentityMatch($Expected, $Actual) {
    if ($null -eq $Expected -or $null -eq $Actual) { return $false }
    $expectedVersion = $Expected.PSObject.Properties['resolved']
    $actualVersion = $Actual.PSObject.Properties['resolved']
    $expectedHash = $Expected.PSObject.Properties['contentHash']
    $actualHash = $Actual.PSObject.Properties['contentHash']
    return $null -ne $expectedVersion -and $null -ne $actualVersion -and
        $null -ne $expectedHash -and $null -ne $actualHash -and
        -not [string]::IsNullOrWhiteSpace($expectedVersion.Value) -and
        -not [string]::IsNullOrWhiteSpace($expectedHash.Value) -and
        $expectedVersion.Value -ceq $actualVersion.Value -and $expectedHash.Value -ceq $actualHash.Value
}
function Test-PackageAssemblyMatch([string]$Package, [string]$Entry, [string]$Binary) {
    $result = [ordered]@{ package = $Package; entry = $Entry; binary = $Binary; packageSha256 = $null; binarySha256 = $null; verified = $false; failure = $null }
    if (-not (Test-Path -LiteralPath $Binary -PathType Leaf)) { $result.failure = 'missing-binary'; return $result }
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($Package)
        try {
            $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $Entry })
            if ($entries.Count -ne 1) { $result.failure = 'entry-count'; return $result }
            $stream = $entries[0].Open()
            try { $result.packageSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            $result.binarySha256 = Get-Sha256 $Binary
            $result.verified = $result.packageSha256 -ceq $result.binarySha256
            if (-not $result.verified) { $result.failure = 'assembly-byte-mismatch' }
        } finally { $archive.Dispose() }
    } catch { $result.failure = 'assembly-read-failure' }
    return $result
}
function Test-RestoredPackageAssets([string]$AssetsPath, [string]$Id, [string]$Version, [string]$ContentHash, [string]$Package) {
    $result = [ordered]@{ assets = $AssetsPath; assetsSha256 = $null; id = $Id; version = $Version; cache = $null; assemblies = @(); verified = $false; failure = $null }
    if (-not (Test-Path -LiteralPath $AssetsPath -PathType Leaf)) { $result.failure = 'missing-assets'; return $result }
    try {
        $result.assetsSha256 = Get-Sha256 $AssetsPath
        $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
        $key = "$Id/$Version"
        $library = $assets.libraries.PSObject.Properties[$key].Value
        $target = $assets.targets.'net11.0'.PSObject.Properties[$key].Value
        $expectedPath = $Id.ToLowerInvariant() + '/' + $Version
        if ($library.type -cne 'package' -or $target.type -cne 'package' -or
            $library.sha512 -cne $ContentHash -or $library.path -cne $expectedPath) {
            $result.failure = 'assets-lock-identity-mismatch'; return $result
        }
        $paths = @(@($target.compile.PSObject.Properties.Name) + @($target.runtime.PSObject.Properties.Name) | Sort-Object -Unique)
        if ($paths.Count -eq 0) { $result.failure = 'empty-assets-selection'; return $result }
        $caches = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $expectedPath } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
        if ($caches.Count -ne 1) { $result.failure = 'missing-or-ambiguous-cache'; return $result }
        $result.cache = $caches[0]
        foreach ($entry in $paths) {
            if ($entry -notmatch '^lib/net11\.0/[^/\\:]+\.dll$') { $result.failure = 'unsupported-assets-entry'; return $result }
            $result.assemblies += Test-PackageAssemblyMatch $Package $entry (Join-Path $result.cache $entry)
        }
        $result.verified = @($result.assemblies | Where-Object { -not $_.verified }).Count -eq 0
        if (-not $result.verified) { $result.failure = 'cache-assembly-mismatch' }
    } catch { $result.failure = 'assets-read-or-format-failure' }
    return $result
}
function Invoke-Captured([string]$WorkingDirectory, [string]$Executable, [string[]]$Arguments) {
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $Executable @Arguments 2>&1 | Out-String
        [ordered]@{ exitCode = $LASTEXITCODE; output = $output.TrimEnd() }
    } finally { Pop-Location }
}

if (-not (Test-Path -LiteralPath $HybridRoot -PathType Container)) {
    throw "HybridCPU ISE root is missing: $HybridRoot"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Push-Location $RepositoryRoot
try {
    $head = (& git rev-parse HEAD).Trim()
    $branch = (& git branch --show-current).Trim()
    $status = @(& git status --short)
    $sourceRoots = @('contracts', 'src', 'tests/SingPlus.Tests/Architecture', 'eng/v6')
    $sourceBefore = Get-RelativeInventory $RepositoryRoot $sourceRoots
    $requiredInputPaths = @(
        'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'NuGet.Config',
        'SingNextOS.slnx', 'src/Runtime/SingPlus.Runtime/packages.lock.json',
        'tools/HybridCpu_ExecutableAdapter/packages.lock.json', 'eng/v6/Freeze-V6Baseline.ps1'
    )
    $packageChecks = @()
    $adapterLock = Join-Path $RepositoryRoot 'tools/HybridCpu_ExecutableAdapter/packages.lock.json'
    if (Test-Path -LiteralPath $adapterLock -PathType Leaf) {
        $lockedDependencies = (Get-Content -LiteralPath $adapterLock -Raw | ConvertFrom-Json).dependencies.'net11.0'
        foreach ($packageId in @('HybridCPU.ExternalRuntime', 'HybridCPU.ExternalRuntime.Contracts')) {
            $lockedVersion = $lockedDependencies.$packageId.resolved
            if ($lockedVersion -cnotmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') { throw 'Noncanonical locked package version' }
            $requiredInputPaths += ".packages/$packageId.$lockedVersion.nupkg"
            $packageChecks += Test-LockedPackage (Join-Path $RepositoryRoot ".packages/$packageId.$lockedVersion.nupkg") $packageId $lockedVersion $lockedDependencies.$packageId.contentHash
        }
    }
    $runtimeLock = Join-Path $RepositoryRoot 'src/Runtime/SingPlus.Runtime/packages.lock.json'
    $consumerLockConsistent = $false
    if ((Test-Path -LiteralPath $runtimeLock -PathType Leaf) -and (Test-Path -LiteralPath $adapterLock -PathType Leaf)) {
        $runtimeDependencies = (Get-Content -LiteralPath $runtimeLock -Raw | ConvertFrom-Json).dependencies.'net11.0'
        $consumerLockConsistent = Test-LockIdentityMatch $lockedDependencies.'HybridCPU.ExternalRuntime.Contracts' $runtimeDependencies.'HybridCPU.ExternalRuntime.Contracts'
    }
    $missingDependencyInputs = @(Get-MissingMandatoryInputs $RepositoryRoot $requiredInputPaths)
    $dependenciesBefore = Get-RelativeInventory $RepositoryRoot ($requiredInputPaths + @('.packages'))

    $dotnet = Invoke-Captured $RepositoryRoot 'dotnet' @('--info')
    $architectureGuards = Invoke-Captured $RepositoryRoot 'dotnet' @(
        'test', 'tests/SingPlus.Tests/SingPlus.Tests.csproj', '--no-restore', '-m:1', '-p:UseSharedCompilation=false',
        '--filter', 'FullyQualifiedName~V6ArchitectureGuardTests', '--verbosity', 'quiet')
    $projects = @(Get-ChildItem -Path $RepositoryRoot -Filter '*.csproj' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|\.vs|artifacts)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName).Replace('\', '/') } |
        Sort-Object)
    $tests = @($projects | Where-Object { $_ -match '(?:Tests|Qualification)\.csproj$' })
    $roadmapRoot = Join-Path $RepositoryRoot 'docs\SingNextOS-v6-roadmap-reworked-2026-09-23'
    $roadmapFailures = [Collections.Generic.List[string]]::new()
    $roadmapEntries = 0
    $roadmapNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath (Join-Path $roadmapRoot 'SHA256SUMS.txt')) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$') {
            $roadmapFailures.Add('malformed-manifest-entry')
            continue
        }
        $roadmapEntries++
        $expected = $Matches[1]
        $name = $Matches[2]
        $segments = $name.Split('/')
        if ([IO.Path]::IsPathRooted($name) -or $name.Contains('\') -or $name.Contains(':') -or
            $name -cne $name.Trim() -or
            @($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_ -cne $_.Trim() }).Count -ne 0) {
            $roadmapFailures.Add("noncanonical-path:$name")
            continue
        }
        if (-not $roadmapNames.Add($name)) {
            $roadmapFailures.Add("duplicate:$name")
            continue
        }
        $path = Join-Path $roadmapRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $roadmapFailures.Add("missing:$name"); continue }
        $cursor = $roadmapRoot
        $redirected = $false
        foreach ($segment in $segments) {
            $cursor = Join-Path $cursor $segment
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $redirected = $true
                break
            }
        }
        if ($redirected) { $roadmapFailures.Add("redirected-path:$name"); continue }
        $actual = Get-Sha256 $path
        if ($actual -ne $expected) { $roadmapFailures.Add("hash:${name}:$actual") }
    }
    if ($roadmapEntries -eq 0) { $roadmapFailures.Add('empty-manifest') }
    if (Test-Path -LiteralPath (Join-Path $HybridRoot '.git')) {
        $hybridGit = Invoke-Captured $HybridRoot 'git' @('rev-parse', 'HEAD')
    } else {
        $hybridGit = [ordered]@{ exitCode = -1; output = 'Git metadata absent; no Git command invoked.' }
    }
    $hybridInventory = Get-RelativeInventory $HybridRoot @(
        'global.json', 'Directory.Build.props', 'HybridCPU v2.slnx',
        'HybridCPU_ExternalRuntime', 'HybridCPU_ExternalRuntime.Contracts',
        'HybridCPU_ExternalRuntime.Tests'
    )

    $owners = [ordered]@{
        CapabilityAuthority = 'src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs'
        RegionAuthority = 'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs'
        ResourceBudgetAuthority = 'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs'
        ProcessRegistry = 'src/Runtime/SingPlus.Runtime/Processes/ProcessRegistry.cs'
        EndpointSessionRegistry = 'src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs'
        EndpointSessionInvocationRegistry = 'src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs'
        CancellationScopeAuthority = 'src/Runtime/SingPlus.Runtime/Deadlines/CancellationScopeAuthority.cs'
        ExternalOperationAuthority = 'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs'
        RuntimeLegality = 'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs'
    }
    $ownerMissing = @($owners.Values | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $_) -PathType Leaf)
    })
    $ownerInputs = Get-RelativeInventory $RepositoryRoot @($owners.Values)
    $sourceAfter = Get-RelativeInventory $RepositoryRoot $sourceRoots
    $sourceStable = (@($sourceBefore | ForEach-Object { "$($_.path) $($_.sha256)" }) -join "`n") -ceq
        (@($sourceAfter | ForEach-Object { "$($_.path) $($_.sha256)" }) -join "`n")
    $locks = Get-RelativeInventory $RepositoryRoot ($requiredInputPaths + @('.packages'))
    $missingDependenciesAfter = @(Get-MissingMandatoryInputs $RepositoryRoot $requiredInputPaths)
    $dependenciesStable = Test-InventoryStable $dependenciesBefore $locks
    $guardBinaries = Get-RelativeInventory $RepositoryRoot @(
        'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
        'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
        'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll'
    )
    $assemblyChecks = @()
    foreach ($package in $packageChecks) {
        if (-not $package.verified) { continue }
        $archive = [IO.Compression.ZipFile]::OpenRead($package.path)
        try { $entries = @($archive.Entries | Where-Object { $_.FullName.StartsWith('lib/net11.0/', [StringComparison]::Ordinal) -and $_.Name.EndsWith('.dll', [StringComparison]::Ordinal) } | ForEach-Object { $_.FullName }) }
        finally { $archive.Dispose() }
        if ($entries.Count -eq 0) { $assemblyChecks += [ordered]@{ verified = $false; failure = 'no-selected-assemblies' }; continue }
        foreach ($entry in $entries) {
            $binary = Join-Path $RepositoryRoot ("tests/SingPlus.Tests/bin/Debug/net11.0/" + [IO.Path]::GetFileName($entry))
            $assemblyChecks += Test-PackageAssemblyMatch $package.path $entry $binary
        }
    }
    $restoredChecks = @()
    foreach ($package in $packageChecks) {
        if (-not $package.verified) { continue }
        $restoredChecks += Test-RestoredPackageAssets (Join-Path $RepositoryRoot 'tools/HybridCpu_ExecutableAdapter/obj/project.assets.json') $package.id $package.version $package.sha512 $package.path
    }
    $snapshotVerified = $restoredChecks.Count -eq 2 -and @($restoredChecks | Where-Object { -not $_.verified }).Count -eq 0 -and $sourceStable -and $dependenciesStable -and $consumerLockConsistent -and $ownerMissing.Count -eq 0 -and
        $assemblyChecks.Count -gt 0 -and @($assemblyChecks | Where-Object { -not $_.verified }).Count -eq 0 -and
        $packageChecks.Count -eq 2 -and @($packageChecks | Where-Object { -not $_.verified }).Count -eq 0 -and
        $missingDependencyInputs.Count -eq 0 -and $missingDependenciesAfter.Count -eq 0 -and $dotnet.exitCode -eq 0 -and
        $guardBinaries.Count -eq 3 -and $architectureGuards.exitCode -eq 0

    $baseline = [ordered]@{
        schema = 'singnext.v6.p00-baseline/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = 'P00'
        slice = 'live-baseline-freeze'
        contour = 'source-toolchain-contract-inventory'
        singNext = [ordered]@{ head = $head; branch = $branch; status = $status; projects = $projects; tests = $tests; lockedInputs = $locks }
        hybridCpu = [ordered]@{
            gitMetadataAvailable = ($hybridGit.exitCode -eq 0)
            reportedGitOutput = $hybridGit.output
            roadmapShaIsAssertedAsCurrentIdentity = $false
            inventory = $hybridInventory
        }
        toolchain = [ordered]@{ dotnetInfoExitCode = $dotnet.exitCode; dotnetInfo = $dotnet.output }
        owners = $owners
        ownerInputs = $ownerInputs
        missingOwnerInputs = $ownerMissing
        sourceSnapshot = [ordered]@{
            roots = $sourceRoots
            inputsBeforeGuards = $sourceBefore
            inputsAfterGuards = $sourceAfter
            stableAcrossGuards = $sourceStable
            verified = $snapshotVerified
            excludes = @('bin', 'obj', '.vs', 'artifacts')
            scope = 'SingNext source/contract/architecture-guard/qualification-script inventory; other test sources require exact qualification artifacts'
        }
        mandatoryDependencyInputs = [ordered]@{ paths = $requiredInputPaths; missing = $missingDependencyInputs; verified = ($missingDependencyInputs.Count -eq 0) }
        dependencySnapshot = [ordered]@{ inputsBeforeGuards = $dependenciesBefore; inputsAfterGuards = $locks; stableAcrossGuards = $dependenciesStable; missingAfterGuards = $missingDependenciesAfter }
        consumerLockConsistency = [ordered]@{ package = 'HybridCPU.ExternalRuntime.Contracts'; consumers = @('SingPlus.Runtime', 'HybridCpu_ExecutableAdapter'); verified = $consumerLockConsistent }
        packageTupleVerification = $packageChecks
        guardPackageAssemblyVerification = $assemblyChecks
        restoredPackageVerification = $restoredChecks
        guardBinaryInputs = $guardBinaries
        featureGates = [ordered]@{
            registry = 'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs'
            state = if ($snapshotVerified) { 'OFF' } else { 'UNVERIFIED' }
            allV6DefaultOff = $snapshotVerified
            guardCommand = 'dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~V6ArchitectureGuardTests --verbosity quiet'
            guardExitCode = $architectureGuards.exitCode
            guardOutput = $architectureGuards.output
        }
        roadmapPackage = [ordered]@{
            path = 'docs/SingNextOS-v6-roadmap-reworked-2026-09-23'
            hashFailures = @($roadmapFailures)
            valid = ($roadmapFailures.Count -eq 0)
            validationScope = 'Listed manifest entries; current inventory also records unlisted files without asserting their qualification.'
            inventory = Get-RelativeInventory $RepositoryRoot @('docs/SingNextOS-v6-roadmap-reworked-2026-09-23')
        }
        claims = [ordered]@{
            p00 = if ($snapshotVerified -and $roadmapFailures.Count -eq 0) { 'StaticAdmission' } else { 'FutureGated' }
            otherPhases = 'Not assessed by this identity freeze; use exact contour qualification artifacts.'
        }
        externalBlockers = @('No physical IOMMU/DMA/ordering/persistence/RAS/attestation campaign input was supplied.')
        skippedChecks = @('Java-dependent checks excluded by instruction; cross-language qualification is not claimed.')
        isaImpact = 'NONE'
    }
    if (-not $baseline.hybridCpu.gitMetadataAvailable) {
        $baseline.externalBlockers += 'HybridCPU ISE Git metadata is unavailable; identity is the recorded file inventory.'
    }

    $jsonPath = Join-Path $OutputDirectory 'baseline.json'
    $markdownPath = Join-Path $OutputDirectory 'baseline.md'
    $baseline | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    $markdown = @"
# SingNextOS v6 P00 baseline evidence

- SingNextOS: ``$head`` on ``$branch``.
- HybridCPU identity: reproducible file inventory; Git metadata available = ``$($baseline.hybridCpu.gitMetadataAvailable)``.
- Projects: $($projects.Count); test/qualification projects: $($tests.Count).
- Roadmap hash validation: ``$($baseline.roadmapPackage.valid)``; failures: ``$($roadmapFailures -join ', ')``.
- V6 gate guard: ``$($baseline.featureGates.state)``; test exit code: ``$($architectureGuards.exitCode)``.
- Source snapshot stable across guards: ``$sourceStable``; verified: ``$snapshotVerified``; source inputs: $($sourceAfter.Count); owner inputs: $($ownerInputs.Count); guard binaries: $($guardBinaries.Count).
- P00 identity claim: ``$($baseline.claims.p00)``. Other phase claims require their exact contour qualification artifacts.
- ISA/opcode/architectural CPU semantics impact: ``NONE``.
- Java-dependent checks skipped by instruction; cross-language qualification is not claimed.

This artifact records identity/evidence only. It grants no execution, effect, publication, or release authority.
"@
    Set-Content -LiteralPath $markdownPath -Value $markdown -Encoding utf8NoBOM
    $sums = @($jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Value $sums -Encoding ascii
    if ($roadmapFailures.Count -gt 0) { exit 2 }
    if ($architectureGuards.exitCode -ne 0) { exit 3 }
    if (-not $snapshotVerified) { exit 4 }
} finally {
    Pop-Location
}
