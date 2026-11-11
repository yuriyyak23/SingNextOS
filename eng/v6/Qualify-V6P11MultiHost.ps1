[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p11-multihost-leases')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$filter = 'FullyQualifiedName~RemoteAuthorityLeaseV1Tests|FullyQualifiedName~RemoteAuthorityLeaseFormalModelTests|FullyQualifiedName~V6RemoteLeaseFaultSimulatorTests|FullyQualifiedName~V6RemoteDelegatedResourcePilotTests|FullyQualifiedName~V6ManagedRemoteLeaseTransportTests|FullyQualifiedName~V6RemoteLeaseRecoveryJournalTests|FullyQualifiedName~CxlSecurityAndMultiHostTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$evidenceInputs = @(
    'contracts/SingPlus.Contracts/RemoteAuthorityLeaseContracts.cs',
    'contracts/SingPlus.Contracts/MultiHostMemory.cs',
    'formal/v6/RemoteAuthorityLease.tla',
    'formal/v6/RemoteAuthorityLease.cfg',
    'src/Runtime/SingPlus.Runtime/Cxl/CxlMultiHostGate.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6RemoteLeaseFaultSimulator.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6RemoteDelegatedResourcePilot.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ManagedRemoteLeaseTransport.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6ManagedRemoteLeaseJournaledSubmit.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6RemoteLeaseRecoveryJournal.cs',
    'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetRecoveryJournal.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tests/SingPlus.Tests/Contracts/RemoteAuthorityLeaseV1Tests.cs',
    'tests/SingPlus.Tests/Architecture/RemoteAuthorityLeaseFormalModelTests.cs',
    'tests/SingPlus.Tests/Runtime/V6RemoteLeaseFaultSimulatorTests.cs',
    'tests/SingPlus.Tests/Runtime/V6RemoteDelegatedResourcePilotTests.cs',
    'tests/SingPlus.Tests/Runtime/V6ManagedRemoteLeaseTransportTests.cs',
    'tests/SingPlus.Tests/Runtime/V6RemoteLeaseRecoveryJournalTests.cs',
    'tests/SingPlus.Tests/Runtime/CxlSecurityAndMultiHostTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P11RemoteLeasePerformanceQualification.cs',
    'eng/v6/Qualify-V6P11MultiHost.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P11 multi-host tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The P11 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value; $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value; $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 83 -or $skipped -ne 0 -or $total -ne 83) {
        throw "Unexpected P11 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p11-remote-lease-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P11 remote-lease performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p11-remote-lease-performance/1' -or
        $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
        throw 'The P11 remote-lease performance artifact is malformed.'
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
        phase = @('P11-A', 'P11-B', 'P11-C', 'P11-C-managed-transport', 'P11-C-authenticated-restart',
            'P11-C-exact-response-correlation', 'P11-C-corrupt-response-retry',
            'P11-C-managed-write-ahead-submit', 'P11-C-guarded-managed-dispatch',
            'P11-pilot-performance')
        slice = 'remote-lease-safety-fault-pilot-managed-transport-and-authenticated-restart'
        contour = 'formal-source/managed-exploration/delegated-resource/two-endpoint-wire-codec/hmac-recovery-journal'
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim(); sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim(); targetFramework = 'net11.0'
            provider = 'managed two-endpoint transport and authenticated recovery journal'
            dependency = 'P01/P04/P05/P10 single-host owners; P02 for persisted state'
        }
        requirementIds = @('P11-RESTART-ISSUED-NO-EFFECT-UNPROVEN-01',
            'P11-EXACT-CLOSURE-BEFORE-RECLAIM-01', 'P11-MANAGED-WRITE-AHEAD-SUBMIT-01')
        testIds = @('V6RemoteLeaseRecoveryJournalTests.IssuedLeaseAfterRestartCannotBeReclaimedWithoutExactClosure',
            'V6ManagedRemoteLeaseTransportTests.JournaledSubmitPersistsPossibleEffectBeforeDeliveryAndExactRetry',
            'V6ManagedRemoteLeaseTransportTests.JournalAppendFailurePreventsManagedSubmitDelivery',
            'V6ManagedRemoteLeaseTransportTests.JournaledSubmitWithoutExactIssuedTupleCannotReachOwner',
            'V6ManagedRemoteLeaseTransportTests.OwnerJournalRestartDeniesExactRetryOfDeliveredSubmit',
            'V6ManagedRemoteLeaseTransportTests.FileBackedJournalReplaysSubmittedBeforeRestartAndDeniesStaleRetry',
            'V6ManagedRemoteLeaseTransportTests.CachedSubmitResponseCannotResurrectLeaseAfterOwnerReboot',
            'V6ManagedRemoteLeaseTransportTests.ManagedOwnerRestartFencesBeforeJournalAppendAndStaleRetry',
            'V6ManagedRemoteLeaseTransportTests.FailedRestartJournalAppendLeavesLiveOwnerFenced',
            'V6ManagedRemoteLeaseTransportTests.ConcurrentManagedSubmitAndRestartNeverAdmitOldLeaseAfterRestart')
        requirementClassification = [ordered]@{
            VerifiedExisting = @('owner incarnation/epoch high-watermarks and authenticated journal replay',
                'possible-effect state remains quarantined until exact closure in managed recovery',
                'managed owner endpoint serializes submit/cache dispatch with live reboot before journal append')
            Partial = @('issued-only state after restart is quarantined unless exact closure arrives',
                'guarded managed transport requires the one-shot coordinator for Submit, but unguarded qualification transport and production dispatch are separate contours',
                'direct pilot reboot and direct journal restart remain independent qualification APIs; only endpoint RestartOwner is coupled in-process')
            ExternalBlocked = @('OS/network remote recovery coordinator and durable-media campaign')
            FutureGated = @('production multi-host leases and ownership transfer')
        }
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        commandsActuallyRun = @(
            "dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter $filter",
            "dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p11-remote-lease-performance --output $performancePath"
        )
        javaDependentChecks = [ordered]@{
            state = 'SkippedByInstruction'
            scope = 'Java-dependent TLA+ checker and any cross-language confirmation'
            claimLimit = 'The TLA+ file remains a source specification; no externally checked formal or physical multi-host claim is inferred.'
        }
        featureGate = [ordered]@{ roadmapGate = 'V6-MULTIHOST-LEASES'; implementationGate = 'V6-MULTIHOST-LEASES'; state = 'OFF' }
        protocol = [ordered]@{
            owner = 'single original logical owner'
            freshness = @('owner and remote incarnation', 'owner epoch', 'lease generation', 'monotonic owner sequence')
            partition = 'denies new remote submit and never proves effect closure'
            reclaim = 'exact lease fence plus explicit remote effect closure'
            accounting = 'parent/child escrow conservation'
            clock = 'no wall-clock expiry assumption'
        }
        coverage = [ordered]@{
            static = @('narrow rights', 'no parent authority', 'no ownership transfer', 'no global capability database')
            faults = @('partition', 'renew/revoke generation race', 'owner reboot', 'remote reboot',
                'epoch and lease ABA', 'expiry without closure', 'wrong closure tuple',
                'provider loss', 'fabric reconfiguration', 'partition at every lifecycle prefix')
            boundedExploration = @('all action interleavings to depth four', 'at most one submit and publication',
                'single logical owner', 'reclaim only after fence and closure', 'escrow conservation')
            delegatedResourcePilot = @('exact lease and resource correlation', 'rights narrowing',
                'monotonic logical expiry', 'concurrent single submit winner', 'single publication winner',
                'exact provider-generation effect closure', 'partition-safe reclaim', 'escrow conservation')
            managedTransport = @('canonical bounded binary request/response codec',
                'distinct remote and owner endpoint objects', 'request-id idempotency cache',
                'dropped or corrupted response retry without duplicate submit', 'duplicate delivery replay',
                'partition before owner linearization', 'request-id payload-conflict rejection',
                'provider-loss transport forbids staged publication',
                'response request/kind and full-wire digest correlation before status interpretation',
                'exact closure permits reclaim only', 'owner reboot rejects stale lease',
                'cached submit response cannot authorize old lease after reboot',
                'endpoint restart serializes with submit and fences live owner before journal append',
                'restart append failure leaves old lease denied')
            writeAheadSubmit = @('exact Issued lease/provider/escrow tuple required',
                'Submitted journal append precedes any managed transport delivery',
                'append failure prevents delivery and exact retry uses one store-acknowledged frame',
                'dropped response retry does not duplicate owner submit',
                'changed command digest rejects',
                'fresh owner journal high-watermarks and exact Submitted state are revalidated on every delivery or retry',
                'guarded transport rejects direct Submit before and after coordinator binding',
                'file-backed store replay observes Submitted and restart quarantines before stale retry')
            persistentRestart = @('append-only authenticated hash chain', 'stable flushed file store',
                'owner incarnation and epoch high-watermarks', 'old lease authority never restored',
                'issued-only predecessor quarantines after restart without durable no-effect proof',
                'stale provider closure rejects before reclaim',
                'possible effects recover as quarantine backlog',
                'old lease accepts only exact closure and reclaim after restart',
                'closed or published predecessor is reclaim-only and cannot republish',
                'wrong key, tampering, stale restart, and lease-ID reuse fail closed')
            existingSingleHostGuard = @('one writable host', 'fence before reclaim',
                'host disappearance does not silently reassign', 'distributed writable flag rejected')
            performance = @('cached exact narrow-lease admission predicate',
                'lease admission plus exact effect-closure predicate',
                'qualification-pilot issue plus staged-write submit',
                'full in-process issue/submit/fence/closure/publish/reclaim lifecycle',
                'full canonical codec plus managed two-endpoint transport lifecycle',
                'authenticated memory-backed restart append/replay/high-watermark lifecycle',
                'rotating Release JIT named-host rounds', 'median/p95/p99 elapsed time', 'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            cachedLeaseAdmissionMedianNanoseconds = [double]$performance.summary.cachedLeaseAdmissionMedianNanoseconds
            leaseAndClosurePredicateMedianNanoseconds = [double]$performance.summary.leaseAndClosurePredicateMedianNanoseconds
            pilotIssueAndSubmitMedianNanoseconds = [double]$performance.summary.pilotIssueAndSubmitMedianNanoseconds
            fullPilotLifecycleMedianNanoseconds = [double]$performance.summary.fullPilotLifecycleMedianNanoseconds
            managedTransportLifecycleMedianNanoseconds = [double]$performance.summary.managedTransportLifecycleMedianNanoseconds
            authenticatedRestartReplayMedianNanoseconds = [double]$performance.summary.authenticatedRestartReplayMedianNanoseconds
            supportsGatePromotion = $false
            interpretation = 'Host-local predicate, pilot, canonical codec, managed endpoint dispatch, and memory-backed authenticated restart replay overhead only; no OS/network remote host, durable-media I/O, or consensus latency.'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing binary evidence: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        changedFilesThisIteration = @('src/Runtime/SingPlus.Runtime/V6/V6RemoteLeaseRecoveryJournal.cs',
            'src/Runtime/SingPlus.Runtime/V6/V6ManagedRemoteLeaseJournaledSubmit.cs',
            'src/Runtime/SingPlus.Runtime/V6/V6ManagedRemoteLeaseTransport.cs',
            'tests/SingPlus.Tests/Runtime/V6RemoteLeaseRecoveryJournalTests.cs',
            'tests/SingPlus.Tests/Runtime/V6ManagedRemoteLeaseTransportTests.cs',
            'eng/v6/Qualify-V6P11MultiHost.ps1')
        publicApiPackageSchemaDelta = 'No public API, package, or schema change; internal managed write-ahead coordinator and recovery disposition only.'
        maximumSupportedClaim = [ordered]@{
            leaseSchema = 'StaticAdmission'
            safetySpecification = 'ModelOnly source specification; no external checker qualification'
            faultSimulator = 'ModelOnly executable bounded exploration'
            delegatedResourcePilot = 'RuntimeEnforced only inside the in-process qualification contour'
            existingSingleHostCxlGuard = 'RuntimeEnforced conservative exclusion only'
            remoteLeaseRuntime = 'ExecutableAdapter only for the managed serialized two-endpoint qualification transport; production remains FutureGated'
            writeAheadSubmit = 'RuntimeEnforced ordering only through the one-shot coordinator, guarded managed transport, and endpoint RestartOwner instance; no production dispatch or media-durability claim'
            persistentRestart = 'RuntimeEnforced authenticated replay and fail-closed restart planning on memory and flushed file stores in the managed contour'
            ownershipTransfer = 'FutureGated'
            executableMultiHostEnvironment = 'FutureGated'
            hardware = 'FutureGated'; production = 'FutureGated'
        }
        performanceObservation = 'In-process predicate/direct/serialized managed-transport overhead only; no cross-host coordination latency claim.'
        remainingBlockers = @(
            'V6-MULTIHOST-LEASES remains OFF; the pilot has no production registration or gate bypass.',
            'The safety specification is source-reviewed/test-indexed but no external checker run is tuple-bound.',
            'The managed serialized two-endpoint transport is not an OS/process/network multi-host transport.',
            'Authenticated restart is executable only in the managed contour; no remote OS/process recovery coordinator or durable-media campaign exists.',
            'Issued-only journal state cannot prove no external submit after restart and remains quarantined until exact closure. The guarded managed transport binds one write-ahead coordinator, but unguarded qualification transport and production dispatch are not coupled, and no durable no-effect proof exists for legacy issuance.',
            'Endpoint RestartOwner serializes live owner reboot with managed dispatch and fences before journal append; the two steps are not an atomic durable transition. Direct pilot/journal APIs and production remote execution remain outside this contour.',
            'No distributed ownership-transfer protocol, executable multi-host environment, hardware, or production qualification exists.'
        )
        rollbackFallback = 'Keep remote leases disabled; retain conservative single-host fencing and require explicit effect closure before reclaim.'
        isaImpact = 'NONE'
    }
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P11 multi-host lease evidence

- Result: $passed/$total selected lease-shape, bounded fault-simulation, narrow delegated-resource pilot, formal-source, CXL exclusion, and gate tests passed.
- Claim: schema = StaticAdmission; safety specification and executable bounded simulator = ModelOnly; pilot, serialized transport, and authenticated restart/reconciliation planning = RuntimeEnforced only inside the managed qualification contour; production remote lease runtime = FutureGated.
- Safety: one logical owner; logical epochs/incarnations only; partition or expiry never proves effect closure; reclaim requires fence plus exact closure. Managed responses must match the exact request identity, command kind, and full-wire digest before success or failure is interpreted; a corrupted response after possible owner commit is reported as DependencyUnavailable and exact retry does not duplicate submit.
- Restart boundary: an Issued-only journal entry is quarantined after owner reboot; it no longer releases escrow from assumed no-effect. Exact matching closure can move it to reclaim-only. Journal issuance and external send are not atomically connected in this managed contour.
- Managed write-ahead path: a guarded transport instance rejects direct Submit before and after binding its one-shot coordinator. The coordinator appends Submitted before sending the exact wire command; append failure prevents delivery, and dropped-response retry uses the same command without duplicate owner submit. A second file-store instance replays Submitted after reopening and quarantines on restart. These tests verify managed ordering and file-store API behavior, not physical media durability or production dispatch.
- Managed restart boundary: endpoint RestartOwner serializes live reboot with owner dispatch, fences the old lease before recording the restart, and rejects cached Submit after generation drift. Concurrent submit/restart and injected journal failure retain the fence. Direct pilot/journal APIs, process restart, physical durability, and production dispatch remain outside this ordering claim.
- Named-host medians: cached lease admission = $([double]$performance.summary.cachedLeaseAdmissionMedianNanoseconds) ns; lease + closure predicate = $([double]$performance.summary.leaseAndClosurePredicateMedianNanoseconds) ns; pilot issue + submit = $([double]$performance.summary.pilotIssueAndSubmitMedianNanoseconds) ns; direct lifecycle = $([double]$performance.summary.fullPilotLifecycleMedianNanoseconds) ns; serialized managed-transport lifecycle = $([double]$performance.summary.managedTransportLifecycleMedianNanoseconds) ns; authenticated memory-backed restart replay = $([double]$performance.summary.authenticatedRestartReplayMedianNanoseconds) ns.
- Performance boundary: host-local predicate, pilot, canonical serialization, managed endpoint dispatch, HMAC, and memory-backed replay only; network, remote OS/process/hosts, durable-media I/O, consensus, transport authentication, and gate promotion are not inferred.
- Gate: `V6-MULTIHOST-LEASES` remains OFF.
- Java-dependent checks: skipped by instruction; the TLA+ source is not claimed as an externally checked proof.
- Not claimed: external model qualification, OS/network remote transport/recovery coordinator, durable-media campaign, ownership transfer, executable physical multi-host environment, hardware, or production.
- ISA/opcode/CPU architecture impact: NONE.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($performancePath, $jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
