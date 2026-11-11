[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputDirectory = (Join-Path $RepositoryRoot 'artifacts\v6\p03-temporal-resource')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$filter = 'FullyQualifiedName~SemanticRefinementV1Tests|FullyQualifiedName~TemporalSemanticsV1Tests|FullyQualifiedName~V6TemporalRuntimeEnforcementTests|FullyQualifiedName~V6TemporalCapacityReservationTests|FullyQualifiedName~VNextPhase11TemporalClaimBoundaryTests|FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~VNextPhase06ResourceDonationTests|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests|FullyQualifiedName=SingPlus.Tests.Architecture.V6ArchitectureGuardTests.V6GateRegistryIsClosedCompleteAndDefaultOff'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.RevocationAtDispatchBoundaryCannotUsePinnedAuthorityForNewCallback'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.KernelCapabilityAdmissionUsesConfiguredOwnerClock|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.EffectLifetimeIsFreshAtResourceCallbackBoundary'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.OperationAdmissionQueryMatchesAcquireWithoutQuotaOrLeaseMutation'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.ClockReadRevocationCannotReturnOperationPermissionOrMutateQuota|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.OperationQueryBoundaryDoesNotReserveOrResurrectAuthority'
$filter += '|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.ResourceClockReadRevocationCannotReturnGrantOrAllocateLease|FullyQualifiedName=SingPlus.Tests.Runtime.VNextPhase04CrossOwnerAdmissionTests.ResourceClockRevokeDuringLeaseAcquisitionCannotPublishAdmission'
$evidenceInputs = @(
    'tests/SingPlus.Tests/Runtime/VNextPhase04CrossOwnerAdmissionTests.cs',
    'contracts/SingPlus.Contracts/TemporalSemanticsV1.cs',
    'contracts/SingPlus.Contracts/SemanticExtensionContracts.cs',
    'contracts/SingPlus.Contracts/OperationObligations.cs',
    'contracts/SingPlus.Contracts/ExecutionGuarantees.cs',
    'contracts/SingPlus.Contracts/SemanticRefinementContracts.cs',
    'docs/SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-002-RESOURCE-RESERVATION-AND-UPPER-BOUND.md',
    'contracts/SingPlus.Contracts/ResourceControlModelContracts.cs',
    'contracts/SingPlus.Contracts/ResourceBudgetContracts.cs',
    'contracts/SingPlus.Contracts/ResourceMeasurementContracts.cs',
    'contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs',
    'src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs',
    'src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs',
    'src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs',
    'src/Runtime/SingPlus.Runtime/Deadlines/CancellationScopeAuthority.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6TemporalSemanticBinding.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6TemporalCapacityReservation.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs',
    'src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs',
    'src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs',
    'tests/SingPlus.Tests/Contracts/TemporalSemanticsV1Tests.cs',
    'tests/SingPlus.Tests/Contracts/SemanticRefinementV1Tests.cs',
    'tests/SingPlus.Tests/Runtime/V6TemporalRuntimeEnforcementTests.cs',
    'tests/SingPlus.Tests/Runtime/V6TemporalCapacityReservationTests.cs',
    'tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase06ResourceDonationTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs',
    'tests/SingPlus.Tests/Runtime/VNextPhase11TemporalClaimBoundaryTests.cs',
    'tests/SingPlus.Tests/Architecture/V6ArchitectureGuardTests.cs',
    'tools/SingPlus.SingCapQualification/SingPlus.SingCapQualification.csproj',
    'tools/SingPlus.SingCapQualification/Program.cs',
    'tools/SingPlus.SingCapQualification/V6P03TemporalPerformanceQualification.cs',
    'eng/v6/Qualify-V6P03Temporal.ps1'
)

Push-Location $RepositoryRoot
try {
    $output = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-restore --filter $filter 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P03 temporal tests failed.`n$output" }
    $match = [regex]::Match($output, 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)')
    if (-not $match.Success) { throw 'The P03 test runner summary could not be parsed.' }
    $failed = [int]$match.Groups[1].Value
    $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value
    $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $passed -ne 217 -or $skipped -ne 0 -or $total -ne 217) {
        throw "Unexpected P03 counts: failed=$failed passed=$passed skipped=$skipped total=$total"
    }
    $boundRaceFilter = 'FullyQualifiedName~V6TemporalCapacityReservationTests.ConcurrentBoundSubmitHasOneOwnerTransitionAndOneProviderCallback'
    for ($iteration = 1; $iteration -le 10; $iteration++) {
        $raceOutput = & dotnet test 'tests\SingPlus.Tests\SingPlus.Tests.csproj' --no-build --no-restore `
            --filter $boundRaceFilter --verbosity quiet 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $raceOutput -notmatch 'Failed:\s+0, Passed:\s+1, Skipped:\s+0, Total:\s+1') {
            throw "P03 bound-owner race repetition $iteration failed.`n$raceOutput"
        }
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $performancePath = Join-Path $OutputDirectory 'performance.json'
    $performanceOutput = & dotnet run --project 'tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj' `
        -c Release --no-restore -- --v6-p03-temporal-performance --output $performancePath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "P03 temporal performance campaign failed.`n$performanceOutput" }
    $performance = Get-Content -Raw -LiteralPath $performancePath | ConvertFrom-Json
    if ($performance.schema -ne 'singnext.v6.p03-temporal-performance/1' -or
        $null -eq $performance.summary -or $performance.summary.supportsGatePromotion) {
        throw 'The P03 temporal performance artifact is malformed.'
    }

    $fileEvidence = @($evidenceInputs | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence input missing: $_" }
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $sourceSetPayload = ($fileEvidence | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $sourceSetDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($sourceSetPayload))).ToLowerInvariant()

    $artifact = [ordered]@{
        schema = 'singnext.v6.qualification/1'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        phase = @('P03', 'P03-C-model', 'P03-C-reset-capacity-quarantine', 'P03-managed-performance', 'P05-temporal-partial-order')
        slice = 'temporal-resource-vocabulary-accounting-upper-bound-managed-capacity-and-overhead'
        contour = 'single-host/ComputeTime-Nanoseconds/accounting-upper-bound-managed-reservation'
        sliceStatus = 'reset-and-settlement-fault-safety-closed-for-named-managed-model; broader-P03-partial'
        requirementClassification = [ordered]@{
            VerifiedExisting = @('ResourceBudgetAuthority owns quantitative budget accounting', 'ResourceScheduler is policy',
                'bound managed temporal capacity reads exact ExternalOperationAuthority publication and release')
            Partial = @('internal managed protected-capacity lifecycle', 'provider-specific temporal evidence',
                'corrective overrun accounting consumes an unqualified provider measurement; no physical enforcement claim')
            Missing = @('physical protected-capacity provider', 'end-to-end schedulability and interference proof')
            Contradicted = @()
            ExternalBlocked = @('physical provider capacity and service guarantees are unavailable in this contour')
            FutureGated = @('guaranteed completion deadline', 'production hard real-time claim')
        }
        owners = [ordered]@{
            budget = 'ResourceBudgetAuthority: lease, accounting, settlement and quarantine'
            provider = 'V6ManagedTemporalCapacityProvider: protected model capacity, generation and correlation'
            effect = 'ExternalOperationAuthority: publication and exact release of the bound operation'
            coordinator = 'binding lifecycle and final pre-submit composition; no independent capacity ledger'
        }
        nonAuthoritativeEvidence = @('TemporalSemanticsV1 sidecar', 'performance measurements', 'qualification artifact')
        sourceAndDependencyTuple = [ordered]@{
            singNextHead = (& git rev-parse HEAD).Trim()
            sourceSetSha256 = $sourceSetDigest
            sdk = (& dotnet --version).Trim()
            targetFramework = 'net11.0'
            provider = 'singnext.managed-model-temporal-capacity'
            dependency = 'ResourceBudgetAuthority/ComputeTimeNanoseconds/ExternalEffect'
        }
        requirementIds = @('P03-OWNER-01', 'P03-CAPACITY-RESET-01', 'P03-CAPACITY-PRESUBMIT-CLOSURE-01', 'P03-CORRELATION-01',
            'P03-SETTLEMENT-01', 'P03-SETTLEMENT-FAULT-01', 'P03-SETTLEMENT-RESET-RACE-01',
            'P03-EFFECT-CLOSURE-01', 'P03-DEADLINE-GATE-01', 'P03-FINAL-INDEPENDENT-ADMISSION-REVALIDATION-01',
            'P03-BOUND-EXTERNAL-OWNER-CLOSURE-01',
            'P03-RESERVATION-UPPER-BOUND-INCOMPARABLE-01',
            'P03-MEASURED-OVERRUN-QUARANTINE-01',
            'P03-OVERRUN-RECONCILIATION-CEILING-01',
            'P03-OWNER-CORRECTIVE-CHARGE-01',
            'P03-DONATION-POSSIBLE-SUBMIT-NO-REFUND-01',
            'P03-GENERATED-DONATION-CLOSE-BEFORE-SUBMIT-01')
        testIds = @('V6TemporalCapacityReservationTests.StaleReleaseCannotTriggerInjectedResetOfLiveReservation',
            'V6TemporalCapacityReservationTests.RepeatedOrQuarantinedReleaseCannotTriggerInjectedReset',
            'V6TemporalCapacityReservationTests.BudgetCancelledAfterAdmissionClosesUnusedProviderCapacityBeforeSubmit',
            'V6TemporalCapacityReservationTests.ResetRetainsAmbiguousCapacityAndCorrelationUntilReconciliation',
            'V6TemporalCapacityReservationTests.ResetBeforeSubmitAllowsExactCancellationOfStaleReservation',
            'V6TemporalCapacityReservationTests.ResetAfterSubmitQuarantinesBudgetBeforeSettlement',
            'V6TemporalCapacityReservationTests.SettlementFailureQuarantinesProviderAndKeepsReconciliationReachable',
            'V6TemporalCapacityReservationTests.ResetBetweenBudgetSettlementAndProviderReleaseRetainsCapacityForReconciliation',
            'V6TemporalCapacityReservationTests.DeniedEffectClosureCannotReleaseQuarantinedCapacityOrBudget',
            'V6TemporalCapacityReservationTests.EffectClosureCallbackRunsOutsideCoordinatorLock',
            'V6TemporalCapacityReservationTests.ConcurrentReconciliationConsumesQuarantineOnce',
            'V6TemporalCapacityReservationTests.ProviderResetBeforeOrDuringSubmitNeverRunsOrPublishesStaleCapacity',
            'V6TemporalCapacityReservationTests.LegalityTriggeredAdmissionRevocationIsCaughtBeforeCapacityUse',
            'V6TemporalCapacityReservationTests.BoundTemporalQuarantineRequiresExactExternalOperationOwnerRelease',
            'V6TemporalCapacityReservationTests.BoundTemporalSettlementWaitsForOwnerPublication',
            'V6TemporalCapacityReservationTests.BoundOwnerSubmitDenialClosesCapacityBeforeProviderCallback',
            'V6TemporalCapacityReservationTests.ConcurrentBoundSubmitHasOneOwnerTransitionAndOneProviderCallback',
            'V6TemporalCapacityReservationTests.BoundOwnerLossDuringSuccessfulProviderCallbackStillQuarantinesCapacity',
            'V6TemporalCapacityReservationTests.MeasuredOverrunQuarantinesBothOwnersAndCannotBeRewrittenByLowerSettlement',
            'V6TemporalCapacityReservationTests.CorrectiveOverrunSettlementUsesBudgetOwnerCapacityOrRetainsQuarantine',
            'V6TemporalCapacityReservationTests.CorrectiveOverrunChargeRequiresEffectClosureBeforeBudgetMutation',
            'V6TemporalCapacityReservationTests.BoundCorrectiveOverrunWaitsForExactExternalOwnerRelease',
            'TemporalSemanticsV1Tests.SmallerUpperBoundRefinesLargerButReservationUsesCapacityDirection',
            'SemanticRefinementV1Tests.GuaranteedReservationDoesNotRefineEnforcedUpperBound',
            'VNextPhase06ResourceDonationTests.PossibleSubmitCannotDowngradeDonationToPreSubmitReturn',
            'VNextPhase06ResourceDonationTests.PossibleSubmitAndPreSubmitReturnHaveOneDonationWinner',
            'VNextPhase06ResourceDonationTests.GeneratedSubmitBlocksLatePreSubmitReturnDuringSessionDrain',
            'VNextPhase06ResourceDonationTests.SessionCloseWithoutLivePinQuarantinesBoundDonationInsteadOfAssumingPreSubmitClosure',
            'VNextPhase06ResourceDonationTests.GeneratedSubmitAfterSessionCloseWithoutPinCannotRefundOrCallProvider')
        commandsRun = @(
            "dotnet test tests\SingPlus.Tests\SingPlus.Tests.csproj --no-restore --filter $filter",
            "10 x dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter $boundRaceFilter --verbosity quiet",
            "dotnet run --project tools\SingPlus.SingCapQualification\SingPlus.SingCapQualification.csproj -c Release --no-restore -- --v6-p03-temporal-performance --output $performancePath")
        environment = [ordered]@{ os = [Environment]::OSVersion.VersionString; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(); shell = 'PowerShell' }
        tests = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $total }
        boundOwnerRaceRepeats = [ordered]@{ passed = 10; failed = 0; total = 10 }
        featureGates = @(
            [ordered]@{ roadmapGate = 'V6-TEMPORAL-CONTRACTS'; implementationGate = 'V6-TEMPORAL-ACCOUNTING'; state = 'OFF' },
            [ordered]@{ roadmapGate = 'V6-GUARANTEED-DEADLINE'; implementationGate = 'V6-GUARANTEED-DEADLINE'; state = 'OFF' })
        coverage = [ordered]@{
            contract = @('canonical ComputeTime/Nanoseconds sidecar', 'upper-bound vs reservation quantity direction',
                'reservation and upper-bound assurance are incomparable',
                'cancellation deadline distinct from completion guarantee', 'partial-order laws')
            runtime = @('budget conservation and settlement', 'donation narrowing/cycle boundaries',
                'replay/squash charge normalization', 'ambiguous resource settlement quarantine',
                'direct session donation possible submit cannot be downgraded to pre-submit refund, and submit/return race has one owner winner',
                'generated submit denies late pre-submit refund after owner commit; session close without a live pin quarantines even a Bound donation because provider callback state is not inferred from that local state; a prepared generated submit after close cannot call the provider or refund the quarantined budget',
                'exact temporal sidecar final-sentry revalidation before submit',
                'SingNext and provider admission are re-read after independent runtime legality and before budget/provider capacity consumption',
                'provider-generation drift before effect', 'named managed provider protected-capacity pool',
                'budget/provider dual reservation', 'one-winner capacity admission',
                'submit-failure quarantine and explicit reconciliation',
                'provider reset retains in-use/quarantined capacity and correlation until reconciliation',
                'pre-submit reset permits exact cancellation without restoring stale capacity',
                'pre-submit budget cancellation closes exact unused provider reservation without effect',
                'post-submit reset quarantines budget before settlement',
                'budget settlement failure quarantines provider and keeps explicit reconciliation reachable',
                'reset between budget settlement and provider release retains quarantined capacity and terminal charge',
                'reconciliation requires effect-owner closure and calls outside coordinator lock',
                'competing reconciliation attempts have one terminal winner',
                'reported compute-time overrun quarantines budget and provider capacity, rejects lower replay, and cannot be cleared by ordinary zero-charge reconciliation',
                'owner corrective charge commits only after effect closure and available capacity at every ancestor; capacity denial retains quarantine')
            boundEffectOwner = @('exact pre-submit external operation handle is bound to the same RuntimeKernel budget owner',
                'budget and provider capacity enter in-use before owner submit, while provider callback waits for one committed owner Submitted transition',
                'definite owner-submit denial closes both reservations before any provider callback',
                'concurrent bound submit has one owner transition and one provider callback',
                'owner loss during a successful provider callback still quarantines both temporal owners',
                'successful settlement requires owner publication',
                'quarantined capacity cannot use an arbitrary closure callback and waits for exact owner release',
                'bound corrective overrun charge waits for exact ExternalOperationAuthority release',
                'supplied binding cannot strip the bound operation to bypass publication or closure')
            negative = @('unit/scope mismatch', 'reservation laundering', 'deadline laundering',
                'guaranteed deadline and temporal owner remain absent/default OFF')
            performance = @('budget-ledger lifecycle baseline', 'managed provider capacity lifecycle',
                'dual-owner admit/revalidate/submit/settle lifecycle',
                'rotating Release JIT named-host rounds', 'median/p95/p99 elapsed time',
                'managed-thread allocation')
        }
        performanceCampaign = [ordered]@{
            artifact = 'performance.json'; sha256 = Get-Sha256 $performancePath
            budgetLedgerMedianNanoseconds = [double]$performance.summary.budgetLedgerMedianNanoseconds
            providerCapacityMedianNanoseconds = [double]$performance.summary.providerCapacityMedianNanoseconds
            dualOwnerMedianNanoseconds = [double]$performance.summary.dualOwnerMedianNanoseconds
            dualOwnerOverLedgerPercent = [double]$performance.summary.dualOwnerOverLedgerPercent
            supportsGatePromotion = $false
            interpretation = 'Uncontended managed software accounting/reservation overhead only; no scheduler, interference, minimum-service, deadline, or schedulability claim.'
        }
        evidenceInputs = $fileEvidence
        binaryAndPackageInputs = @(
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Tests.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Runtime.dll',
            'tests/SingPlus.Tests/bin/Debug/net11.0/SingPlus.Contracts.dll'
        ) | ForEach-Object {
            $path = Join-Path $RepositoryRoot $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Binary evidence missing: $_" }
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
        }
        changedFilesThisIteration = @('src/Runtime/SingPlus.Runtime/V6/V6TemporalCapacityReservation.cs',
            'tests/SingPlus.Tests/Runtime/V6TemporalCapacityReservationTests.cs',
            'contracts/SingPlus.Contracts/SemanticRefinementContracts.cs',
            'contracts/SingPlus.Contracts/TemporalSemanticsV1.cs',
            'tests/SingPlus.Tests/Contracts/SemanticRefinementV1Tests.cs',
            'tests/SingPlus.Tests/Contracts/TemporalSemanticsV1Tests.cs',
            'docs/SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-002-RESOURCE-RESERVATION-AND-UPPER-BOUND.md',
            'eng/v6/Qualify-V6P03Temporal.ps1')
        publicApiPackageSchemaDelta = 'No public enum, signature, package, or schema shape change. This iteration adds an internal budget-owner corrective-settlement method; prior V1 refinement corrections remain.'
        maximumSupportedClaim = [ordered]@{
            temporalSidecar = 'StaticAdmission'
            managedTemporalSubmitSentry = 'RuntimeEnforced for exact upper-bound contour'
            existingComputeAccounting = 'RuntimeEnforced'
            existingExactBudgetUpperBound = 'EnforcedUpperBound for the named managed contour'
            capacityReservation = 'ModelOnly named provider plus RuntimeEnforced dual-owner lifecycle on internal contour'
            boundExternalOwnerClosure = 'RuntimeEnforced only for the exact managed RuntimeKernel external-operation publication/release and temporal capacity contour; no physical effect containment claim'
            guaranteedCompletionDeadline = 'FutureGated'
            hardRealTime = 'FutureGated'
            production = 'FutureGated'
        }
        softwarePerformanceObservation = 'Uncontended named-host managed accounting/reservation overhead; no service guarantee or gate-promotion claim.'
        javaChecks = [ordered]@{
            state = 'SKIPPED_BY_USER_INSTRUCTION'
            scope = 'All Java-dependent P03 verification and cross-language confirmation.'
            claimImpact = 'No Java interoperability, Java-dependent differential, or Java-dependent production qualification claim.'
        }
        remainingBlockers = @(
            'The live temporal sidecar path is internal and V6-TEMPORAL-ACCOUNTING remains OFF.',
            'A named managed model provider exposes protected-capacity reservation; no physical provider advertises it.',
            'The post-legality admission re-read is not atomic with a physical provider callback; no physical provider generation or service guarantee is inferred.',
            'Only the bound managed contour reads live ExternalOperationAuthority publication/release; unbound model reconciliation still uses an explicit callback contract.',
            'ExternalOperationAuthority release records a local provider-resource closure assertion; physical effect containment and resource closure remain unqualified.',
            'No end-to-end schedulability/interference proof supports a guaranteed completion deadline.',
            'A reported overrun can be charged by ResourceBudgetAuthority only after effect closure and spare capacity at every ancestor; otherwise both reservations stay quarantined. The report is not qualified physical measurement.',
            'Both temporal rollout gates remain OFF.'
        )
        rollbackFallback = 'Keep both v6 gates OFF; existing ResourceBudgetAuthority accounting remains authoritative.'
        isaImpact = 'NONE'
    }
    $artifact.testIds += 'VNextPhase07ExternalOperationResourceBindingTests.PublishedOperationCannotReleaseRegionWhileResourceSettlementIsInFlight'
    $artifact.testIds += 'VNextPhase07ExternalOperationResourceBindingTests.ProviderLossWithQuarantinedResourceBindingCannotReleaseRegion'
    $jsonPath = Join-Path $OutputDirectory 'qualification.json'
    $markdownPath = Join-Path $OutputDirectory 'qualification.md'
    $artifact.requirementIds += 'P03-EXACT-TEMPORAL-SCHEMA-ADMISSION-01'
    $artifact.testIds += 'V6TemporalRuntimeEnforcementTests.MatchingUnknownTemporalSchemasCannotBorrowV1PayloadPermission'
    $artifact.requirementClassification.VerifiedExisting += 'Temporal admission rejects unknown schema ID or version even when requirement and guarantee match and contain canonical V1 payload'
    $artifact.requirementIds += 'P03-STRICT-TEMPORAL-SCOPE-UNICODE-01'
    $artifact.testIds += @('TemporalSemanticsV1Tests.MalformedScopeCannotAliasCanonicalReplacementBytes','TemporalSemanticsV1Tests.SupplementaryScopeRoundTripsAtExactUtf8Boundary')
    $artifact.requirementClassification.VerifiedExisting += 'Temporal scope rejects malformed UTF16 before canonical bytes; valid supplementary Unicode exact byte boundary preserved'
    $artifact.requirementIds += 'P03-ACTIVE-OPERATION-ADMISSION-01'
    $artifact.requirementIds += 'C0-PROVIDER-UNKNOWN-MANDATORY-01'
    $artifact.requirementIds += 'C0-CANONICAL-BASE-DIGEST-01'
    $artifact.testIds += 'V6TemporalRuntimeEnforcementTests.AlteredBaseDigestSidecarCannotPassFreshTemporalBinding'
    $artifact.requirementClassification.VerifiedExisting += 'Altered canonical base obligation/guarantee digest sidecar fails temporal fresh binding with no submit and budget remains Bound'
    $artifact.testIds += 'V6TemporalRuntimeEnforcementTests.UnknownMandatoryProviderCompanionCannotHideBehindTemporalGuarantee'
    $artifact.requirementClassification.VerifiedExisting += 'Managed temporal consumer refuses unknown mandatory provider companion while preserving bound lease and unsubmitted admission'
    $artifact.testIds += 'VNextPhase06ResourceDonationTests.GeneratedSentryConsumesExactInvocationDonationWithoutSecondCharge'
    $artifact.requirementClassification.VerifiedExisting += 'Final donation admission rejects cancelled operation disposition before provider callback; post-marker cancellation preserves consuming charge through quarantine, pre-marker closure remains owner-confirmed'
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM
    @"
# P03 temporal/resource evidence

- Result: $passed/$total selected contract, budget, donation, managed capacity, reset, quarantine, and settlement tests passed; bound-owner concurrent submit repeated 10/10 without a second winner.
- Claims: temporal contract = StaticAdmission; exact managed final-sentry and ComputeTime accounting = RuntimeEnforced; named managed budget cap = EnforcedUpperBound; protected capacity = ModelOnly named provider lifecycle.
- Assurance correction (ADR-002): reservation capacity cannot satisfy an enforced consumption upper bound; temporal amounts are compared within compatible assurance branches only.
- Reset safety: pre-submit reservations are released, while ambiguous in-use capacity and its operation correlation stay quarantined until explicit reconciliation; post-submit settlement cannot release the budget after provider reset.
- Pre-submit budget cancellation: exact unused provider capacity is closed at the submit sentry, with no provider effect.
- Donation closure: the internal direct managed path rejects Active-to-Returned downgrade before Budget mutation; a concurrent possible submit and pre-submit return have one owner winner. Generated callback tests reject late refund after owner submit. Session close quarantines a Bound donation when it lacks exact cross-owner pre-submit closure. A prepared generated submit after that close cannot reach its provider callback; budget and external effect owners retain conservative quarantine.
- Final admission: a SingNext or provider revoke triggered during runtime legality is detected before budget/provider capacity use and before the provider callback.
- Settlement fault: a rejected budget settlement quarantines provider capacity and keeps explicit reconciliation reachable.
- Reported overrun: ComputeTime above the reserved bound quarantines both owners. A later lower settlement or ordinary zero-charge reconciliation cannot erase the report. An explicit corrective path charges the reported value through ResourceBudgetAuthority only after effect closure and spare capacity at every ancestor; denial retains quarantine. The report is not trustworthy physical measurement.
- Settlement/reset race: terminal budget charge is preserved while provider capacity remains quarantined until explicit reconciliation.
- Reconciliation: denied effect-owner closure keeps both reservations quarantined; the closure callback runs outside the coordinator lock.
- Bound effect owner: budget/capacity become in-use before one external owner submit; provider callback follows only after owner Submitted. Definite owner-submit denial closes unused reservations. Publication gates successful settlement; after ambiguous provider submit, temporal capacity remains quarantined until ExternalOperationAuthority releases that same operation. An arbitrary callback cannot close a bound effect.
- Named-host medians: budget ledger = $([double]$performance.summary.budgetLedgerMedianNanoseconds) ns; provider capacity = $([double]$performance.summary.providerCapacityMedianNanoseconds) ns; dual-owner lifecycle = $([double]$performance.summary.dualOwnerMedianNanoseconds) ns.
- Performance boundary: uncontended managed software only; no scheduler/interference, minimum-service, completion-deadline, hard-RT, or gate-promotion claim.
- Gates: `V6-TEMPORAL-ACCOUNTING` and `V6-GUARANTEED-DEADLINE` remain OFF.
- Not claimed: physical protected-capacity provider, minimum service, completion deadline, hard real time, hardware, or production qualification.
- No TemporalAuthority or duplicate ledger was introduced; ResourceBudgetAuthority remains authoritative.
- ISA/opcode/CPU architecture impact: NONE.
- Java-dependent checks: skipped by user instruction; no Java interoperability or Java-dependent differential claim.
- Commands: dotnet test (selected P03 filter, --no-restore); dotnet run (Release P03 performance campaign, --no-restore).
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
    @($performancePath, $jsonPath, $markdownPath) | ForEach-Object {
        "$(Get-Sha256 $_)  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
} finally {
    Pop-Location
}
