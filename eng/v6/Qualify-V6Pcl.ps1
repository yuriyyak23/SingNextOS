[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\pcl'),
    [string]$HybridCpuRoot = (Join-Path (Split-Path $RepositoryRoot -Parent) 'HybridCPU ISE')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$filter = 'FullyQualifiedName~CompilerLoweringEvidenceV1Tests|FullyQualifiedName~CompilerLoweringEvidenceMetadataCodecV1Tests|FullyQualifiedName~V6CompilerLoweringEvidenceVerifierTests|FullyQualifiedName~LoweringEvidenceEmitterTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/CompilerLoweringEvidenceContracts.cs',
    'contracts/SingPlus.Contracts/CompilerLoweringEvidenceMetadataCodecV1.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6CompilerLoweringEvidenceVerifier.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ManagedSafePointProvider.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6StaticResourceEstimatePolicy.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tools/SingPlus.LoweringEvidenceEmitter/SingPlus.LoweringEvidenceEmitter.csproj',
    'tools/SingPlus.LoweringEvidenceEmitter/LoweringEvidenceEmitter.cs',
    'tools/SingPlus.LoweringEvidenceEmitter/Program.cs',
    'tools/SingPlus.LoweringEvidenceEmitter/README.md',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6PclPerformanceQualification.cs',
    'tests/SingPlus.Tests/Contracts/CompilerLoweringEvidenceV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/CompilerLoweringEvidenceMetadataCodecV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6CompilerLoweringEvidenceVerifierTests.cs',
    'tests/SingPlus.Tests/Tools/LoweringEvidenceEmitterTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'eng/v6/Qualify-V6Pcl.ps1'
)
$hybridCpuInputs = @(
    'Compilers/HybridCPU_Compiler/NativeAot/HybridCPU.Compiler.NativeAot.Adapter.csproj',
    'Compilers/HybridCPU_Compiler/NativeAot/Program.cs',
    'Compilers/HybridCPU_Compiler/NativeAot/Properties/AssemblyInfo.cs',
    'Compilers/HybridCPU_Compiler/NativeAot/SingNextPclCanonicalIrV1.cs',
    'Compilers/HybridCPU_Compiler/NativeAot/SingNextPclSidebandV1.cs',
    'Compilers/HybridCPU_Compiler/NativeAot/NativeAotSeamContractsV1.cs',
    'Compilers/HybridCPU_Compiler/Core/IR/Model/IrExternalDescriptorEvidenceV1.cs',
    'Compilers/HybridCPU_Compiler/Core/IR/Model/IrBundleAnnotations.cs',
    'Compilers/HybridCPU_Compiler/Core/IR/Model/IrSlotMetadata.cs',
    'Compilers/HybridCPU_Compiler/Core/IR/Model/IrInstruction.cs',
    'Compilers/HybridCPU_Compiler/Core/IR/Construction/HybridCpuIrBuilder.cs',
    'Compilers/HybridCPU_Compiler/Core/IR/Bundling/HybridCpuBundleLowerer.cs',
    'Compilers/HybridCPU_Compiler/API/Runtime/NativeTransportRuntimeAdapter.cs',
    'HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan5Phase23NativeAotSeamTests.cs',
    'HybridCPU_ISE.Tests/HybridCPU_ISE.Tests.csproj'
)
$integrationRoot = $null
$performancePath = $null

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "PCL tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The PCL test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value; $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value; $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 37 -or $skipped -ne 0 -or $total -ne 37) {
        throw "Unexpected PCL counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }

    if (-not (Test-Path -LiteralPath $HybridCpuRoot -PathType Container)) {
        throw "HybridCPU ISE root is missing: $HybridCpuRoot"
    }
    $hybridTestProject = Join-Path $HybridCpuRoot 'HybridCPU_ISE.Tests\HybridCPU_ISE.Tests.csproj'
    $hybridBuild = & dotnet build $hybridTestProject --no-restore 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "HybridCPU focused build failed.`n$hybridBuild" }
    $hybridOutput = & dotnet test $hybridTestProject --no-restore --no-build `
        --filter 'FullyQualifiedName~CompilerRefPlan5Phase23NativeAotSeamTests' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "HybridCPU NativeAOT seam tests failed.`n$hybridOutput" }
    $hybridMatch = [regex]::Match($hybridOutput, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $hybridMatch.Success) { throw 'The HybridCPU NativeAOT test summary could not be parsed.' }
    $hybridFailed = [int]$hybridMatch.Groups[1].Value
    $hybridPassed = [int]$hybridMatch.Groups[2].Value
    $hybridSkipped = [int]$hybridMatch.Groups[3].Value
    $hybridTotal = [int]$hybridMatch.Groups[4].Value
    if ($hybridFailed -ne 0 -or $hybridPassed -ne 24 -or $hybridSkipped -ne 0 -or $hybridTotal -ne 24) {
        throw "Unexpected HybridCPU NativeAOT counts: failed=$hybridFailed passed=$hybridPassed skipped=$hybridSkipped total=$hybridTotal"
    }

    $emitterProject = Join-Path $RepositoryRoot 'tools\SingPlus.LoweringEvidenceEmitter\SingPlus.LoweringEvidenceEmitter.csproj'
    $emitterBuild = & dotnet build $emitterProject --no-restore 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "PCL emitter build failed.`n$emitterBuild" }
    $adapter = Join-Path $HybridCpuRoot 'Compilers\HybridCPU_Compiler\NativeAot\bin\Debug\net11.0\HybridCPU.Compiler.NativeAot.Adapter.dll'
    $fixture = Join-Path $HybridCpuRoot 'HybridCPU_ISE.Tests\bin\Debug\net11.0\HybridCPU_ISE.Tests.dll'
    $emitter = Join-Path $RepositoryRoot 'tools\SingPlus.LoweringEvidenceEmitter\bin\Debug\net11.0\SingPlus.LoweringEvidenceEmitter.dll'
    @($adapter, $fixture, $emitter) | ForEach-Object {
        if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) { throw "PCL integration binary is missing: $_" }
    }

    $integrationRoot = Join-Path ([IO.Path]::GetTempPath()) ("singnext-pcl-integration-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $integrationRoot | Out-Null
    $facts = Join-Path $integrationRoot 'facts.json'
    $inputIr = Join-Path $integrationRoot 'input.ir'
    $binary = Join-Path $integrationRoot 'output.bin'
    $metadata = Join-Path $integrationRoot 'output.pcl'
    $dotnetHost = (Get-Command dotnet).Source
    $configuration = [ordered]@{
        ManifestPath = $facts; InputIrPath = $inputIr; ProducerPath = $emitter
        MetadataPath = $metadata; HostPath = $dotnetHost; DeriveFromCanonicalIr = $true
    } | ConvertTo-Json -Compress
    $priorSideband = [Environment]::GetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1')
    try {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $configuration)
        $positiveOutput = & dotnet $adapter compile --assembly $fixture `
            --type 'HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures' --method Add `
            --out $binary --source-commit '94ea82652cdd4e0f8046b5bd5becbd11461482ca' `
            --patch-digest 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9' 2>&1 | Out-String
        $positiveExit = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $priorSideband)
    }
    if ($positiveExit -ne 0) { throw "HybridCPU PCL positive integration failed with exit $positiveExit.`n$positiveOutput" }
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf) -or -not (Test-Path -LiteralPath $metadata -PathType Leaf) -or
        -not (Test-Path -LiteralPath $facts -PathType Leaf) -or -not (Test-Path -LiteralPath $inputIr -PathType Leaf)) {
        throw 'HybridCPU PCL positive integration did not produce output, derived inputs, and metadata.'
    }
    $derivedManifest = Get-Content -Raw -LiteralPath $facts | ConvertFrom-Json
    if ($derivedManifest.CompilerContractVersion -ne 'hybridcpu-nativeaot-canonical-ir-v1' -or
        @($derivedManifest.NumericFacts).Count -eq 0 -or @($derivedManifest.SafePointMap).Count -eq 0 -or
        @($derivedManifest.StaticResourceEstimates).Count -ne 3) {
        throw 'HybridCPU did not derive the expected canonical-IR numeric, safe-point, and static-resource fact manifest.'
    }
    $metadataLines = Get-Content -LiteralPath $metadata
    $positiveOutputBytes = (Get-Item -LiteralPath $binary).Length
    $positiveMetadataBytes = (Get-Item -LiteralPath $metadata).Length
    $outputDigest = Get-Sha256 $binary
    $metadataOutputDigest = ($metadataLines | Where-Object { $_.StartsWith('output=', [StringComparison]::Ordinal) }) -replace '^output=', ''
    $metadataToolchainDigest = ($metadataLines | Where-Object { $_.StartsWith('toolchain=', [StringComparison]::Ordinal) }) -replace '^toolchain=', ''
    $metadataProducerDigest = ($metadataLines | Where-Object { $_.StartsWith('producer=', [StringComparison]::Ordinal) }) -replace '^producer=', ''
    $metadataInputDigest = ($metadataLines | Where-Object { $_.StartsWith('input=', [StringComparison]::Ordinal) }) -replace '^input=', ''
    if ($metadataOutputDigest -ne $outputDigest -or $metadataToolchainDigest -ne (Get-Sha256 $adapter) -or
        $metadataProducerDigest -ne (Get-Sha256 $emitter) -or $metadataInputDigest -ne (Get-Sha256 $inputIr) -or
        @($metadataLines | Where-Object { $_.StartsWith('safe-point=', [StringComparison]::Ordinal) }).Count -ne
            @($derivedManifest.SafePointMap).Count -or
        @($metadataLines | Where-Object { $_.StartsWith('resource-estimate=', [StringComparison]::Ordinal) }).Count -ne
            @($derivedManifest.StaticResourceEstimates).Count) {
        throw 'HybridCPU PCL metadata is not bound to the exact canonical-IR/output/toolchain/producer tuple.'
    }
    $firstDerivedTuple = [ordered]@{
        manifest = Get-Sha256 $facts; inputIr = Get-Sha256 $inputIr
        output = Get-Sha256 $binary; metadata = Get-Sha256 $metadata
    }
    try {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $configuration)
        $repeatOutput = & dotnet $adapter compile --assembly $fixture `
            --type 'HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures' --method Add `
            --out $binary --source-commit '94ea82652cdd4e0f8046b5bd5becbd11461482ca' `
            --patch-digest 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9' 2>&1 | Out-String
        $repeatExit = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $priorSideband)
    }
    $secondDerivedTuple = [ordered]@{
        manifest = Get-Sha256 $facts; inputIr = Get-Sha256 $inputIr
        output = Get-Sha256 $binary; metadata = Get-Sha256 $metadata
    }
    if ($repeatExit -ne 0 -or ($firstDerivedTuple | ConvertTo-Json -Compress) -ne ($secondDerivedTuple | ConvertTo-Json -Compress)) {
        throw "HybridCPU compiler-derived PCL inputs are not deterministic. exit=$repeatExit`n$repeatOutput"
    }

    $corpusCases = @(
        [ordered]@{ name = 'managed-helper-call'; type = 'RestrictedCilCSharpFixtures'; method = 'CallIdentity'; success = $true },
        [ordered]@{ name = 'early-return-cfg'; type = 'RestrictedCilCSharpFixtures'; method = 'EarlyReturn'; success = $true },
        [ordered]@{ name = 'primitive-helper-shape'; type = 'ManagedCallGraphFixtures'; method = 'PrimitiveHelper'; success = $true },
        [ordered]@{ name = 'unsupported-callvirt'; type = 'RestrictedCilCSharpFixtures'; method = 'StringLength'; success = $false; exit = 4; diagnostic = 'HCCIL1001.*callvirt' },
        [ordered]@{ name = 'unsupported-comparison-opcode'; type = 'ScalarControlFlowV2Fixtures'; method = 'ForwardDiamond'; success = $false; exit = 4; diagnostic = 'HCCIL1001.*0xfe01' },
        [ordered]@{ name = 'non-single-assignment-loop'; type = 'ScalarControlFlowV2Fixtures'; method = 'ForAccumulator'; success = $false; exit = 4; diagnostic = 'HCCIL1021' },
        [ordered]@{ name = 'local-budget-exhaustion'; type = 'ScalarControlFlowV2Fixtures'; method = 'NestedLoops'; success = $false; exit = 5; diagnostic = 'HCCIL2008' },
        [ordered]@{ name = 'non-allowlisted-call-graph-root'; type = 'ManagedCallGraphFixtures'; method = 'Root'; success = $false; exit = 4; diagnostic = 'HCCIL1009' },
        [ordered]@{ name = 'unsupported-array-load-shape'; type = 'Phase04CilShapesFixture'; method = 'LoadInt'; success = $false; exit = 5; diagnostic = 'HCCIL0020' },
        [ordered]@{ name = 'unsupported-divide-opcode'; type = 'RuntimeResultFixture'; method = 'Divide'; success = $false; exit = 4; diagnostic = 'HCCIL1001.*0x005b' }
    )
    $corpusRoot = Join-Path $integrationRoot 'corpus'
    New-Item -ItemType Directory -Path $corpusRoot | Out-Null
    $corpusEvidence = @()
    foreach ($case in $corpusCases) {
        $caseRoot = Join-Path $corpusRoot $case.name
        New-Item -ItemType Directory -Path $caseRoot | Out-Null
        $caseFacts = Join-Path $caseRoot 'facts.json'
        $caseInput = Join-Path $caseRoot 'input.ir'
        $caseBinary = Join-Path $caseRoot 'output.bin'
        $caseMetadata = Join-Path $caseRoot 'output.pcl'
        $caseConfiguration = [ordered]@{
            ManifestPath = $caseFacts; InputIrPath = $caseInput; ProducerPath = $emitter
            MetadataPath = $caseMetadata; HostPath = $dotnetHost; DeriveFromCanonicalIr = $true
        } | ConvertTo-Json -Compress
        $firstWatch = [Diagnostics.Stopwatch]::StartNew()
        try {
            [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $caseConfiguration)
            $caseOutput = & dotnet $adapter compile --assembly $fixture `
                --type "HybridCPU_ISE.Tests.CompilerTests.$($case.type)" --method $case.method `
                --out $caseBinary --source-commit '94ea82652cdd4e0f8046b5bd5becbd11461482ca' `
                --patch-digest 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9' 2>&1 | Out-String
            $caseExit = $LASTEXITCODE
        } finally {
            $firstWatch.Stop()
            [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $priorSideband)
        }
        if ($case.success) {
            if ($caseExit -ne 0 -or -not (Test-Path -LiteralPath $caseBinary -PathType Leaf) -or
                -not (Test-Path -LiteralPath $caseMetadata -PathType Leaf) -or
                -not (Test-Path -LiteralPath $caseFacts -PathType Leaf) -or
                -not (Test-Path -LiteralPath $caseInput -PathType Leaf)) {
                throw "PCL corpus positive case '$($case.name)' failed. exit=$caseExit`n$caseOutput"
            }
            $caseManifest = Get-Content -Raw -LiteralPath $caseFacts | ConvertFrom-Json
            if (@($caseManifest.StaticResourceEstimates).Count -ne 3 -or
                @($caseManifest.SafePointMap).Count -eq 0) {
                throw "PCL corpus positive case '$($case.name)' emitted incomplete facts."
            }
            $firstTuple = [ordered]@{
                manifest = Get-Sha256 $caseFacts; inputIr = Get-Sha256 $caseInput
                output = Get-Sha256 $caseBinary; metadata = Get-Sha256 $caseMetadata
            }
            $metadataOutput = ((Get-Content -LiteralPath $caseMetadata) |
                Where-Object { $_.StartsWith('output=', [StringComparison]::Ordinal) }) -replace '^output=', ''
            if ($metadataOutput -ne $firstTuple.output) {
                throw "PCL corpus positive case '$($case.name)' metadata is not output-bound."
            }
            $secondWatch = [Diagnostics.Stopwatch]::StartNew()
            try {
                [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $caseConfiguration)
                $secondOutput = & dotnet $adapter compile --assembly $fixture `
                    --type "HybridCPU_ISE.Tests.CompilerTests.$($case.type)" --method $case.method `
                    --out $caseBinary --source-commit '94ea82652cdd4e0f8046b5bd5becbd11461482ca' `
                    --patch-digest 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9' 2>&1 | Out-String
                $secondExit = $LASTEXITCODE
            } finally {
                $secondWatch.Stop()
                [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $priorSideband)
            }
            $secondTuple = [ordered]@{
                manifest = Get-Sha256 $caseFacts; inputIr = Get-Sha256 $caseInput
                output = Get-Sha256 $caseBinary; metadata = Get-Sha256 $caseMetadata
            }
            if ($secondExit -ne 0 -or ($firstTuple | ConvertTo-Json -Compress) -ne
                    ($secondTuple | ConvertTo-Json -Compress)) {
                throw "PCL corpus positive case '$($case.name)' is not deterministic. exit=$secondExit`n$secondOutput"
            }
            $estimates = @($caseManifest.StaticResourceEstimates)
            $corpusEvidence += [ordered]@{
                name = $case.name; expected = 'success'; type = $case.type; method = $case.method
                firstElapsedMilliseconds = [Math]::Round($firstWatch.Elapsed.TotalMilliseconds, 3)
                secondElapsedMilliseconds = [Math]::Round($secondWatch.Elapsed.TotalMilliseconds, 3)
                outputBytes = (Get-Item -LiteralPath $caseBinary).Length
                outputSha256 = $firstTuple.output; metadataSha256 = $firstTuple.metadata
                instructionUpperBound = [ulong](@($estimates | Where-Object ResourceIdentity -eq 'canonical-instruction-count')[0].UpperBound)
                basicBlockUpperBound = [ulong](@($estimates | Where-Object ResourceIdentity -eq 'canonical-basic-block-count')[0].UpperBound)
                maximumOperandUpperBound = [ulong](@($estimates | Where-Object ResourceIdentity -eq 'maximum-static-operands-per-instruction')[0].UpperBound)
                safePointCount = @($caseManifest.SafePointMap).Count; deterministic = $true
            }
        } else {
            if ($caseExit -ne $case.exit -or $caseOutput -notmatch $case.diagnostic -or
                (Test-Path -LiteralPath $caseBinary) -or (Test-Path -LiteralPath ($caseBinary + '.json')) -or
                (Test-Path -LiteralPath $caseMetadata) -or (Test-Path -LiteralPath $caseFacts) -or
                (Test-Path -LiteralPath $caseInput)) {
                throw "PCL corpus negative case '$($case.name)' did not fail closed as expected. exit=$caseExit`n$caseOutput"
            }
            $corpusEvidence += [ordered]@{
                name = $case.name; expected = 'fail-closed'; type = $case.type; method = $case.method
                exitCode = $caseExit; diagnosticPattern = $case.diagnostic
                elapsedMilliseconds = [Math]::Round($firstWatch.Elapsed.TotalMilliseconds, 3)
                outputQuarantined = $true
            }
        }
    }

    'stale metadata must not survive' | Set-Content -LiteralPath $metadata -Encoding utf8NoBOM
    $negativeConfiguration = [ordered]@{
        ManifestPath = $facts; InputIrPath = $inputIr; ProducerPath = $fixture
        MetadataPath = $metadata; HostPath = $dotnetHost; DeriveFromCanonicalIr = $true
    } | ConvertTo-Json -Compress
    try {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $negativeConfiguration)
        $negativeOutput = & dotnet $adapter compile --assembly $fixture `
            --type 'HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures' --method Add `
            --out $binary --source-commit '94ea82652cdd4e0f8046b5bd5becbd11461482ca' `
            --patch-digest 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9' 2>&1 | Out-String
        $negativeExit = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $priorSideband)
    }
    if ($negativeExit -ne 14 -or $negativeOutput -notmatch 'HCNAOT4003' -or
        (Test-Path -LiteralPath $binary) -or (Test-Path -LiteralPath ($binary + '.json')) -or
        (Test-Path -LiteralPath $metadata)) {
        throw "HybridCPU PCL fail-closed integration did not quarantine output/stale metadata. exit=$negativeExit`n$negativeOutput"
    }
    $graphRoot = Join-Path $integrationRoot 'managed-graph'
    New-Item -ItemType Directory -Path $graphRoot | Out-Null
    $graphFixture = Join-Path $graphRoot 'graph-fixture.dll'
    Copy-Item -LiteralPath $fixture -Destination $graphFixture
    $graphPresentation = Join-Path $graphRoot 'body-presentation.json'
    $graphFacts = Join-Path $graphRoot 'facts.json'
    $graphInput = Join-Path $graphRoot 'input.ir'
    $graphBinary = Join-Path $graphRoot 'output.bin'
    $graphMetadata = Join-Path $graphRoot 'output.pcl'
    $graphType = 'HybridCPU_ISE.Tests.CompilerTests.ManagedCallGraphFixtures'
    $graphMethodNames = @('ParameterlessRoot', 'LoopHelper', 'Left', 'Right', 'Shared')
    $graphBodies = @()
    $getEntityToken = [Reflection.Metadata.Ecma335.MetadataTokens].GetMethod('GetToken',
        [type[]]@([Reflection.Metadata.EntityHandle]))
    if ($null -eq $getEntityToken) { throw 'The exact ECMA-335 entity-token API is unavailable.' }
    $graphStream = [IO.File]::OpenRead($graphFixture)
    try {
        $graphPe = [Reflection.PortableExecutable.PEReader]::new($graphStream)
        try {
            $graphReader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($graphPe)
            foreach ($typeHandle in $graphReader.TypeDefinitions) {
                $typeDefinition = $graphReader.GetTypeDefinition($typeHandle)
                $typeNamespace = $graphReader.GetString($typeDefinition.Namespace)
                $typeName = $graphReader.GetString($typeDefinition.Name)
                $fullTypeName = if ([string]::IsNullOrEmpty($typeNamespace)) { $typeName } else { "$typeNamespace.$typeName" }
                if ($fullTypeName -ne $graphType) { continue }
                foreach ($methodHandle in $typeDefinition.GetMethods()) {
                    $methodDefinition = $graphReader.GetMethodDefinition($methodHandle)
                    $methodName = $graphReader.GetString($methodDefinition.Name)
                    if ($graphMethodNames -contains $methodName) {
                        $graphBodies += [pscustomobject][ordered]@{
                            TypeName = $fullTypeName
                            MethodName = $methodName
                            MetadataToken = [int]$getEntityToken.Invoke($null,
                                [object[]]@([Reflection.Metadata.EntityHandle]$methodHandle))
                        }
                    }
                }
            }
        } finally { $graphPe.Dispose() }
    } finally { $graphStream.Dispose() }
    $graphBodies = @($graphBodies | Sort-Object @{ Expression = { $_.TypeName } },
        @{ Expression = { $_.MethodName } }, @{ Expression = { $_.MetadataToken } })
    $actualGraphMethods = (@($graphBodies.MethodName | Sort-Object) -join '|')
    $expectedGraphMethods = (@($graphMethodNames | Sort-Object) -join '|')
    if ($graphBodies.Count -ne 5 -or $actualGraphMethods -ne $expectedGraphMethods) {
        throw 'The exact five-method managed graph body set was not found in the fixture metadata.'
    }
    $graphRootBody = @($graphBodies | Where-Object MethodName -eq 'ParameterlessRoot')[0]
    $graphAssemblySha = Get-Sha256 $graphFixture
    $graphProfile = 'HybridCPU.DotNetAot.ScalarControlFlowV2'
    $graphBodyDigestRows = @($graphBodies | ForEach-Object {
        "$($_.TypeName)::$($_.MethodName):0x$($_.MetadataToken.ToString('x8', [Globalization.CultureInfo]::InvariantCulture))"
    })
    $graphDigestPayload = @(
        'hybridcpu.nativeaot-body-presentation/v1', 2, $graphProfile, $graphAssemblySha,
        "$($graphRootBody.TypeName)::$($graphRootBody.MethodName):0x$($graphRootBody.MetadataToken.ToString('x8', [Globalization.CultureInfo]::InvariantCulture))",
        ($graphBodyDigestRows -join ';')) -join '|'
    $graphContractDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($graphDigestPayload))).ToLowerInvariant()
    [ordered]@{
        SchemaId = 'hybridcpu.nativeaot-body-presentation/v1'; SchemaVersion = 2; ProfileId = $graphProfile
        AssemblySha256 = $graphAssemblySha; Root = $graphRootBody; PresentedBodies = $graphBodies
        ContractDigest = $graphContractDigest
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $graphPresentation -Encoding utf8NoBOM
    $graphConfiguration = [ordered]@{
        ManifestPath = $graphFacts; InputIrPath = $graphInput; ProducerPath = $emitter
        MetadataPath = $graphMetadata; HostPath = $dotnetHost; DeriveFromCanonicalIr = $true
    } | ConvertTo-Json -Compress
    $priorPresentation = [Environment]::GetEnvironmentVariable('HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1')
    try {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $graphConfiguration)
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1', $graphPresentation)
        $presentedOutput = & dotnet $adapter compile-image --assembly $graphFixture `
            --type $graphType --method ParameterlessRoot `
            --out $graphBinary --source-commit '94ea82652cdd4e0f8046b5bd5becbd11461482ca' `
            --patch-digest 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9' 2>&1 | Out-String
        $presentedExit = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $priorSideband)
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1', $priorPresentation)
    }
    if ($presentedExit -ne 0 -or -not (Test-Path -LiteralPath $graphBinary -PathType Leaf) -or
        -not (Test-Path -LiteralPath ($graphBinary + '.json') -PathType Leaf) -or
        -not (Test-Path -LiteralPath $graphMetadata -PathType Leaf) -or
        -not (Test-Path -LiteralPath $graphFacts -PathType Leaf) -or
        -not (Test-Path -LiteralPath $graphInput -PathType Leaf)) {
        throw "Compiler-derived PCL managed-graph integration failed. exit=$presentedExit`n$presentedOutput"
    }
    $graphManifest = Get-Content -Raw -LiteralPath $graphFacts | ConvertFrom-Json
    $graphArtifact = Get-Content -Raw -LiteralPath ($graphBinary + '.json') | ConvertFrom-Json
    $graphSnapshot = Get-Content -Raw -LiteralPath $graphInput | ConvertFrom-Json
    $graphMetadataOutputDigest = ((Get-Content -LiteralPath $graphMetadata) |
        Where-Object { $_.StartsWith('output=', [StringComparison]::Ordinal) }) -replace '^output=', ''
    if ($graphManifest.CompilerContractVersion -ne 'hybridcpu-nativeaot-canonical-ir-graph-v1' -or
        @($graphManifest.SafePointMap).Count -eq 0 -or @($graphManifest.StaticResourceEstimates).Count -ne 3 -or
        $graphSnapshot.SchemaId -ne 'hybridcpu.singnext-pcl-canonical-ir-graph/v1' -or
        @($graphSnapshot.Methods).Count -ne 5 -or $graphArtifact.CompiledMethodCount -ne 5 -or
        $graphMetadataOutputDigest -ne (Get-Sha256 $graphBinary)) {
        throw 'Compiler-derived managed-graph PCL facts, snapshot, artifact, or output binding are incomplete.'
    }
    $firstGraphTuple = [ordered]@{
        manifest = Get-Sha256 $graphFacts; inputIr = Get-Sha256 $graphInput
        output = Get-Sha256 $graphBinary; nativeManifest = Get-Sha256 ($graphBinary + '.json')
        metadata = Get-Sha256 $graphMetadata
    }
    try {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $graphConfiguration)
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1', $graphPresentation)
        $presentedRepeatOutput = & dotnet $adapter compile-image --assembly $graphFixture `
            --type $graphType --method ParameterlessRoot `
            --out $graphBinary --source-commit '94ea82652cdd4e0f8046b5bd5becbd11461482ca' `
            --patch-digest 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9' 2>&1 | Out-String
        $presentedRepeatExit = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1', $priorSideband)
        [Environment]::SetEnvironmentVariable('HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1', $priorPresentation)
    }
    $secondGraphTuple = [ordered]@{
        manifest = Get-Sha256 $graphFacts; inputIr = Get-Sha256 $graphInput
        output = Get-Sha256 $graphBinary; nativeManifest = Get-Sha256 ($graphBinary + '.json')
        metadata = Get-Sha256 $graphMetadata
    }
    if ($presentedRepeatExit -ne 0 -or ($firstGraphTuple | ConvertTo-Json -Compress) -ne
        ($secondGraphTuple | ConvertTo-Json -Compress)) {
        throw "Compiler-derived managed-graph PCL output is not deterministic. exit=$presentedRepeatExit`n$presentedRepeatOutput"
    }

    $performancePath = Join-Path $integrationRoot 'pcl-performance.json'
    $performanceProject = Join-Path $RepositoryRoot 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj'
    $performanceOutput = & dotnet run --project $performanceProject -c Release --no-restore -- `
        --v6-pcl-performance --output $performancePath --iterations 10000 --rounds 15 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $performancePath -PathType Leaf)) {
        throw "PCL performance campaign failed.`n$performanceOutput"
    }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.Schema -ne 'singnext.v6.pcl-performance/1' -or
        @($performance.Samples).Count -ne 30 -or
        @($performance.Samples | Where-Object Mode -eq 'cache-miss').Count -ne 15 -or
        @($performance.Samples | Where-Object Mode -eq 'cache-hit').Count -ne 15) {
        throw 'PCL performance campaign emitted an unexpected schema or sample matrix.'
    }

    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $hybridCpuEvidence = @($hybridCpuInputs | ForEach-Object {
        $path = Join-Path $HybridCpuRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "HybridCPU evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $hybridChangeManifest = @(
        [ordered]@{
            path = 'Compilers/HybridCPU_Compiler/NativeAot/Program.cs'
            beforeSha256 = '95c52f758283de50bc2f04e5296acb63efdc324b9361530e7559aada4353ea97'
            beforeBasis = 'recovered from two byte-identical sibling baseline copies; no-index diff contains only this additive slice'
            afterSha256 = Get-Sha256 (Join-Path $HybridCpuRoot 'Compilers/HybridCPU_Compiler/NativeAot/Program.cs')
        },
        [ordered]@{
            path = 'Compilers/HybridCPU_Compiler/NativeAot/SingNextPclSidebandV1.cs'
            beforeSha256 = $null
            beforeBasis = 'file absent before this slice'
            afterSha256 = Get-Sha256 (Join-Path $HybridCpuRoot 'Compilers/HybridCPU_Compiler/NativeAot/SingNextPclSidebandV1.cs')
        },
        [ordered]@{
            path = 'Compilers/HybridCPU_Compiler/NativeAot/SingNextPclCanonicalIrV1.cs'
            beforeSha256 = $null
            beforeBasis = 'file absent before this slice'
            afterSha256 = Get-Sha256 (Join-Path $HybridCpuRoot 'Compilers/HybridCPU_Compiler/NativeAot/SingNextPclCanonicalIrV1.cs')
        },
        [ordered]@{
            path = 'Compilers/HybridCPU_Compiler/NativeAot/Properties/AssemblyInfo.cs'
            beforeSha256 = $null
            beforeBasis = 'file absent before this slice; grants internal test access only'
            afterSha256 = Get-Sha256 (Join-Path $HybridCpuRoot 'Compilers/HybridCPU_Compiler/NativeAot/Properties/AssemblyInfo.cs')
        }
    )
    $payload = (@($fileEvidence | ForEach-Object { "$($_.sha256)  SingNextOS/$($_.path)" }) +
        @($hybridCpuEvidence | ForEach-Object { "$($_.sha256)  HybridCPU ISE/$($_.path)" })) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performanceArtifactPath = Join-Path $OutputDirectory 'performance.json'
    Copy-Item -LiteralPath $performancePath -Destination $performanceArtifactPath -Force
    $performanceDigest = Get-Sha256 $performanceArtifactPath
    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('PCL-A', 'PCL-B', 'PCL-C', 'PCL-D-build-emitter', 'PCL-E-hybridcpu-nativeaot-sideband',
            'PCL-F-independent-static-fact-policy-binding', 'PCL-G-compiler-derived-canonical-ir-inputs',
            'PCL-H-exact-memory-footprint-fail-closed', 'PCL-I-repeated-check-performance-characterization',
            'PCL-J-safe-point-map-live-revalidation',
            'PCL-K-static-resource-estimate-live-policy',
            'PCL-L-multi-shape-workload-campaign',
            'PCL-M-managed-graph-sideband',
            'PCL-N-typed-vector-transfer-sideband',
            'PCL-O-typed-matrix-memory-sideband',
            'PCL-P-typed-matrix-compute-sideband',
            'PCL-Q-exact-dsc-l7-footprint-projection',
            'PCL-R-descriptor-footprint-independent-fact-policy',
            'PCL-S-proof-absent-live-admission-differential',
            'PCL-T-typed-static-cache-identity',
            'PCL-U-bounded-static-decision-cache')
        slice = 'minimal-compiler-lowering-evidence-static-cache-and-nativeaot-post-lowering-emission'
        contour = 'managed-build-tool/hybridcpu-nativeaot/runtime/exact-file-digests/static-facts/live-checks-preserved'
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim(); sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim(); targetFramework = 'net11.0'
            compiler = 'HybridCPU NativeAOT restricted single-method/managed-graph opt-in sideband'
            runtime = 'V6CompilerLoweringEvidenceVerifier static admission prototype; no product caller'
        }
        requirementIds = @('PCL-EXACT-OUTPUT-BINDING-01','PCL-LIVE-AUTHORITY-INDEPENDENCE-01',
            'PCL-REPEATED-STATIC-CHECK-PROFILE-01','PCL-RUNTIME-CONSUMER-BOUNDARY-01')
        requirementClassification = [ordered]@{
            VerifiedExisting = @('versioned canonical metadata and exact output/toolchain binding',
                'live authority and runtime legality callbacks run on every verifier admission, including cache hits')
            Partial = @('opt-in NativeAOT producer and prototype verifier have no connected product runtime consumer',
                'named-host repeated-check saving has no accepted product threshold or workload replication')
            FutureGated = @('PCL policy promotion and production TCB inclusion')
        }
        tests = [ordered]@{
            singNext = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
            hybridCpuNativeAot = [ordered]@{ passed = $hybridPassed; failed = $hybridFailed; skipped = $hybridSkipped; total = $hybridTotal }
            crossProjectIntegration = [ordered]@{ passed = 18; failed = 0; total = 18 }
            aggregate = [ordered]@{ passed = ($passed + $hybridPassed + 18); failed = 0; skipped = 0; total = ($total + $hybridTotal + 18) }
        }
        featureGate = [ordered]@{
            roadmapGate = 'V6-PROOF-CARRYING-LOWERING'
            implementationGate = 'V6-PROOF-CARRYING-LOWERING'; state = 'OFF'
        }
        coverage = [ordered]@{
            facts = @('region-relative footprint', 'alias declaration', 'ordering preservation', 'numeric mode',
                'safe-point encoded location and live-state-shape digest',
                'advisory canonical instruction/block/maximum-operand upper bounds')
            binding = @('schema/version', 'compiler contract', 'toolchain digest', 'input IR digest',
                'exact output digest', 'producer digest', 'verifier policy generation',
                'independently required canonical static-fact-set digest',
                'independently required safe-point-map digest',
                'independently required static-resource-estimate digest')
            mutation = @('output byte', 'schema', 'version', 'toolchain', 'compiler contract', 'producer', 'canonical digest')
            factPolicy = @('required footprint without expected fact-set digest rejects',
                'forged alias set rejects before live checks', 'fact-set digest excludes artifact identity',
                'required fact-set digest participates in cache identity')
            metadataTransport = @('bounded canonical UTF-8', 'strict LF form', 'unknown fact reject',
                'canonical re-emission equality', 'runtime admission from exact metadata bytes')
            emitter = @('strict bounded fact manifest', 'input IR digest', 'exact final-output digest',
                'toolchain and producer binary digests', 'read-only stable input handles',
                'distinct path enforcement', 'atomic same-directory sidecar replacement',
                'emitter-to-runtime-verifier acceptance with repeated live checks')
            hybridCpuSideband = @('real NativeAOT final output boundary', 'opt-in strict bounded configuration',
                'exact adapter assembly as toolchain', 'exact SingNext emitter assembly as producer',
                'positive exact-output digest match', 'producer failure returns nonzero',
                'five-method adapter-presented graph after import/link/workstream completion',
                'failed requested evidence deletes output, manifest, and stale metadata')
            compilerDerivedInputs = @('validated canonical IR snapshot',
                'caller manifest ignored in compiler-derived mode', 'integer numeric-mode facts from canonical semantics',
                'exact memory ranges and mechanical disjointness where present',
                'declared read/write without an exact direction-consistent range rejects',
                'annotation and canonical side-effect region disagreement rejects',
                'managed safe-point location and live-state shape derived from canonical IR',
                'instruction count, basic-block count, and maximum operands per instruction derived from canonical IR',
                'bounded deterministic repeat emission',
                'deterministic graph aggregate with method-namespaced facts',
                'typed vector-transfer sideband snapshot with no-fallback validation',
                'typed MatrixTile load/store sideband snapshot with exact validated footprints',
                'typed MatrixTile compute/transpose sideband snapshot with proven no-external-memory projection',
                'authority-free exact DSC/L7 descriptor footprint projections',
                'missing or malformed descriptor projections and typed effects reject with deletion')
            authority = @('capability revoke still denies', 'runtime legality still denies',
                'safe-point map requires a live runtime/provider callback on every admission',
                'static resource estimates require a fresh generation-bound advisory policy callback',
                'proof and receipt grant no execution authority', 'absence uses reference fallback when optional')
            cache = @('exact tuple key', 'policy-generation invalidation',
                'one static verification for two identical uses', 'live checks execute on every use')
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll',
            'tools/SingPlus.LoweringEvidenceEmitter/bin/Debug/net11.0/SingPlus.LoweringEvidenceEmitter.dll'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing binary evidence: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        hybridCpuBinaryInputs = @($adapter, $fixture) | ForEach-Object {
            if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) { throw "Missing HybridCPU binary evidence: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $_).Length; sha256 = Get-Sha256 $_ }
        }
        javaDependentChecks = [ordered]@{
            state = 'SkippedByInstruction'
            claimLimit = 'No Java-dependent cross-language confirmation, physical-provider, or production claim is inferred.'
        }
        publicApiPackageSchemaDelta = 'No public API, package, or schema change; qualification evidence tuple and claim boundary only.'
        hybridCpuIdentity = [ordered]@{
            gitMetadataAvailable = $false
            identity = 'reproducible source inventory; roadmap SHA is not asserted as current checkout identity'
            sourceInventory = $hybridCpuEvidence
            changeManifest = $hybridChangeManifest
            positiveOutputBytes = $positiveOutputBytes
            positiveOutputSha256 = $outputDigest
            positiveMetadataBytes = $positiveMetadataBytes
            derivedManifestSha256 = $firstDerivedTuple.manifest
            derivedInputIrSha256 = $firstDerivedTuple.inputIr
            derivedFootprintCount = @($derivedManifest.Footprints).Count
            derivedNumericFactCount = @($derivedManifest.NumericFacts).Count
            derivedSafePointCount = @($derivedManifest.SafePointMap).Count
            derivedStaticResourceEstimateCount = @($derivedManifest.StaticResourceEstimates).Count
            managedGraphCompiledMethodCount = [int]$graphArtifact.CompiledMethodCount
            managedGraphManifestSha256 = $firstGraphTuple.manifest
            managedGraphInputIrSha256 = $firstGraphTuple.inputIr
            managedGraphOutputSha256 = $firstGraphTuple.output
            managedGraphMetadataSha256 = $firstGraphTuple.metadata
        }
        workloadCampaign = [ordered]@{
            interpretation = 'Named-host process-level build/emission characterization only; timings include process startup and are not a product threshold.'
            positiveCaseCount = 4
            negativeCaseCount = 7
            baseline = [ordered]@{
                name = 'scalar-add'; expected = 'success'; type = 'RestrictedCilCSharpFixtures'; method = 'Add'
                outputBytes = $positiveOutputBytes; outputSha256 = $outputDigest
                metadataBytes = $positiveMetadataBytes; deterministic = $true
            }
            cases = $corpusEvidence
        }
        performanceCampaign = [ordered]@{
            reportPath = 'performance.json'
            reportSha256 = $performanceDigest
            environment = $performance.Environment
            methodology = $performance.Methodology
            summary = $performance.Summary
            claimBoundary = $performance.ClaimBoundary
        }
        fullSuiteInfrastructureFailures = @(
            [ordered]@{
                command = 'dotnet build "HybridCPU v2.slnx" --no-restore'
                observedOutsideThisQualificationScript = $true
                result = 'failed: 81 errors, 72 warnings'
                scope = 'HybridCPU_EnvGUI legacy callers of removed CPU_Core Move_Num/Load/Store members and instruction-type mismatches'
                relationToPclSlice = 'unrelated; NativeAOT adapter project, focused test project, and cross-project PCL checks passed'
            }
        )
        maximumSupportedClaim = [ordered]@{
            proofLanguage = 'StaticAdmission'
            canonicalization = 'RuntimeEnforced construction, strict metadata parsing, and envelope validation'
            compiledMetadataTransport = 'StaticAdmission through bounded canonical codec and runtime parser'
            staticFactPolicyBinding = 'StaticAdmission against an independently required exact canonical fact-set digest'
            buildSideEmitter = 'ExecutableAdapter for exact post-lowering files; emits no authority'
            hybridCpuNativeAotSideband = 'ExecutableAdapter for opt-in single-method and adapter-presented managed-graph NativeAOT output seams; no authority or legality effect'
            compilerDerivedFacts = 'ExecutableAdapter for conservative canonical-IR snapshots, typed no-fallback vector-transfer sidebands, and complete exact-region facts in the restricted single-method and five-method presented-graph seams; incomplete memory evidence rejects'
            safePointMap = 'StaticAdmission for exact-output-bound locations/live-state shapes only after independent policy-digest binding and a repeated live runtime/provider validation callback'
            staticResourceEstimates = 'StaticAdmission advisory structural upper bounds only after independent digest binding and fresh generation-bound policy validation; no reservation, capacity, time, energy, or authority claim'
            cacheSaving = 'ModelOnly deterministic static-verifier invocation reduction only'
            runtimeAuthority = 'ModelOnly proof object; live owners remain authoritative'
            performanceBenefit = 'ModelOnly host-specific repeated-check characterization only; see performance.json. No product threshold or production claim.'
            hardware = 'FutureGated'; production = 'FutureGated'
        }
        remainingBlockers = @(
            'V6-PROOF-CARRYING-LOWERING remains OFF.',
            'No product runtime admission path calls V6CompilerLoweringEvidenceVerifier; current calls are tests and qualification tools, so measured static-cache savings are not a deployed optimization.',
            'Only the explicit HybridCPU NativeAOT single-method and adapter-presented managed-graph output seams are wired; other compiler pipelines are not.',
            'DSC/L7 proof support is limited to exact authority-free footprint projections; raw descriptors, completion, and runtime legality remain outside PCL.',
            'Named-host verifier and multi-shape compiler campaigns exist, but no accepted product threshold or isolated-host replication justifies production TCB inclusion.',
            'Proof facts never replace live authority, provider admission, refinement, or runtime legality.'
        )
        rollbackFallback = 'Keep the gate OFF and execute the existing reference runtime validation path.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# Proof-Carrying Lowering static evidence

- Result: $($passed + $hybridPassed + 18)/$($total + $hybridTotal + 18) selected SingNext, HybridCPU NativeAOT, and cross-project integration checks passed.
- Claim: StaticAdmission only for immutable lowering facts bound to one exact output/toolchain tuple.
- Metadata: bounded canonical bytes round-trip into runtime admission; unknown, reordered, oversized, or mutated metadata fails closed.
- Emitter: the executable post-lowering tool hashes exact IR/output/toolchain/producer files under stable read handles, rejects path aliasing and unknown manifest fields, and atomically publishes the sidecar.
- HybridCPU integration: the opt-in NativeAOT sideband derives bounded deterministic snapshots and conservative facts from actual validated single-method canonical IR and a five-method adapter-presented graph; typed VectorTransfer, all four MatrixTile lowering forms, and authority-free DSC/L7 exact-footprint projections are snapshotted only after fail-closed validation, graph derivation runs after import/link/workstream completion, and emission binds the exact IR/adapter/emitter/output tuple and quarantines failed output.
- Cache result: two identical uses require one static verification while both repeat live authority and runtime-legality checks; proof-present/cache-hit and proof-absent paths make identical live decisions after authority and legality state changes. A typed cache key prevents delimiter-bearing contract names from colliding with altered toolchain expectations; the 1024-entry FIFO cap bounds retained static decisions, and evicted tuples are reverified without skipping live gates.
- Fact policy: mandatory footprints require an independently expected canonical fact-set digest; a valid-shape DSC range substitution changes PCL's emitted facts and runtime admission rejects it against the original expectation before live checks. PCL's projection validation alone is not independent descriptor authority; alias/order/numeric substitution and cache reuse across a changed expectation also fail closed.
- Safe-point map: the restricted compiler seam derives exact encoded locations and live-state-shape digests; use requires an independent expected map digest and a fresh runtime/provider callback even on cache hits.
- Static resource estimates: canonical instruction/block/maximum-operand upper bounds are advisory only; acceptance requires an independent digest and fresh generation-bound live policy check and never reserves capacity.
- Performance: 15 alternating Release batches per mode compare forced static-cache misses with identical-tuple cache hits; the named-host result is recorded in `performance.json` without a product or production threshold claim.
- Consumer boundary: the NativeAOT producer is opt-in and the static verifier is called by tests and qualification tools only. No product runtime admission path consumes PCL metadata, so cache savings are not a deployed optimization.
- Workload campaign: four positive scalar/helper/control-flow forms are exact-output-bound and deterministic across repeated compilation; seven unsupported opcode/SSA/budget/call-graph/array forms fail closed without output or metadata. Process-level timings are characterization only.
- Gate: `V6-PROOF-CARRYING-LOWERING` remains OFF.
- Java-dependent checks: skipped by instruction; no cross-language confirmation is claimed.
- Full HybridCPU solution: the separately executed no-restore build is blocked by 81 unrelated `HybridCPU_EnvGUI` legacy API/type errors; the changed NativeAOT project and its focused tests build successfully.
- Not claimed: all-pipeline compiler wiring, raw DSC/L7 descriptor authority or completion, execution permission, runtime legality, latency benefit, hardware, or production qualification.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($jsonPath, $markdownPath, $performanceArtifactPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally {
    if ($integrationRoot) {
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedIntegration = [IO.Path]::GetFullPath($integrationRoot)
        if ($resolvedIntegration.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedIntegration).StartsWith('singnext-pcl-integration-', [StringComparison]::Ordinal)) {
            [IO.Directory]::Delete($resolvedIntegration, $true)
        }
    }
    Pop-Location
}
