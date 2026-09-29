[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p09-device-trust')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$filter = 'FullyQualifiedName~DeviceTrustContractsV1Tests|FullyQualifiedName~CxlSecurityAndMultiHostTests|FullyQualifiedName~CxlType2AcceleratorServiceTests|FullyQualifiedName~PlatformDeviceLeaseTests|FullyQualifiedName~SecureExecutionBindingTests|FullyQualifiedName~Phase7EvidenceSecureComputeTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/DeviceTrustContracts.cs',
    'contracts/SingPlus.Contracts/EvidenceContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/CxlSecurityContracts.cs',
    'src/Platform/SingPlus.Platform.Abstractions/PlatformDeviceLeaseContracts.cs',
    'src/Platform/SingPlus.Platform.Host/CxlSecurityModelProvider.cs',
    'src/Runtime/SingPlus.Runtime/Cxl/CxlDeviceTrustEvidenceAdapter.cs',
    'src/Runtime/SingPlus.Runtime/Cxl/CxlSecurityAuthority.cs',
    'src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Device.cs',
    'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.SecureCompute.cs',
    'src/Runtime/SingPlus.Runtime/SecureCompute/RuntimeKernel.SecureCompute.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tests/SingPlus.Tests/Contracts/DeviceTrustContractsV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/CxlSecurityAndMultiHostTests.cs',
    'tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs',
    'tests/SingPlus.Tests/Platform/PlatformDeviceLeaseTests.cs',
    'tests/SingPlus.Tests/Platform/SecureExecutionBindingTests.cs',
    'tests/SingPlus.Tests/Platform/Phase7EvidenceSecureComputeTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P09DeviceTrustPerformanceQualification.cs',
    'eng/v6/Qualify-V6P09Trust.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P09 trust tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The P09 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value; $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value; $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 172 -or $skipped -ne 0 -or $total -ne 172) {
        throw "Unexpected P09 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p09-device-trust-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P09 device-trust performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p09-device-trust-performance/1' -or
        $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
        throw 'The P09 device-trust performance artifact is malformed.'
    }
    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $payload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('P09-A', 'P09-B-model', 'P09-C-model',
            'P09-C-post-security-query-revalidation', 'P09-model-performance')
        slice = 'provider-neutral-trust-predicate-and-cxl-model-freshness'
        contour = 'single-host/managed/CXL-model/staged-Type2'
        requirementIds = @('P09-TRUST-PREDICATE-01', 'P09-FINAL-TRUST-REVALIDATION-01',
            'P09-MODEL-RESET-QUERY-COHERENCE-01',
            'P09-VISIBILITY-TRUST-REVALIDATION-01',
            'P09-PUBLICATION-ACTION-TRUST-DRIFT-01')
        testIds = @('DeviceTrustContractsV1Tests.AnyMutableGenerationDriftRejectsReplayedEvidence',
            'CxlSecurityAndMultiHostTests.DeviceRebindDuringSecurityQueryCannotReturnStaleReadiness',
            'CxlSecurityAndMultiHostTests.UpdatedCxlEnvelopeCannotMakePreResetTrustObligationFreshAgain',
            'CxlType2AcceleratorServiceTests.TrustEvidenceLossAfterOwnerAdmissionIsRevalidatedBeforeProviderEffect',
            'CxlSecurityAndMultiHostTests.ConcurrentSecurityResetAndQueryReturnOneCoherentGenerationTuple',
            'CxlType2AcceleratorServiceTests.TrustResetDuringVisibilityCannotPublishStagedType2Output',
            'CxlType2AcceleratorServiceTests.TrustResetInsidePublicationActionRetainsAmbiguousEffect')
        requirementClassification = [ordered]@{
            VerifiedExisting = @('CXL Type-2 secure submit has a live model-sideband trust predicate consumer',
                'device and fabric owners revalidate after the security query callback')
            Partial = @('the producer is a deterministic model and the trust query is not a physical attestation handshake')
            ExternalBlocked = @('no named hardware trust root, firmware tuple, certificate or revocation producer')
            FutureGated = @('physical SPDM/TDISP/IDE attestation and production secure assignment')
        }
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim(); sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim(); targetFramework = 'net11.0'
            provider = 'CxlSecurityModelProvider/ModelOnly'
            dependency = 'CxlAuthorityBridge/device-lease/Region-use/CxlType2'
        }
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        commandsActuallyRun = @(
            'dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter <P09 selected filter>',
            'dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p09-device-trust-performance --output artifacts\v6\p09-device-trust\performance.json'
        )
        javaDependentChecks = [ordered]@{
            state = 'SkippedByInstruction'
            claimLimit = 'No Java-dependent cross-language or external producer confirmation is inferred; physical attestation and production claims remain FutureGated.'
        }
        featureGate = [ordered]@{ roadmapGate = 'V6-DEVICE-ATTESTATION'; implementationGate = 'V6-DEVICE-ATTESTATION'; state = 'OFF' }
        ownership = [ordered]@{
            policyEvaluator = 'predicate comparison only'
            capabilityAndDeviceLease = 'device permission and assignment authority'
            regionAuthority = 'Region access authority'
            provider = 'sideband measurement and freshness generations'
            externalOperationAuthority = 'submit, completion, publication, and release lifecycle'
        }
        coverage = [ordered]@{
            schema = @('device identity', 'firmware measurement', 'assurance class',
                'provider trust/device/assignment/reset/policy generations', 'evidence sequence')
            negative = @('replay after reset', 'wrong device', 'wrong firmware', 'insufficient assurance',
                'assignment mismatch', 'unknown mandatory assurance', 'malformed evidence')
            runtime = @('ordinary capability/device/Region validation remains required',
                'fresh trust query after owner admission and immediately before provider submit',
                'device/fabric authority revalidated after security callback before readiness or optional fallback',
                'snapshot endpoint/generation correlation and callback exception fail closed',
                'evidence loss closes without provider effect', 'publication-time revalidation',
                'reset during provider visibility callback is rechecked before staged publication',
                'reset during publication action preserves the exact owner ambiguous-effect state without Published or Released',
                'model reset/query concurrency returns one coherent provider-trust/evidence/reset tuple')
            authority = @('obligation, evidence, adapter projection, and decision grant no authority')
            performance = @('provider-neutral evidence validation', 'exact nine-field predicate evaluation',
                'CXL sideband projection plus predicate evaluation',
                'fresh deterministic CXL model query plus projection and predicate evaluation',
                'rotating Release JIT named-host rounds', 'median/p95/p99 elapsed time', 'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            evidenceValidationMedianNanoseconds = [double]$performance.summary.evidenceValidationMedianNanoseconds
            predicateEvaluationMedianNanoseconds = [double]$performance.summary.predicateEvaluationMedianNanoseconds
            sidebandProjectionAndPredicateMedianNanoseconds = [double]$performance.summary.sidebandProjectionAndPredicateMedianNanoseconds
            modelQueryProjectionAndPredicateMedianNanoseconds = [double]$performance.summary.modelQueryProjectionAndPredicateMedianNanoseconds
            supportsGatePromotion = $false
            interpretation = 'Provider-neutral predicate and deterministic in-memory CXL model overhead only; no physical attestation handshake, signature, certificate, or revocation cost.'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Platform.Host.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        changedFilesThisIteration = @('src/Platform/SingPlus.Platform.Host/CxlSecurityModelProvider.cs',
            'src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs',
            'tests/SingPlus.Tests/Runtime/CxlSecurityAndMultiHostTests.cs',
            'tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs',
            'eng/v6/Qualify-V6P09Trust.ps1')
        publicApiPackageSchemaDelta = 'No public API, package, or schema change; model provider reset/query synchronization only.'
        maximumSupportedClaim = [ordered]@{
            trustSchemaAndPolicy = 'StaticAdmission'
            cxlModelSidebandAdapter = 'RuntimeEnforced on the named managed model contour'
            cxlModelTrustPerformance = 'ModelOnly predicate/projection/model-query overhead only; physical attestation latency remains FutureGated'
            physicalSpdmTdispIde = 'FutureGated'
            hybridCpuTrustProducer = 'FutureGated'
            hardwareAttestation = 'FutureGated'
            production = 'FutureGated'
        }
        remainingBlockers = @(
            'V6-DEVICE-ATTESTATION remains OFF.',
            'The CXL provider is a deterministic ModelOnly producer, not a hardware root of trust.',
            'No named SPDM/TDISP/IDE or HybridCPU physical trust producer and firmware tuple is integrated.',
            'No hardware trust root, certificate/revocation service, production qualification, or TLA+ model exists.'
        )
        rollbackFallback = 'Keep the trust gate OFF; deny trust-required work or select an explicitly non-attested contour without silent downgrade.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P09 device trust evidence

- Result: $passed/$total selected trust, CXL model, device-lease, secure-compute, protected Type-2 boundary, and gate tests passed. The three new protected-label tests enter through the broad CxlType2AcceleratorServiceTests filter; they do not strengthen physical trust claims.
- Claims: provider-neutral trust predicates = StaticAdmission; CXL model sideband/revalidation = RuntimeEnforced only on the named managed model contour.
- Freshness tuple: provider trust, device, assignment, reset, policy, and evidence generations.
- Safety: trust evidence grants no capability, lease, Region access, execution, or publication authority. Device/fabric authority is revalidated after the security callback; rebind during that callback, mismatched snapshot identity, and provider exceptions cannot return readiness.
- Model reset/query: the in-memory producer serializes reset, registration, and snapshot reads; a query sees one coherent freshness tuple and health state. This does not establish physical attestation freshness.
- Publication boundary: a trust reset during the provider visibility callback is rechecked before staged publication; the managed operation closes without publishing output.
- Publication action: reset after the callback may have published output is detected before the owner records Published; the exact operation retains PublicationEffectAmbiguous and cannot be released by ordinary closure.
- Named-host model medians: evidence validation = $([double]$performance.summary.evidenceValidationMedianNanoseconds) ns; predicate evaluation = $([double]$performance.summary.predicateEvaluationMedianNanoseconds) ns; sideband projection + predicate = $([double]$performance.summary.sidebandProjectionAndPredicateMedianNanoseconds) ns; fresh model query + projection + predicate = $([double]$performance.summary.modelQueryProjectionAndPredicateMedianNanoseconds) ns.
- Performance boundary: deterministic managed predicate/model work only; no nonce exchange, signature verification, certificate/revocation service, SPDM/TDISP/IDE handshake, physical firmware, or gate promotion is inferred.
- Gate: `V6-DEVICE-ATTESTATION` remains OFF.
- Java-dependent checks: skipped by instruction; no cross-language or physical-producer claim is inferred.
- Not claimed: SPDM/TDISP/IDE hardware, HybridCPU trust producer, hardware attestation, production, or formal refinement.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($performancePath, $jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
