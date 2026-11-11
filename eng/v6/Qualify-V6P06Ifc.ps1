[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p06-protected-ifc'),
    [string]$ArtifactsPath = (Join-Path $OutputDirectory 'graph')
)
$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$requiredTests = @(
 'SingPlus.Tests.Contracts.InformationFlowContractsV1Tests.JoinRaisesConfidentialityAndLowersIntegrity',
 'SingPlus.Tests.Contracts.InformationFlowContractsV1Tests.FlowRequiresBothConfidentialityAndIntegrityBounds',
 'SingPlus.Tests.Contracts.InformationFlowContractsV1Tests.DeclassifyAndEndorseChangeExactlyOneDimension',
 'SingPlus.Tests.Contracts.InformationFlowContractsV1Tests.UnknownVersionsAndClassesFailClosed',
 'SingPlus.Tests.Contracts.InformationFlowFiniteModelV1Tests.JoinIsACommutativeAssociativeIdempotentOperationAcrossTheFiniteLattice',
 'SingPlus.Tests.Contracts.InformationFlowFiniteModelV1Tests.JoinIsTheLeastUpperBoundForEveryPair',
 'SingPlus.Tests.Contracts.InformationFlowFiniteModelV1Tests.FlowRelationIsReflexiveAntisymmetricAndTransitive',
 'SingPlus.Tests.Contracts.InformationFlowFiniteModelV1Tests.DisallowedInfluenceCannotBeHiddenByJoiningWithAnAllowedValue',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.ProtectedPropagationUsesJoinAndRejectsUnlabeledInput',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.DeclassificationWithoutExactCapabilityFails',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.ExactCapabilityAuthorizesOneExplicitTransitionAndStaleBindingFails',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.RevokedCapabilityCannotAuthorizeTransitionAndGateRemainsOff',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.OwnershipTransferCarriesLabelAndAdvancesItsBindingGeneration',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.ActiveRegionUsePinsProtectedLabelAgainstTransition',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.ActiveOutputUsePinsProtectedLabelAgainstPropagation',
 'SingPlus.Tests.Runtime.V6ProtectedInformationFlowTests.InvalidatedUnreleasedUsePinsProtectedLabelChanges',
 'SingPlus.Tests.Runtime.V6ProtectedAcceleratorLabelFlowTests.InputIntermediateAndOutputRemainBoundToOneOperationAndLabel',
 'SingPlus.Tests.Runtime.V6ProtectedAcceleratorLabelFlowTests.OperationGenerationAndSequenceSubstitutionFailClosed',
 'SingPlus.Tests.Runtime.V6ProtectedAcceleratorLabelFlowTests.IntermediateOrOutputLabelLaunderingFailsClosed',
 'SingPlus.Tests.SipJobs.SipJobProtectedLabelFlowTests.FusionAndMaterializationPreserveExactLabelSidecars',
 'SingPlus.Tests.SipJobs.SipJobProtectedLabelFlowTests.MissingDuplicateAndSchemaSubstitutionFailClosed',
 'SingPlus.Tests.SipJobs.SipJobProtectedLabelFlowTests.ImplicitLabelChangeAndGenerationRollbackFailClosed',
 'SingPlus.Tests.SipJobs.SipJobProtectedLabelFlowTests.ReplayedPlanAndUnsupportedConfidentialBoundaryFailClosed',
 'SingPlus.Tests.SipJobs.SipJobProtectedTransportExecutorTests.OrdinaryTransportRevalidatesExactLabelsBeforeLiveOwnerCommit',
 'SingPlus.Tests.SipJobs.SipJobProtectedTransportExecutorTests.FusedTransportPreservesSameProtectedBindingAndSkipsOnlyTransportHop',
 'SingPlus.Tests.SipJobs.SipJobProtectedTransportExecutorTests.MissingOrDuplicateLabelRejectsBeforeTransportAndOwnerCallbacks',
 'SingPlus.Tests.SipJobs.SipJobProtectedTransportExecutorTests.UnverifiedPlanRejectsBeforeLabelOrOwnerWork',
 'SingPlus.Tests.SipJobs.SipJobProtectedTransportExecutorTests.OwnerFailureIsPropagatedWithoutPromotingLabelToAuthority',
 'SingPlus.Tests.SipJobs.Phase145ABarrierPlannerTests.EveryKnownBoundaryIsClosedAndConservative',
 'SingPlus.Tests.SipJobs.Phase145ABarrierPlannerTests.UnknownVersionAndBoundaryFailClosed',
 'SingPlus.Tests.Platform.Phase7EvidenceSecureComputeTests.EvidenceAndManifestTypesAreNotAuthorityOrReclaimProof',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelsReachExactManagedType2ProviderSubmitBoundary',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelsWithWrongProviderGenerationFailBeforeProviderEffect',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelLaunderingFailsBeforeProviderEffect',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProviderWithoutProtectedBoundaryIsRejectedBeforeOperationOrEffect',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedLabelsNeverReplaceOrdinaryRegionAuthority',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.ProtectedSidebandWithoutLiveRegionLabelsCannotReachProvider',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.StaleProtectedInputLabelGenerationCannotReachProvider',
 'SingPlus.Tests.Runtime.CxlType2AcceleratorServiceTests.LiveProtectedType2OperationPinsInputLabelUntilClosure',
 'SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
)
$filter = ($requiredTests | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
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
 'eng/v6/Qualify-V6P06Ifc.ps1', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config',
 'tests/SingPlus.Tests/SingPlus.Tests.csproj', 'src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj',
 'src/Runtime/SingPlus.Runtime/packages.lock.json', 'contracts/SingPlus.Contracts/SingPlus.Contracts.csproj',
 'src/Platform/SingPlus.Platform.Abstractions/SingPlus.Platform.Abstractions.csproj',
 'src/Platform/SingPlus.Platform.Host/SingPlus.Platform.Host.csproj', 'src/Sip/SingPlus.Sip/SingPlus.Sip.csproj',
 'tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj',
 'tools/HybridCpu_ExecutableAdapter/packages.lock.json')
Push-Location $RepositoryRoot
try {
 New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
 New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null
 $resultsDirectory = Join-Path $OutputDirectory 'results'
 $processDirectory = Join-Path $OutputDirectory 'processes'
 New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
 New-Item -ItemType Directory -Path $processDirectory -Force | Out-Null
 $commandsActuallyRun = [System.Collections.Generic.List[string]]::new()
 function Get-InputRows {
  @($inputs | ForEach-Object {
   $path = Join-Path $RepositoryRoot $_
   if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
   [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
  })
 }
 function Save-ProcessSnapshot([string]$Name) {
  $rows = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, CommandLine)
  $path = Join-Path $processDirectory "$Name.json"
  $rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
  $active = @($rows | Where-Object { $_.Name -match '^dotnet\.exe$|^testhost\.exe$' })
  if ($active.Count -gt 0) { throw "Refusing to overlap a live dotnet/testhost process before $Name. See $path" }
 }
 function Assert-InputEpoch($Expected, [string]$Stage) {
  $current = Get-InputRows
  if ($current.Count -ne $Expected.Count) { throw "P06 input count changed before $Stage." }
  for ($index = 0; $index -lt $Expected.Count; $index++) {
   if ($current[$index].path -ne $Expected[$index].path -or $current[$index].sha256 -ne $Expected[$index].sha256) {
    throw "P06 source/build input drift before ${Stage}: $($Expected[$index].path)"
   }
  }
  $current | Export-Csv -LiteralPath (Join-Path $OutputDirectory "inputs-$Stage.csv") -NoTypeInformation -Encoding utf8
 }
 $inputEpoch = Get-InputRows
 $inputEpoch | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'inputs-pre.csv') -NoTypeInformation -Encoding utf8
 $testAssets = Join-Path $ArtifactsPath 'obj\SingPlus.Tests\project.assets.json'
 $qualificationAssets = Join-Path $ArtifactsPath 'obj\SingPlus.SingCapQualification\project.assets.json'
 $restoreExit = 'not-needed-assets-present'
 if (-not (Test-Path -LiteralPath $testAssets -PathType Leaf) -or -not (Test-Path -LiteralPath $qualificationAssets -PathType Leaf)) {
  Save-ProcessSnapshot 'pre-restore'
  Assert-InputEpoch $inputEpoch 'pre-restore'
  $restoreCommand = "dotnet restore tests\SingPlus.Tests\SingPlus.Tests.csproj --locked-mode -m:1 -nr:false -p:UseSharedCompilation=false -p:ArtifactsPath=`"$ArtifactsPath`""
  $restoreCommand | Set-Content -LiteralPath (Join-Path $OutputDirectory 'restore-command.txt') -Encoding utf8
  $commandsActuallyRun.Add($restoreCommand)
  & dotnet restore 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --locked-mode -m:1 -nr:false -p:UseSharedCompilation=false "-p:ArtifactsPath=$ArtifactsPath" 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'restore.log')
  $restoreExit = [string]$LASTEXITCODE
  Set-Content -LiteralPath (Join-Path $OutputDirectory 'restore.exit') $restoreExit -NoNewline
  if ($restoreExit -ne '0') { throw "P06 locked restore failed with exit $restoreExit." }
 }
 if (-not (Test-Path -LiteralPath $testAssets -PathType Leaf) -or -not (Test-Path -LiteralPath $qualificationAssets -PathType Leaf)) { throw 'Required isolated assets are missing after restore.' }
 Assert-InputEpoch $inputEpoch 'post-restore'
 Save-ProcessSnapshot 'pre-test'
 Assert-InputEpoch $inputEpoch 'pre-test'
 if ($restoreExit -notin @('0', 'not-needed-assets-present')) { throw "Previous restore exit is not successful: $restoreExit" }
 $testCommand = "dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:ArtifactsPath=`"$ArtifactsPath`" --filter `"<required P06 tests>`" --logger `"trx;LogFileName=p06.trx`" --results-directory `"$resultsDirectory`" --verbosity minimal"
 $testCommand | Set-Content -LiteralPath (Join-Path $OutputDirectory 'test-command.txt') -Encoding utf8
 $testCommandRecord = [ordered]@{ command = $testCommand; filter = $filter; requiredTests = $requiredTests; workingDirectory = $RepositoryRoot; artifactsPath = $ArtifactsPath; resultsDirectory = $resultsDirectory; priorRestoreExit = $restoreExit }
 $commandsActuallyRun.Add("dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:ArtifactsPath=`"$ArtifactsPath`" --filter `"$filter`" --logger `"trx;LogFileName=p06.trx`" --results-directory `"$resultsDirectory`" --verbosity minimal")
 & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore -m:1 -nr:false -p:UseSharedCompilation=false "-p:ArtifactsPath=$ArtifactsPath" --filter $filter --logger 'trx;LogFileName=p06.trx' --results-directory $resultsDirectory --verbosity minimal 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'test.log')
 $testExit = [string]$LASTEXITCODE
 Set-Content -LiteralPath (Join-Path $OutputDirectory 'test.exit') $testExit -NoNewline
 if ($testExit -ne '0') { throw "P06 tests failed with exit $testExit." }
 $trxPath = Join-Path $resultsDirectory 'p06.trx'
 if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw 'P06 test run did not produce TRX.' }
 [xml]$trx = Get-Content -Raw -LiteralPath $trxPath
 $testResults = @($trx.TestRun.Results.UnitTestResult)
 $passedNames = @($testResults | Where-Object { $_.outcome -eq 'Passed' } | ForEach-Object { $_.testName })
 $failed = @($testResults | Where-Object { $_.outcome -eq 'Failed' }).Count
 $skipped = @($testResults | Where-Object { $_.outcome -notin @('Passed', 'Failed') }).Count
 $total = $testResults.Count
 $passed = $passedNames.Count
 $missingCases = @($requiredTests | Where-Object { $required = $_; -not ($passedNames | Where-Object { $_.Contains($required) }) })
 if ($total -lt $requiredTests.Count -or $failed -ne 0 -or $skipped -ne 0 -or $passed -ne $total -or $missingCases.Count -ne 0) {
  throw "P06 required-case validation failed: total=$total passed=$passed failed=$failed skipped=$skipped missing=$($missingCases -join ', ')."
 }
 $testCommandRecord.tests = [ordered]@{ total = $total; passed = $passed; failed = $failed; skipped = $skipped; requiredCaseCount = $requiredTests.Count; requiredCasesMissing = @($missingCases) }
 $testInputPayload = ($inputEpoch | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
 $testCommandRecord.sourceSetSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($testInputPayload))).ToLowerInvariant()
 $testCommandRecord | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'test-command.json') -Encoding utf8
 Assert-InputEpoch $inputEpoch 'post-test'
 Save-ProcessSnapshot 'pre-performance-build'
 Assert-InputEpoch $inputEpoch 'pre-performance-build'
 if ($testExit -ne '0') { throw "Previous test exit is not successful: $testExit" }
 $performanceBuildCommand = "dotnet build tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj --configuration Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:ArtifactsPath=`"$ArtifactsPath`" --verbosity minimal"
 $performanceBuildCommand | Set-Content -LiteralPath (Join-Path $OutputDirectory 'performance-build-command.txt') -Encoding utf8
 $commandsActuallyRun.Add($performanceBuildCommand)
 & dotnet build 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' --configuration Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false "-p:ArtifactsPath=$ArtifactsPath" --verbosity minimal 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'performance-build.log')
 $performanceBuildExit = [string]$LASTEXITCODE
 Set-Content -LiteralPath (Join-Path $OutputDirectory 'performance-build.exit') $performanceBuildExit -NoNewline
 if ($performanceBuildExit -ne '0') { throw "P06 performance build failed with exit $performanceBuildExit." }
 Assert-InputEpoch $inputEpoch 'pre-performance'
 Save-ProcessSnapshot 'pre-performance'
 $performancePath = Join-Path $OutputDirectory 'performance.json'
 $performanceHost = Join-Path $ArtifactsPath 'bin\SingPlus.SingCapQualification\release\SingPlus.SingCapQualification.exe'
 if (-not (Test-Path -LiteralPath $performanceHost -PathType Leaf)) { throw "P06 performance apphost missing: $performanceHost" }
 $performanceCommand = "`"$performanceHost`" --v6-p06-information-flow-performance --output `"$performancePath`""
 $performanceCommand | Set-Content -LiteralPath (Join-Path $OutputDirectory 'performance-command.txt') -Encoding utf8
 $commandsActuallyRun.Add($performanceCommand)
 & $performanceHost --v6-p06-information-flow-performance --output $performancePath 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'performance.log')
 $performanceExit = [string]$LASTEXITCODE
 Set-Content -LiteralPath (Join-Path $OutputDirectory 'performance.exit') $performanceExit -NoNewline
 if ($performanceExit -ne '0') { throw "P06 information-flow performance campaign failed with exit $performanceExit." }
 $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
 if ($performance.schema -ne 'singnext.v6.p06-information-flow-performance/1' -or
  $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
  throw 'The P06 information-flow performance artifact is malformed.'
 }
 Assert-InputEpoch $inputEpoch 'post-performance'
 $files = Get-InputRows
 $payload=($files|ForEach-Object{"$($_.sha256)  $($_.path)"})-join"`n"
 $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
 $artifact=[ordered]@{
  schema='singnext.v6.qualification/1'; generatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); phase=@('P06','P06-C-runtime','P06-D-managed-provider','P06-managed-performance')
  slice='owned-region-sipjob-and-managed-type2-live-boundary-protected-label-propagation'; contour='single-process/owned-Region/internal-managed-runtime/closed-value-SipJob/managed-CXL-Type2-model'
  sourceAndDependencyTuple=[ordered]@{singNextHead=(& git rev-parse HEAD).Trim();sourceSetSha256=$digest;sdk=(Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'global.json')|ConvertFrom-Json).sdk.version;targetFramework='net11.0';provider='CxlType2ModelAccelerator/protected-sideband';dependency='RegionAuthority/protected-label-generation; CapabilityAuthority/explicit-transition'}
   requirementIds=@('P06-LATTICE-ORDER-AND-UNKNOWN-DENY-01','P06-REGION-PROPAGATION-AND-TRANSFER-01','P06-CAPABILITY-AUTHORIZED-TRANSITION-01','P06-SIPJOB-EXACT-SIDECAR-AND-BARRIER-01','P06-LABEL-NEVER-AUTHORIZES-ACCESS-01','P06-LIVE-TYPE2-PROVIDER-BOUNDARY-01','P06-ACTIVE-USE-LABEL-PIN-01','P06-INVALIDATED-USE-LABEL-PIN-01')
   testIds=$requiredTests
  requirementClassification=[ordered]@{VerifiedExisting=@('RegionAuthority owns protected label and generation; CapabilityAuthority owns explicit transition permission');Partial=@('named managed protected Type-2 path now checks live input/output labels before operation admission and provider submit; no physical provider enforcement');FutureGated=@('multi-input protected accelerator sideband','physical provider and whole-system IFC')}
  tests=[ordered]@{passed=$passed;failed=$failed;skipped=$skipped;total=$total;requiredCaseCount=$requiredTests.Count;requiredCasesMissing=@($missingCases)}
  terminal=[ordered]@{restoreExit=$restoreExit;testExit=$testExit;performanceBuildExit=$performanceBuildExit;performanceExit=$performanceExit}
  commandsActuallyRun=$commandsActuallyRun.ToArray()
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
  binaryAndPackageInputs=@((Join-Path $ArtifactsPath 'bin\SingPlus.Tests\debug\SingPlus.Tests.dll'),(Join-Path $ArtifactsPath 'bin\SingPlus.Runtime\debug\SingPlus.Runtime.dll'),(Join-Path $ArtifactsPath 'bin\SingPlus.Contracts\debug\SingPlus.Contracts.dll'),(Join-Path $ArtifactsPath 'bin\SingPlus.SingCapQualification\release\SingPlus.SingCapQualification.dll'))|ForEach-Object{if(!(Test-Path -LiteralPath $_ -PathType Leaf)){throw "Missing binary evidence: $_"};[ordered]@{path=[IO.Path]::GetRelativePath($RepositoryRoot,$_);bytes=(Get-Item $_).Length;sha256=Get-Sha256 $_}}
  changedFilesThisIteration=@('eng/v6/Qualify-V6P06Ifc.ps1')
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
