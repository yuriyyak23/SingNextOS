[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p07-locality-data-motion')
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$filter='FullyQualifiedName~LocalityPlanningContractsV1Tests|FullyQualifiedName~V6DataMotionTests|FullyQualifiedName~VisibilityRegionLocalityV1Tests|FullyQualifiedName~ComputePlannerTests|FullyQualifiedName~VNextPhase10ResourceSchedulerTests|FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~SemanticCodesignOwnerBoundaryTests|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaSubmissionTests.ProviderDmaDataMotionClosesAuthorityBeforeSourceReleaseAndCanonicalReceipt|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaSubmissionTests.PendingProviderDmaDataMotionDoesNotReleaseEitherRegionOrLowerAuthority|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaSubmissionTests.CrossOwnerProviderDmaMovesBetweenExactDomainAuthorities|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaSubmissionTests.ProviderDmaResumeRejectsUnrelatedActiveSubmissionLegs|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$filter+='|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaSubmissionTests.ProviderDmaDataMotionResetBeforeClosureRetainsSourceAndNoReceipt'
$filter+='|FullyQualifiedName=SingPlus.Tests.Platform.PlatformDmaSubmissionTests.ProviderDmaPartialGrantClosureCannotResubmitOrReleaseSourceAfterDestinationReset'
$inputs=@(
 'contracts/SingPlus.Contracts/LocalityPlanningContracts.cs','contracts/SingPlus.Contracts/ComputePlanning.cs',
 'contracts/SingPlus.Contracts/SemanticRefinementContracts.cs','src/Runtime/SingPlus.Runtime/V6/V6DataMotion.cs',
 'src/Runtime/SingPlus.Runtime/V6/V6ManagedLocalityCostProvider.cs',
 'src/Runtime/SingPlus.Runtime/V6/V6PlatformDmaSemanticSubmission.cs',
 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Dma.cs',
 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCopySubmission.cs',
 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCompletion.cs',
 'src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaPostCompletion.cs',
 'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs','src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs',
 'src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs','src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs',
 'src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs','src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs',
 'tests/SingPlus.Tests/Contracts/LocalityPlanningContractsV1Tests.cs','tests/SingPlus.Tests/Runtime/V6DataMotionTests.cs',
 'tests/SingPlus.Tests/Runtime/VisibilityRegionLocalityV1Tests.cs','tests/SingPlus.Tests/Runtime/ComputePlannerTests.cs',
 'tests/SingPlus.Tests/Runtime/VNextPhase10ResourceSchedulerTests.cs','tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs',
 'tests/SingPlus.Tests/Architecture/SemanticCodesignOwnerBoundaryTests.cs',
 'tests/SingPlus.Tests/Platform/PlatformDmaSubmissionTests.cs',
 'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
 'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
 'tools/SingPlus.SingCapQualification/Program.cs',
 'tools/SingPlus.SingCapQualification/V6P07LocalityPerformanceQualification.cs',
 'eng/v6/Qualify-V6P07Locality.ps1')
$performanceRoot=$null
Push-Location $RepositoryRoot
try {
 $output=& dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1|Out-String
 if($LASTEXITCODE-ne 0){throw "P07 tests failed.`n$output"}
 $m=[regex]::Match($output,'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
 if(!$m.Success){throw 'P07 summary was not parsed.'}
 $failed=[int]$m.Groups[1].Value;$passed=[int]$m.Groups[2].Value;$skipped=[int]$m.Groups[3].Value;$total=[int]$m.Groups[4].Value
 if($failed-ne 0-or$passed-ne 59-or$skipped-ne 0-or$total-ne 59){throw "Unexpected P07 counts: $failed/$passed/$skipped/$total"}
 $performanceRoot=Join-Path ([IO.Path]::GetTempPath()) ("singnext-p07-performance-"+[Guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path $performanceRoot|Out-Null
 $performancePath=Join-Path $performanceRoot 'performance.json'
 $performanceProject=Join-Path $RepositoryRoot 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj'
 $performanceOutput=& dotnet run --project $performanceProject -c Release --no-restore -- `
  --v6-p07-locality-performance --output $performancePath --iterations 100 --rounds 7 2>&1|Out-String
 if($LASTEXITCODE-ne 0-or!(Test-Path -LiteralPath $performancePath -PathType Leaf)){throw "P07 performance campaign failed.`n$performanceOutput"}
 $performance=Get-Content -Raw -LiteralPath $performancePath|ConvertFrom-Json
 if($performance.Schema-ne'singnext.v6.p07-locality-performance/1'-or@($performance.Samples).Count-ne 42-or
    @($performance.Summaries).Count-ne 3-or@($performance.Summaries|Where-Object SupportsPolicyPromotion).Count-ne 0){
  throw 'P07 performance campaign emitted an unexpected schema, sample matrix, or promotion claim.'
 }
 $files=@($inputs|ForEach-Object{$p=Join-Path $RepositoryRoot $_;if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw "Missing: $_"};[ordered]@{path=$_;bytes=(Get-Item $p).Length;sha256=Get-Sha256 $p}})
 $payload=($files|ForEach-Object{"$($_.sha256)  $($_.path)"})-join"`n";$digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
 New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
 $performanceArtifact=Join-Path $OutputDirectory 'performance.json'
 Copy-Item -LiteralPath $performancePath -Destination $performanceArtifact -Force
 $performanceDigest=Get-Sha256 $performanceArtifact
 $artifact=[ordered]@{
  schema='singnext.v6.qualification/1';generatedUtc=[DateTimeOffset]::UtcNow.ToString('O');phase=@('P07','P07-C-managed-cost-provider','P07-D-modeled-ab','P07-E-managed-provider-dma','P07-F-managed-elapsed-overhead-campaign')
  slice='provider-observed-neutral-cost-data-motion-controlled-modeled-ab-and-managed-elapsed-overhead';contour='single-host/managed/owned-buffer/live-cost-sideband/host-copy-or-exact-provider-dma-then-release/modeled-cost-ab/elapsed-overhead'
  sourceAndDependencyTuple=[ordered]@{singNextHead=(& git rev-parse HEAD).Trim();sourceSetSha256=$digest;sdk=(& dotnet --version).Trim();targetFramework='net11.0';provider='managed provider-DMA copy model';dependency='P01 RegionAuthority/P04 DMA owners/ResourceBudgetAuthority'}
  requirementIds=@('P07-DMA-MOVE-RESET-NO-SOURCE-RELEASE-01','P07-DMA-PARTIAL-CLOSURE-NO-RESUBMIT-01')
  testIds=@('PlatformDmaSubmissionTests.ProviderDmaDataMotionResetBeforeClosureRetainsSourceAndNoReceipt','PlatformDmaSubmissionTests.ProviderDmaPartialGrantClosureCannotResubmitOrReleaseSourceAfterDestinationReset')
  requirementClassification=[ordered]@{
   VerifiedExisting=@('managed provider-DMA reset at submit or grant closure retains both Regions and no Completed receipt')
   Partial=@('post-completion partial-closure retains exact copy-pair correlation and lower grant closure/fault-pin observation, but has no owner-qualified effect-closure receipt or safe data-motion reconciliation after a destination reset')
   Missing=@('post-reset provider-qualified effect-closure producer and exact owner recovery transition for the original copy pair')
   ExternalBlocked=@('existing RevokeDmaGrant status is bound to the original provider incarnation and cannot prove closure after reset','physical DMA and topology provider qualification')
   FutureGated=@('locality policy promotion and production movement')
  }
  tests=[ordered]@{passed=$passed;failed=$failed;skipped=$skipped;total=$total}
  commandsActuallyRun=@('dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter <P07 selected filter>','dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p07-locality-performance --output <temporary performance.json> --iterations 100 --rounds 7')
  javaDependentChecks=[ordered]@{state='SkippedByInstruction';claimLimit='No Java-dependent cross-language confirmation is inferred; physical topology, hardware and production claims remain FutureGated.'}
  featureGate=[ordered]@{roadmapGate='V6-LOCALITY-PLANNING';implementationGate='V6-LOCALITY-PLANNING';state='OFF'}
  ownership=[ordered]@{planner='advisory ranking only';regionAuthority='source use and release facts';resourceBudgetAuthority='temporary allocation and final ownership charges';provider='freshness/generation of cost evidence'}
  coverage=[ordered]@{
   schema=@('freshness interval','confidence and contention bounds','provider-neutral identities','no private topology fields')
   runtime=@('named managed provider freshness and monotonic observation generation','provider restart invalidation','live provider sideband planning','final estimate revalidation','exact Region/mutation/byte tuple','source read pin','copy then source release','exact whole-buffer DMA read/write grants','paired provider submit and resumable both-leg completion without resubmit','cross-owner process/domain authority separation','destination acquire before closure','both grants and mappings close before source release','canonical lifecycle receipt')
   accounting=@('owned-memory conservation','temporary allocation compensation','concurrent one-winner move')
   experiment=@('same-observation reference and candidate arms','adjusted latency plus contention cost units','modeled improvement permille','missing or stale reference rejection','Release elapsed-time campaign for 64B/4KiB/64KiB host-copy workloads','alternating preselected versus provider-observed planner arms','same managed destination prevents false locality-benefit attribution','no policy-promotion claim')
   negative=@('stale and replayed provider evidence','provider generation drift at final sentry with temporary-charge compensation','stale mutation','pending DMA destination retains both Regions and lower authority until exact resume','reset during atomic copy submit or grant closure retains source and suppresses Completed receipt','partial grant closure retains exact paired grant observation without permitting re-submit, effect closure, or source release after destination reset','reversed or unrelated DMA legs cannot be laundered into a copy pair','topology and authority ABI absence')
  }
  evidenceInputs=$files
  binaryAndPackageInputs=@(
   'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
   'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
   'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll',
   'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Platform.Abstractions.dll'
  )|ForEach-Object{$p=Join-Path $RepositoryRoot $_;if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw "Missing binary evidence: $_"};[ordered]@{path=$_;bytes=(Get-Item $p).Length;sha256=Get-Sha256 $p}}
  changedFilesThisIteration=@('src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Dma.cs','src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCopySubmission.cs','tests/SingPlus.Tests/Platform/PlatformDmaSubmissionTests.cs','eng/v6/Qualify-V6P07Locality.ps1')
  publicApiPackageSchemaDelta='No public API, package, or schema change; internal DMA-owner copy correlation and read-only closure observation.'
  performanceCampaign=[ordered]@{reportPath='performance.json';reportSha256=$performanceDigest;environment=$performance.Environment;methodology=$performance.Methodology;summaries=$performance.Summaries;claimBoundary=$performance.ClaimBoundary}
  maximumSupportedClaim=[ordered]@{
   costEvidence='RuntimeEnforced revalidation of advisory evidence from named managed provider; never authority';hostCopyMovement='RuntimeEnforced on internal managed contour'
   accounting='RuntimeEnforced for OwnedMemoryBytes on host-copy contour';providerDmaMovement='RuntimeEnforced on exact same-process and cross-owner deterministic managed provider contours'
   performanceBenefit='ModelOnly cost A/B plus named-host elapsed overhead characterization; no placement-sensitive locality benefit or policy-promotion claim';physicalTopology='FutureGated';hardware='FutureGated';production='FutureGated'
  }
  remainingBlockers=@('V6-LOCALITY-PLANNING remains OFF and entry point is internal.','After both DMA visibility legs complete, active submissions are removed, but the DMA grant owner retains exact copy-pair correlation and local closed/fault-pinned facts. RevokeDmaGrant returns only a status for the original provider incarnation, and the bridge rejects that stale grant after reset. No exact post-reset provider effect-closure evidence or owner recovery transition exists; source Region remains owned and repeated execution cannot re-submit DMA. Observation is not authority for release.','Provider DMA movement is qualified only for exact deterministic managed-provider contours; physical providers remain unqualified.','The live cost sideband is supplied only by a named managed model provider, not a physical topology source.','A named-host managed host-copy workload now characterizes planner overhead, but identical physical placement means it cannot demonstrate locality benefit; controlled NUMA/device/topology workloads and an accepted product threshold remain absent.','No hardware or production qualification exists.')
  rollbackFallback='Keep the locality gate OFF and retain the existing planner/reference movement behavior.';isaImpact='NONE'
 }
 $json=Join-Path $OutputDirectory 'qualification.json';$md=Join-Path $OutputDirectory 'qualification.md'
 $artifact|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $json -Encoding utf8NoBOM
 @"
# P07 locality/data-motion evidence

- Result: $passed/$total selected planner, locality, Region, budget, host-move, and managed provider-DMA tests passed.
- Named claim: host-copy movement and OwnedMemoryBytes conservation are RuntimeEnforced only for the internal managed contour.
- Cost estimates come from a named managed provider with monotonic generations, freshness/restart invalidation, and final revalidation; estimates and plans remain non-authoritative.
- Controlled A/B: the selected and reference arms use the same fresh provider observations and report modeled adjusted-cost improvement only; the result cannot promote policy or claim elapsed-time performance.
- Elapsed-time campaign: 7 alternating Release rounds per arm and payload (64 B, 4 KiB, 64 KiB) compare provider-observed planning with a preselected identical managed destination. It characterizes planner overhead only; identical placement cannot prove locality benefit or promote policy. See `performance.json`.
- Provider DMA: exact same-process and cross-owner whole-buffer grants use paired submit, resumable both-leg completion without resubmit, destination acquire, grant/mapping closure, and only then source release plus canonical receipt on the deterministic managed provider.
- DMA reset boundary: reset during copy submit or grant closure retains the source and destination Regions and emits no Completed movement receipt.
- Partial closure: if source grant closure succeeds but destination grant closure faults on reset, a repeat cannot re-submit DMA or release source. DMA grant records retain exact copy-pair correlation and expose source PlatformClosed and destination fault-pin facts after active submissions disappear. RevokeDmaGrant returns only a status bound to the old incarnation, so post-reset provider effect closure and an owner-qualified reconciliation transition remain missing. This read-only observation never authorizes source release.
- Gate: `V6-LOCALITY-PLANNING` remains OFF.
- Java-dependent checks: skipped by instruction; no cross-language or physical-provider claim is inferred.
- Not claimed: physical-provider DMA movement, elapsed-time or workload performance benefit, physical topology evidence, hardware, or production qualification.
- ISA/opcode/CPU architecture impact: NONE.
"@|Set-Content -LiteralPath $md -Encoding utf8NoBOM
 @($json,$md,$performanceArtifact)|ForEach-Object{"$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"}|Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
}finally{
 if($performanceRoot){
  $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath());$resolved=[IO.Path]::GetFullPath($performanceRoot)
  if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved).StartsWith('singnext-p07-performance-',[StringComparison]::Ordinal)){[IO.Directory]::Delete($resolved,$true)}
 }
 Pop-Location
}
