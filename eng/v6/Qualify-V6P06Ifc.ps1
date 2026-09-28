[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p06-protected-ifc')
)
$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$filter = 'FullyQualifiedName~InformationFlowContractsV1Tests|FullyQualifiedName~InformationFlowFiniteModelV1Tests|FullyQualifiedName~V6ProtectedInformationFlowTests|FullyQualifiedName~SipJobProtectedLabelFlowTests|FullyQualifiedName~SipJobProtectedTransportExecutorTests|FullyQualifiedName~V6ProtectedAcceleratorLabelFlowTests|FullyQualifiedName~CapabilityAuthorityTests|FullyQualifiedName~Phase7EvidenceSecureComputeTests|FullyQualifiedName~Phase145ABarrierPlannerTests|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelsReachExactManagedType2ProviderSubmitBoundary|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelsWithWrongProviderGenerationFailBeforeProviderEffect|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelLaunderingFailsBeforeProviderEffect|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProviderWithoutProtectedBoundaryIsRejectedBeforeOperationOrEffect|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelsNeverReplaceOrdinaryRegionAuthority|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedSidebandWithoutLiveRegionLabelsCannotReachProvider|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.StaleProtectedInputLabelGenerationCannotReachProvider'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.LiveProtectedType2OperationPinsInputLabelUntilClosure'
$inputs = @(
 'contracts/SingPlus.Contracts/InformationFlowContracts.cs', 'contracts/SingPlus.Contracts/CapabilityResourceIds.cs',
 'src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs', 'src/Runtime/SingPlus.Runtime/V6/V6ProtectedInformationFlow.cs',
 'src/Runtime/SingPlus.Runtime/SipJobs/SipJobProtectedLabelFlow.cs', 'src/Runtime/SingPlus.Runtime/SipJobs/SipJobResourceTransportExecutor.cs',
 'src/Runtime/SingPlus.Runtime/V6/V6ProtectedAcceleratorLabelFlow.cs',
 'src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs',
 'src/Platform/SingPlus.Platform.Abstractions/CxlAcceleratorContracts.cs',
 'src/Platform/SingPlus.Platform.Host/CxlType2ModelAccelerator.cs',
 'src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs', 'src/Runtime/SingPlus.Runtime/Capabilities/OperationAuthority.cs',
 'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs', 'tests/SingPlus.Tests/Contracts/InformationFlowContractsV1Tests.cs',
 'tests/SingPlus.Tests/Contracts/InformationFlowFiniteModelV1Tests.cs', 'tests/SingPlus.Tests/SipJobs/SipJobProtectedLabelFlowTests.cs',
 'tests/SingPlus.Tests/SipJobs/SipJobProtectedTransportExecutorTests.cs',
 'tests/SingPlus.Tests/Runtime/V6ProtectedAcceleratorLabelFlowTests.cs',
 'tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs',
 'tests/SingPlus.Tests/Runtime/V6ProtectedInformationFlowTests.cs', 'tests/SingPlus.Tests/Capabilities/CapabilityAuthorityTests.cs',
 'tests/SingPlus.Tests/Platform/Phase7EvidenceSecureComputeTests.cs', 'tests/SingPlus.Tests/SipJobs/Phase145ABarrierPlannerTests.cs',
 'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
 'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
 'tools/SingPlus.SingCapQualification/Program.cs',
 'tools/SingPlus.SingCapQualification/V6P06InformationFlowPerformanceQualification.cs',
 'eng/v6/Qualify-V6P06Ifc.ps1')
Push-Location $RepositoryRoot
try {
 $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
 if ($LASTEXITCODE -ne 0) { throw "P06 tests failed.`n$output" }
 $m = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
 if (-not $m.Success) { throw 'P06 summary was not parsed.' }
 $failed=[int]$m.Groups[1].Value; $passed=[int]$m.Groups[2].Value; $skipped=[int]$m.Groups[3].Value; $total=[int]$m.Groups[4].Value
 if ($failed -ne 0 -or $passed -ne 76 -or $skipped -ne 0 -or $total -ne 76) { throw "Unexpected P06 counts: $failed/$passed/$skipped/$total" }
 New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
 $performancePath = Join-Path $OutputDirectory 'performance.json'
 $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
  -c Release --no-restore -- --v6-p06-information-flow-performance --output $performancePath 2>&1 | Out-String
 if ($LASTEXITCODE -ne 0) { throw "P06 information-flow performance campaign failed.`n$performanceOutput" }
 $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
 if ($performance.schema -ne 'singnext.v6.p06-information-flow-performance/1' -or
  $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
  throw 'The P06 information-flow performance artifact is malformed.'
 }
 $files = @($inputs | ForEach-Object { $p=Join-Path $RepositoryRoot $_; if (!(Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing: $_" }; [ordered]@{path=$_;bytes=(Get-Item $p).Length;sha256=Get-Sha256 $p} })
 $payload=($files|ForEach-Object{"$($_.sha256)  $($_.path)"})-join"`n"
 $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
 $artifact=[ordered]@{
  schema='singnext.v6.qualification/1'; generatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); phase=@('P06','P06-C-runtime','P06-D-managed-provider','P06-managed-performance')
  slice='owned-region-sipjob-and-managed-type2-live-boundary-protected-label-propagation'; contour='single-process/owned-Region/internal-managed-runtime/closed-value-SipJob/managed-CXL-Type2-model'
  sourceAndDependencyTuple=[ordered]@{singNextHead=(& git rev-parse HEAD).Trim();sourceSetSha256=$digest;sdk=(& dotnet --version).Trim();targetFramework='net11.0';provider='CxlType2ModelAccelerator/protected-sideband';dependency='RegionAuthority/protected-label-generation; CapabilityAuthority/explicit-transition'}
   requirementIds=@('P06-LIVE-REGION-LABEL-BINDING-01','P06-ACTIVE-USE-LABEL-PIN-01','P06-INVALIDATED-USE-LABEL-PIN-01')
   testIds=@('CxlType2AcceleratorServiceTests.ProtectedSidebandWithoutLiveRegionLabelsCannotReachProvider','CxlType2AcceleratorServiceTests.StaleProtectedInputLabelGenerationCannotReachProvider','CxlType2AcceleratorServiceTests.LiveProtectedType2OperationPinsInputLabelUntilClosure','V6ProtectedInformationFlowTests.ActiveRegionUsePinsProtectedLabelAgainstTransition','V6ProtectedInformationFlowTests.ActiveOutputUsePinsProtectedLabelAgainstPropagation','V6ProtectedInformationFlowTests.InvalidatedUnreleasedUsePinsProtectedLabelChanges')
  requirementClassification=[ordered]@{VerifiedExisting=@('RegionAuthority owns protected label and generation; CapabilityAuthority owns explicit transition permission');Partial=@('named managed protected Type-2 path now checks live input/output labels before operation admission and provider submit; no physical provider enforcement');FutureGated=@('multi-input protected accelerator sideband','physical provider and whole-system IFC')}
  tests=[ordered]@{passed=$passed;failed=$failed;skipped=$skipped;total=$total}
  commandsActuallyRun=@('dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter <P06 selected filter>','dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p06-information-flow-performance --output artifacts\v6\p06-protected-ifc\performance.json')
  javaDependentChecks=[ordered]@{state='SkippedByInstruction';claimLimit='No Java-dependent cross-language confirmation is inferred; physical provider, whole-system noninterference and production remain FutureGated.'}
  featureGate=[ordered]@{roadmapGate='V6-IFC';implementationGate='V6-IFC';state='OFF'}
  coverage=[ordered]@{
   lattice=@('confidentiality join=max','integrity join=min','flow checks both dimensions','unknown versions deny','all 9 elements exhaustively satisfy semilattice/order laws','narrow joined-influence confinement checked exhaustively')
    runtime=@('unlabeled input denied','multi-input label join','ownership transfer carries label generation','stale label binding denied','active and invalidated unreleased Region uses pin label attach, transition and output propagation','exact SipJob plan and label revalidation immediately before live owner callback','ordinary and fused paths preserve the same protected binding','invalid sidecars invoke neither transport nor owner callback')
   authority=@('label grants no access','wrong capability resource denied','revoked capability denied','exact operation-authority lease authorizes explicit transition')
   boundaries=@('SecureCompute evidence remains non-authoritative','SipJob sidecars bind exact plan/edge/schema and preserve label across fusion/materialization','SipJob confidential/cross-runtime boundaries remain conservative barriers','accelerator input/intermediate/output sideband binds the actual external operation and provider generations without granting access','protected sideband matches live input/output Region label and generation before preparation and immediately before managed CXL Type-2 provider effect','unlabeled and stale sidebands fail before provider effect')
   performance=@('lattice join and flow-policy primitive baselines','live protected Region label query',
    'two-input protected Region propagation with owner validation and Region locks','exact two-edge SipJob plan/sidecar verification',
    'rotating Release JIT named-host rounds','median/p95/p99 elapsed time','managed-thread allocation')
  }
  performanceCampaign=[ordered]@{
   artifact='performance.json';sha256=Get-Sha256 $performancePath
   latticeJoinMedianNanoseconds=[double]$performance.summary.latticeJoinMedianNanoseconds
   flowPolicyCheckMedianNanoseconds=[double]$performance.summary.flowPolicyCheckMedianNanoseconds
   regionLabelQueryMedianNanoseconds=[double]$performance.summary.regionLabelQueryMedianNanoseconds
   twoInputRegionPropagationMedianNanoseconds=[double]$performance.summary.twoInputRegionPropagationMedianNanoseconds
   twoEdgeSipJobVerificationMedianNanoseconds=[double]$performance.summary.twoEdgeSipJobVerificationMedianNanoseconds
   supportsGatePromotion=$false
   interpretation='Protected-contour policy plus live managed Region tracking and SipJob admission overhead only; provider execution and physical enforcement remain outside the measured boundary.'
  }
  evidenceInputs=$files
  binaryAndPackageInputs=@('tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll','tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll','tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll')|ForEach-Object{$p=Join-Path $RepositoryRoot $_;if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw "Missing binary evidence: $_"};[ordered]@{path=$_;bytes=(Get-Item $p).Length;sha256=Get-Sha256 $p}}
  changedFilesThisIteration=@('src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs','src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs','tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs','tests/SingPlus.Tests/Runtime/V6ProtectedInformationFlowTests.cs','eng/v6/Qualify-V6P06Ifc.ps1')
  publicApiPackageSchemaDelta='No public API, package, or schema change; internal live-label sentry and active-use pin only.'
  maximumSupportedClaim=[ordered]@{
   labelAndPolicy='StaticAdmission';ownedRegionPropagation='RuntimeEnforced on internal managed protected contour'
   declassifyEndorse='RuntimeEnforced through existing CapabilityAuthority operation lease on named contour'
   sipSipJobPropagation='RuntimeEnforced at the internal SipJobResourceTransportExecutor live-owner boundary for exact closed plan edge/schema sidecars';acceleratorIntermediateLabels='RuntimeEnforced at the internal managed CXL Type-2 protected-provider submit boundary; provider guarantee remains ModelOnly';finiteLatticeAndNarrowNoninterference='ModelOnly exhaustive finite check';wholeSystemNoninterference='FutureGated';hardware='FutureGated';production='FutureGated'
   informationFlowPerformance='ModelOnly protected policy, managed Region tracking, and SipJob admission overhead; no physical-provider or gate-promotion claim'
  }
  remainingBlockers=@('V6-IFC remains OFF and entry points are internal.','Protected accelerator enforcement reaches only the named managed CXL Type-2 model provider; no physical hardware provider carries it.','Protected multi-input accelerator sideband is unsupported; the exact single-input contour fails closed for RightInput.','No external formal proof or whole-system noninterference claim is supported.','No hardware or production claim is supported.')
  rollbackFallback='Keep V6-IFC OFF; existing capability, sealing, and SecureCompute isolation remain authoritative.';isaImpact='NONE'
 }
 $json=Join-Path $OutputDirectory 'qualification.json';$md=Join-Path $OutputDirectory 'qualification.md'
 $artifact|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $json -Encoding utf8NoBOM
 @"
# P06 protected-contour IFC evidence

- Result: $passed/$total selected label, capability, SecureCompute-boundary, and SipJob-barrier tests passed.
- Named claim: owned-Region propagation and explicit capability-authorized declassify/endorse are RuntimeEnforced only in the internal managed contour.
- Gate: `V6-IFC` remains OFF; labels never grant access.
- Java-dependent checks: skipped by instruction; no cross-language or physical-provider claim is inferred.
- Runtime contour: exact SipJob plan/edge/schema labels are revalidated immediately before the existing live-owner callback on ordinary and fused transport paths; labels grant no authority.
 - Managed provider contour: accelerator sideband is bound to the actual operation/provider generations and live input/output Region labels, then revalidated immediately before CXL Type-2 model provider submit. Active and invalidated unreleased Region uses pin label attach, transition and output propagation; labels grant no authority. Multi-input accelerator labels remain FutureGated.
- Model: all 9 finite labels are exhaustively checked for semilattice/order laws and narrow joined-influence confinement.
- Named-host medians: lattice join = $([double]$performance.summary.latticeJoinMedianNanoseconds) ns; flow-policy check = $([double]$performance.summary.flowPolicyCheckMedianNanoseconds) ns; Region label query = $([double]$performance.summary.regionLabelQueryMedianNanoseconds) ns; two-input Region propagation = $([double]$performance.summary.twoInputRegionPropagationMedianNanoseconds) ns; two-edge SipJob verification = $([double]$performance.summary.twoEdgeSipJobVerificationMedianNanoseconds) ns.
- Performance boundary: protected policy, live managed Region tracking, and SipJob admission only; capability transitions, transport/provider callbacks, physical acceleration, contention, and gate promotion are not inferred.
- Not claimed: physical hardware-provider enforcement, whole-system noninterference, hardware, or production qualification.
- ISA/opcode/CPU architecture impact: NONE.
"@|Set-Content -LiteralPath $md -Encoding utf8NoBOM
 @($performancePath,$json,$md)|ForEach-Object{"$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"}|Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
