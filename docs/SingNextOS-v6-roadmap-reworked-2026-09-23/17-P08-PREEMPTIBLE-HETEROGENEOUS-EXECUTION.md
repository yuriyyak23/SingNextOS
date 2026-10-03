# P08 — Preemptible and Resumable Heterogeneous Execution

## Structured verdict

**PHASE:** P08 — Preemptible and Resumable Heterogeneous Execution  
**BASELINE:** Cancellation, replay, retire and external operation lifecycles exist; uniform safe-point/capture/resume guarantees differ across CPU, MatrixTile, DSC, L7 and providers.  
**VERDICT:** Keep cancel/preempt/drain/capture/suspend/resume/restart/contain distinct. Qualify each provider independently; stateful resume is a stronger separate feature.

### VERIFIED_EXISTING
- HybridCPU has retire/replay/runtime-legality seams.
- ExternalRuntime has operation/session/generation lifecycles.
- ResourceBudgetAuthority can account for consumption/settlement.

### PARTIAL
- CPU-like preemption may be available, but MatrixTile/DSC/L7/external provider semantics are not uniform.
- Cancellation does not prove no effect.

### GAPS
- Provider-neutral preemption capability/guarantee vocabulary.
- Safe-point latency guarantee only where enforceable.
- Opaque captured-state digest + provider/runtime generation binding.
- Fresh re-admission and legality on resume.

### CONTRADICTIONS
- A cancel receipt cannot be interpreted as containment.
- Checkpoint/captured state cannot preserve authority.

### REMOVE_OR_MERGE
- Do not merge P08 with P07 locality.
- Do not make captured state a resumability capability.

### NEW_REQUIRED
- PreemptionGuaranteeV1 sidecar with NonPreemptible/RestartOnly/SafePoint/StateCapture classes.
- ResumeBindingV1 correlation record with operation/provider/runtime generations.

### AUTHORITY IMPACT
Invocation/external operation owner remains lifecycle owner; provider/HybridCPU owns machine execution state; budget owner accounts consumption.

### HYBRIDCPU IMPACT
`RUNTIME_ONLY`/`SIDEBAND_CONTRACT` for safe points, drain/capture/resume evidence. No authority migration.

### COMPILER IMPACT
`COMPILER_CONTRACT` only if safe-point maps are compiler-generated and validated; optional for first preemption contour.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Stale captured state replayed after Region/provider generation change.
- Resume bypasses fresh authority or runtime legality.

### CORRECTNESS RISKS
- Publication race with cancel/preempt.
- Provider reset between capture and resume.
- Partial output survives restart unexpectedly.

### PERFORMANCE RISKS
- Safe points and state capture may add latency/storage overhead; quantify per provider.

### REQUIRED TESTS
- Cancel-vs-retire race.
- Preempt-vs-publication race.
- Provider reset at capture/resume.
- Stale generation resume rejection.
- Restart-only duplicate effect tests.
- Budget charging across suspend/resume/restart.
- MatrixTile/DSC/L7 provider-specific negative tests.

### FORMAL WORK
- TLA+ lifecycle model including effect-possible and quarantine.
- Trace equivalence for restart/resume contours.

### DEPENDENCIES
P03 for service/charging semantics; P04 for DMA/device-backed contours; P05 failure/refinement core. P10 informs containment after provider failure.

### EXIT CRITERIA
- Each provider advertises only executable preemption class.
- Resume performs fresh SingNext, provider and HybridCPU legality gates.
- No cancellation path claims absence of effect without evidence.

### ROADMAP PATCH
Keep P08 independent, provider-specific and generation-bound. Split stateful resume from basic preemption by gate.

## Objective

Keep cancel/preempt/drain/capture/suspend/resume/restart/contain distinct. Qualify each provider independently; stateful resume is a stronger separate feature.

## Live baseline and existing mechanisms
- ExternalOperationAuthority.
- HybridCPU replay/retire/legality.
- ResourceBudgetAuthority settlement.

## Required implementation changes
- Add preemption guarantee sidecar.
- Add lifecycle transitions and trace events.
- Add provider safe-point/capture adapters.
- Add fresh resume admission path.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-PREEMPTION`; `V6-STATEFUL-RESUME` stronger and separate  
**Claim ceiling at emulator/runtime freeze:** `ExecutableAdapter per provider; stateful resume FutureGated until exact provider closure`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P08-A vocabulary.
- P08-B CPU/reference preemption contour.
- P08-C MatrixTile/DSC/L7 capability inventory.
- P08-D capture/resume optional contour.
- P08-E race/fault qualification.

## Rollback

Disable preemption gate; new operations use current cancellation/restart semantics. Captured state is discarded, never converted into permission.

### P08 managed restart scheduling observation commit — 2026-10-02
ScheduleRestart stages timestamp/window/backoff outside supervisor/capability gates and before retry-list mutation. Nonfatal clock/frequency errors, nonpositive frequency and arithmetic overflow return PlatformFaulted with no scheduling mutation. After observation the existing capability owner validates exact control source; local commit requires the captured record, old instance, Stopped-after-drain state and unchanged attempt sequence. Observe is the real retry consumer for Stopped + RestartAfterDrain after clock recovery. Existing component Reclaimable closure remains the prerequisite; clock/failure response is not closure. Eight new cases cover fault, overflow, zero/negative frequency, capability/domain/principal loss and real replacement generation 2. Broad final: 4097 passed, 0 failed, 2 skipped. Focused final: 88 passed; initial 86 superseded by frequency boundary extension. Classification: bounded managed scheduling guard; restart dispatch/full provider resume remains Partial/FutureGated. V1 public contracts retained; gates OFF, RuntimeEnforced ceiling only for named guard, no deadlines/WCET/minimum-service claims. Java excluded; ISA/opcode/CPU impact NONE. Evidence: artifacts/v6/iteration-20261002-p08-restart-scheduling.
P04 controlled failure effects remains Missing: BeginComponentTeardown calls InitializeProcessTeardownLocked, which already invokes native/IPC/telemetry/platform cross-owner closure paths. No pure carried-control commit boundary exists there; holding capability gate around these callbacks is not an acceptable implementation. Pins/quarantine and gates remain unchanged.

### P08 Observe restart dispatch preflight — 2026-10-02
Observe catches nonfatal eligibility clock failure without retiring the old component. A fresh source-owner local snapshot checks RestartBackoff, exact old instance and unchanged eligible timestamp after the clock before obtaining the factory definition. Factory executes outside supervisor/capability gates. Factory fault or inconsistent output returns PlatformFaulted with existing backoff intact; the existing Observe retries. AdmitReplacement now freshly validates control source and exact old instance plus replacement identity/generation/primary-service contract before any RetireReclaimableComponent call. Observe final read uses authorized Query. Eight added cases cover clock/factory faults, source revoke from either callback, wrong generation, inconsistent output, independent wrong identity and blocked factory with concurrent Query/revoke. Broad final: 4105 passed, 0 failed, 2 skipped. Final focused: 96 passed; earlier 94/1 failed from incorrect constructor error assertion, then 95 passed before identity extension. V1 public contracts unchanged. Classification: bounded managed preflight guard; effect authorization between factory preflight/callback and between retirement/component admission remains Partial/Missing, not proven atomic by these checks. Arbitrary factory effects are not closure and cannot be rolled back by an error response. Gates OFF; RuntimeEnforced ceiling only for tested managed guards; physical resume FutureGated. Java excluded; ISA/opcode/CPU architecture impact NONE. Evidence: artifacts/v6/iteration-20261002-p08-restart-dispatch-preflight.

### P04 replacement component preflight before retirement — 2026-10-02
The existing component evaluator gains an internal exact-replacement mode: only the tracked old ProcessHandle in Reclaimable and exactly next generation can bypass DuplicateIdentity. Ordinary admission retains DuplicateIdentity. A shared V2 import validator serves actual AdmitComponent and replacement preflight. Supervisor AdmitReplacement validates its existing local replacement contract, then digest/registrations/compatibility/resource/driver/mandatory dependency requirements through the existing evaluator before retirement, then freshly revalidates source and local contract again. Declared budget requirements remain requests, not guarantees; later dependency races still use the real admission/rollback consumer. Five added cases cover digest, registration, compatibility and missing hard dependency preserving old component/process/budget handles and recovery, plus exact-closure/generation boundary and ordinary duplicate semantics. Initial focused 71 passed/1 failed exposed changed wrong-identity error precedence; corrected by restoring local contract check first; final focused 72 passed; broad 4110 passed, 0 failed, 2 skipped. StaticAdmission for plan checks; RuntimeEnforced only for managed exact/refusal guard. Overall carried control into provider admission remains Missing; full concurrency/retirement atomicity Partial; no physical closure inference. Gates OFF, V1 public API unchanged, Java excluded, ISA/opcode/CPU architecture impact NONE. Evidence: artifacts/v6/iteration-20261002-p04-replacement-component-preflight.

### P08/P10 Quarantined supervisor closure progress — 2026-10-02
Supervisor Observe now routes both Draining and Quarantined exact instances to the existing component teardown consumer. CompleteDrain still requires actual component Reclaimable before Stopped/restart scheduling. Quarantine is uncertainty, not closure and not a permanent terminal verdict. P08-QO-001/002/003: QuarantinedObserveUsesRealClosureBeforeStopOrRestart(planned/failure, refusal/exception) proves real preparation retry, preserved terminal target and fresh generation admission only after closure/backoff; QuarantinedObserveWithoutControlCannotRetryClosure(source/domain/principal loss) denies before callback; repeated AmbiguousExternalEffectQuarantinesAndBlocksReplacement observations preserve ProviderLost disposition, Quarantined supervisor, old process, zero restart attempts and refused region release. Local RegionUse tokens are Invalidated at teardown, independently of backing pins.
Bounded managed consumer routing closed/RuntimeEnforced; carried post-callback control/exact-instance publication in CompleteDrain remains Partial and is next dependency-ready slice. Native/session initialization remains Partial; exact device/lost receipt reconciliation Missing/pinned; physical closure FutureGated. Build initial 3 test API errors corrected; final build 0 errors/8 warnings/11.61s before final assertion changes compiled by tests. Initial focused 252 passed/1 failed from wrong Active-token assertion; intermediate correction used nonexistent operation state and failed compilation; final correction checks existing ProviderLost disposition and ReleaseRegion refusal. Final focused 253 passed; broad 4157 passed/0 failed/2 skipped. Fresh evidence: artifacts/v6/iteration-20261002-p08-quarantined-closure-observe/audit.json. Gates OFF, public API/enums/packages unchanged, Java excluded, ISA/opcode/CPU impact NONE.

### P08 drain completion publication guard — 2026-10-02
CompleteDrain now checks component identity/process tuple, stages its clock timestamp outside supervisor/capability gates, then uses existing CapabilityAuthority.CommitSupervisorRecordPublication with fresh principal/control validation and exact service-instance resolution before mutating State/Health/BlockingReason and producing a receipt. Lock order remains supervisor -> capability; trusted commit performs local record/snapshot work only, with no clock/factory/lifecycle/provider callbacks. Clock fault or source loss refuses before any completion-record mutation; actual process/provider closure already performed is not undone. Stale old instance cannot overwrite a fresh replacement.
P08-DC-001/002/003/004: DrainCompletionClockCannotPublishAfterSourceLossOrFault(0..3), DrainCompletionClockReplacementCannotOverwriteFreshInstance, BlockedDrainCompletionClockHoldsNoSupervisorOrCapabilityGate and DrainCompletionClockFaultPreservesPendingOrUnknownEffect(false/true) exercise real closure/retry, source/domain/principal loss, clock fault/recovery, actual replacement generation 2, concurrent Query/revoke and Submitted/ProviderLost pins. Bounded completion publication closed/RuntimeEnforced. Initial Drain request publication and other Health callback sites remain Partial and are next dependency-ready owner guards; native/session initialization and carried provider admission remain separate gaps. No closure inferred from failed publication, not-found or telemetry.
Build 0 errors/9 warnings/20.70s before two additional effect cases; focused initial 259 passed, final 261 passed after those additions; broad 4165 passed/0 failed/2 skipped. Fresh evidence: artifacts/v6/iteration-20261002-p08-drain-completion-publication/audit.json. Gates OFF; physical FutureGated; Java excluded; ISA/opcode/CPU impact NONE; public V1 API/enums/packages unchanged.

### P08 drain request publication and exact process dispatch — 2026-10-02
Drain stages its request timestamp outside supervisor/capability gates, then fresh principal/control owner validation and ResolveExactLocked commit Draining/Health together before lifecycle dispatch. Clock fault/source loss refuses without request publication or process teardown. Actual supervisor dispatch now carries expected ProcessHandle to an additive internal component Drain overload; the existing component record checks exact generation before mutation. The captured record is reused for progress, avoiding a second identity lookup that could redirect onto replacement. Expected identity is a refusal guard, not authority; source permission remains CapabilityAuthority. Existing public API and V1/default consumers (checkpointing/dependency drain) are preserved.
P08-DR-001/002/003: DrainRequestClockCannotStartTeardownAfterSourceLossOrFault(0..3), DrainRequestClockReplacementCannotDrainFreshProcess, BlockedDrainRequestClockAllowsQueryAndSourceRevoke cover source/domain/principal loss, clock fault/recovery, actual replacement generation 2, stale kernel consumer target refusal and concurrent Query/revoke while clock blocked. No request/teardown mutation on refusal; new component/process remains Running. Bounded local guard closed/RuntimeEnforced. Source lifetime through provider callbacks is not made atomic by this commit; broader carried admission/concurrency Partial/Missing. Deadline overload Health publication remains Partial and next dependency-ready slice; other admission/dependency/native-session guards remain separate.
Build 0 errors/9 warnings/23.40s; focused 267 passed; broad 4171 passed/0 failed/2 skipped. Fresh evidence: artifacts/v6/iteration-20261002-p08-drain-request-publication/audit.json. Gates OFF; physical FutureGated; Java excluded; ISA/opcode/CPU impact NONE; no public API/enum/package/schema changes.

### P08 deadline outcome publication guard — 2026-10-02
Expired deadline Quarantine/FailReplacement publication stages the supervisor clock outside supervisor/capability gates, then freshly resolves principal/control and exact service instance through existing CapabilityAuthority commit before changing State/Health/BlockingReason. Existing timeout policy codes retained: Quarantine receipt ClosureProven=false; FailReplacement returns ReplacementBlocked after local Draining observation. Timeout is a waiting-stop observation, not containment/closure/reclaim or a service/deadline/WCET guarantee. Temporal observation may legitimately change its existing cancellation owner before publication; source/clock refusal causes no supervisor-record mutation. No temporal authority/ledger added.
P08-DL-001/002/003: DrainDeadlinePublicationRefusesClockFaultOrSourceLoss(Quarantine/FailReplacement, 0..3), DrainDeadlineClockCannotPublishOntoReplacement(false/true), BlockedDeadlinePublicationClockAllowsQueryAndRevoke(false/true) cover clock fault/recovery, source/domain/principal loss, actual replacement and concurrent Query/revoke. Submitted operation remains Submitted, component Running and region release refused. Tests use independently supplied kernel and supervisor clocks to target actual publication callback; they do not qualify physical time or cancellation-owner clock callback locking. Bounded managed publication guard closed/RuntimeEnforced; broader temporal owner callback contour Partial; physical FutureGated.
Build 0 errors/9 warnings/23.99s; focused 279 passed; broad 4183 passed/0 failed/2 skipped. Fresh evidence: artifacts/v6/iteration-20261002-p08-drain-deadline-publication/audit.json. Next: CompleteAdmission readiness publication clock/source/definition guards and exact-process compensation. Gates OFF; V1 public API/enums/packages unchanged; Java excluded; ISA/opcode/CPU impact NONE.

### P08 readiness publication and exact compensation — 2026-10-02
CompleteAdmission checks component identity/process/manifest digest and exact provided service generation/process, stages readiness clock outside owner gates, then freshly validates principal/control and exact managed definition/state. Dependency bindings and degradation are evaluated before mutation inside the existing local commit; Instance/ProvidedInstances/Bindings/State/Health/restart fields publish together. Final receipt uses authorized Query and exact instance/digest after dependent rebinding. No clock/provider/lifecycle callback is added under capability commit.
CompensateIncompleteAdmission now carries the actual admitted ProcessHandle into existing component Drain, never identity-only cleanup. Its supervisor publication requires fresh source, same definition reference and still-unpublished admission state; late compensation cannot overwrite a new Ready definition. Clock fault can publish Stopped only with valid control and real component closure. Lost control retains prior Admitting/no-instance supervisor record while actual kernel component owner proves or pins cleanup; this is not permission or a fake closure record. Recovery consumer for failed initial readiness/no instance remains Partial/Missing, not fabricated.
P08-RP-001/002/003: ReadinessClockSourceLossOrFaultCannotPublishPartialReady(0..3), ReadinessClockDependencyLossIsRevalidatedBeforePublication, LateAdmissionCompensationCannotDrainOrOverwriteReplacement and BlockedReadinessClockAllowsQueryAndSourceRevocation cover source/domain/principal loss, clock fault, real dependency closure, generation-2 replacement, concurrent Query/revoke and exact compensation. Build 0 errors/9 warnings/21.72s before added tests/final receipt guard, compiled again by focused/broad. Focused 286 passed; broad 4190 passed/0 failed/2 skipped. Fresh evidence: artifacts/v6/iteration-20261002-p08-readiness-publication/audit.json. Bounded managed publication/compensation guard closed/RuntimeEnforced; admission before readiness is not made atomically source-authorized. Dependency propagation/rebind callbacks and native/session initialization Partial; carried provider admission/reconciliation Missing; physical FutureGated. Gates OFF; V1 public API/enums/packages unchanged; Java excluded; ISA/opcode/CPU impact NONE. Next: dependency-failure Health publication clock/source/generation guard.

### 2026-10-03 — P08 dependency-failure publication (P08-DF)
Bounded managed guard VerifiedExisting: stage clock outside gates; fresh principal/control commit validates exact failed provider and every captured peer instance, definition, binding and state before the first Degraded/Health write. CapabilityAuthority owns permission; supervisor owns local records. Primary cleanup uses exact ProcessHandle even when peer publication refuses. No global transaction across independent drain policies; initial primary failure mutation and carried provider admission remain Partial/Missing.
Eight cases cover source/domain/principal loss, clock exception, real provider/dependent replacement, whole-batch refusal and blocked clock concurrent Query/revoke. Final focused 294 passed; solution 4198 passed, 0 failed, 2 skipped. Evidence: artifacts/v6/iteration-20261002-p08-dependency-failure-publication (commands, exact tuples, TRX and SHA256 manifests). Gates OFF; named managed guards RuntimeEnforced; physical FutureGated. Java excluded; ISA/opcode/CPU NONE. Next: actual dependency rebind generation/source/publication guard; native/session initialization and lost receipt reconciliation remain Partial/Missing, pins retained.

### 2026-10-03 — P08 rebind exact target (P08-RE)
RebindComponentDependency now requires captured dependent ProcessHandle; mismatch returns StaleGeneration before session or routing mutation. Real supervisor consumer captures the handle and refuses local failure publication onto a replaced process. Public V1 contracts unchanged. P08-RE-001/002: ExactDependencyRebindRefusesStaleTargetBeforeSessionMutation(false/true), ExactDependencyRebindAcceptsCurrentTargetThroughExistingConsumer. Wrong generation, actual replacement/ABA and positive existing consumer tested. Build 0 errors/9 warnings/29.49s before final tests/consumer publication check; final focused 297 passed, broad 4201 passed/0 failed/2 skipped. Evidence: artifacts/v6/iteration-20261003-p08-rebind-exact-target.
Bounded exact target guard VerifiedExisting/RuntimeEnforced; complete rebind Partial. No synchronization of concurrent component retirement claimed. Old session CloseSession error is still ignored by rebind: native refusal or SessionDraining must retain accounting in next slice; no no-mutation/closure claim for that path. Fresh source and Health clock publication remain Partial. Existing component/session/capability owners retained. Gates OFF; physical FutureGated; Java excluded; ISA/opcode/CPU NONE.

### 2026-10-03 — P08 rebind closure retention (P08-RC)
Rebind retains DependencySessions/Sessions until actual CloseSession consumer succeeds; native refusal/exception and active pins cannot drop accounting or start replacement session. Target record/process/state is rechecked after callback. Private rebind mode permits repeated cleanup only from exact existing session owner Closed/zero-pin record and exact channel owner closed generation; public V1 CloseSession behavior unchanged. Existing invocation cleanup removes unaccepted correlations; possible-effect count checked before and after cleanup, preventing routing retirement for accepted effects or donation quarantine. These owner checks prove managed routing cleanup only, never physical containment or whole-process reclaim.
P08-RC-001/002/003/004: DependencyRebindRetainsSessionAccountingOnNativeClosureRefusal(false/true), DependencyRebindRetainsPinnedSessionUntilExistingOwnerCleanup, DependencyRebindCannotDiscardClosedSessionWithAcceptedInvocationEffect. Native retry uses existing actual registration; pin release and process teardown consume existing owners. Initial focused 322 passed/1 failed exposed already-closed provider restart path, fixed owner-controlled repeat cleanup. One test compile error (missing Send receiver) corrected. Final focused 356 passed; final broad 4205 passed/0 failed/2 skipped. Initial build 0 errors/9 warnings/19.39s; repaired build 0 errors/9 warnings/21.62s before final test/post-cleanup check, compiled by final focused/broad.
Evidence: artifacts/v6/iteration-20261003-p08-rebind-closure-retention. Bounded retention VerifiedExisting/RuntimeEnforced; complete rebind Partial. Source carry/Health publication, concurrent retirement and failure after old closure before fresh OpenSession remain Partial. Unknown receipt reconciliation remains Missing/pinned. Gates OFF; physical FutureGated; Java excluded; ISA/opcode/CPU NONE; no new owner or public API/enum/package/schema.

### 2026-10-03 — P08 exact rebind publication (P08-RB)
Rebind captures exact provider primary/provided handles, descriptor id/generation, dependent instance/definition/state/bindings. Clock staged outside locks. Existing CapabilityAuthority commit freshly validates principal/control and tuples before dispatch and before local publication; no provider/kernel lifecycle/clock callback in trusted commit. Whole candidate bindings and readiness are computed before mutation. Hard readiness compares every binding with currently ready/degraded managed provider exact handle. Sequential contracts update only snapshots resulting from this consumer's own committed publications. CompleteAdmission propagates publication refusal, without rolling back independently admitted Ready provider or treating failure as no-effect.
Ten cases: RebindClockSourceLossOrFaultRefusesBeforeDispatchAndPublication(0..3), RebindClockReplacementRefusesCapturedProviderOrDependent(false/true), RebindNativeCallbackSourceLossRefusesPublicationButRetainsActualKernelSession, BlockedRebindClockAllowsQueryAndRevoke, RebindNativeCallbackLifecycleChangeCannotBeOverwrittenByPublication, RebindMultipleContractsAdvancesOnlyItsOwnCommittedSnapshots. Actual multi-contract generation-2 replacement remains Degraded between first/second rebind; actual retained session closes via existing exact teardown.
P08-RB-001/002/003/004 managed publication guards VerifiedExisting/RuntimeEnforced. Carried source admission is Contradicted: native callback revoke can still precede new kernel session creation because source lifetime does not cover OpenSession effect commit. Test records this defect, not qualification. Next mandatory repair: exact source capability through actual local channel/session creation boundary; callbacks outside capability gate. Post-closure OpenSession refusal, concurrent component retirement and failed-publication retry consumer remain Partial/Missing. Unknown receipt contours remain pinned; no new owner/closure API.
Initial build 2 errors/1 warning/7.24s (wrong DomainId property), fixed to actual Domain; repaired build 0 errors/9 warnings/21.99s before final tests/multi-contract guard, compiled by focused/broad. Focused progression 364, 366, final 366 passed. Final broad 4215 passed/0 failed/2 skipped. Evidence: artifacts/v6/iteration-20261003-p08-rebind-publication. Public V1 API/enum/package/schema unchanged; gates OFF; physical FutureGated; Java excluded; ISA/opcode/CPU NONE.

### 2026-10-03 — P08 carried local session admission (P08-RA)
Previously Contradicted native-callback revoke/new-session path repaired for actual supervisor rebind. Sole production consumer carries exact principal/control to new internal overload. Initial source check precedes old closure; fresh source/lineage check at actual lifetime-free channel/session commit is held by existing CapabilityAuthority. Existing ServiceRegistry commit holds exact accepting descriptor id/contract/generation; provider/target process new-effect checks and dependent session capabilities precede local creation. Lock order: process lifecycle -> service -> capability -> local registries. Private arrays only; no native/provider/clock/supervisor callback inside these commits. Native old closure executes outside service/capability gates. If revoke wins, no new session/channel; if local admission wins, creation linearizes before revoke. Neither proves physical containment or safe reclaim.
P08-RA-001/002/003/004: RebindNativeCallbackSourceLossRefusesNewSessionAndPublication(0..3) covers capability/domain/principal loss and actual principal ABA with fresh control; SourcedRebindRefusesWrongAuthorityBeforeOldSessionClosure(0..2); SourcedRebindRevalidatesExactServiceAfterNativeCallback(false/true); BlockedNativeRebindAllowsSourceRevokeAndCannotCreateLateSession; SourcedRebindRejectsRevokedCapabilityAncestorAfterNativeCallback. Net ten additional cases; existing positive multi-contract generation-2 and V1/reference consumers retained.
Build 0 errors/9 warnings/18.98s before final test updates, compiled by focused/broad. Focused 375 then final 376 passed; broad 4225 passed/0 failed/2 skipped. Evidence: artifacts/v6/iteration-20261003-p08-rebind-source-admission. Bounded managed source/service admission VerifiedExisting/RuntimeEnforced. Public OpenSession and three-argument internal TCB fallback retain V1 behavior; only actual supervisor rebind gets carried source guard. Full P08 Partial: provider preflight before destructive closure, post-closure admission failure, late component tracking/concurrent retirement and failed-publication reconciliation remain Partial/Missing. Unknown receipt contours remain pinned. Gates OFF; physical FutureGated; Java excluded; ISA/opcode/CPU NONE. No new owner/public contract/schema/package or commit.

### 2026-10-03 — P08 provider preflight before closure (P08-RF)
Kernel rebind runs exact ServiceRegistry admission preflight before any old native/session closure or routing mutation. Shared ValidateSessionPartiesAndRequirements consumes actual caller/provider process new-effect checks and actual service-required capabilities; the same helper runs again in real OpenSession commit. Preflight remains an observation, not a cached permission/reservation/admission guarantee. Source-aware final commit still holds exact service/source gates; no callback under preflight gates.
P08-RF-001/002/003: RebindProviderPreflightRefusesBeforeNativeClosureOrRoutingMutation(0..3) covers unavailable service, stale generation, real registered provider requiring a missing capability and unknown service id; callback count zero, original live session/routing/budget handles preserved. Existing SourcedRebindRevalidatesExactServiceAfterNativeCallback(false/true) extended: late refusal leaves old closure distinct, process budget handle retained; restored availability enables actual existing sourced kernel rebind to perform fresh retry with new session. No restoration of old receipt or inference of no-effect/closure from error.
Build 0 errors/9 warnings/19.37s before final test updates, compiled by focused/broad. Focused 380 passed; broad 4229 passed/0 failed/2 skipped. Evidence: artifacts/v6/iteration-20261003-p08-rebind-provider-preflight. Bounded preflight VerifiedExisting/RuntimeEnforced; generic late admission failures Partial. Next: component session tracking inside process-lifecycle admission boundary to avoid late mutation against teardown. Failed publication recovery and unknown receipt reconciliation remain Missing/pinned. Gates OFF; physical FutureGated; public V1 API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE; no new owner/commit.

### 2026-10-03 — P08 rebind lifecycle tracking (P08-RT)
Existing process-lifecycle gate now covers exact component revalidation, removal of proven-closed old routing, lifetime-free local session creation and component tracking. Native old closure stays outside that gate. Captured previous handle must still match routing after callback; competing admission cannot delete a newly bound session. No-prior-routing admission refuses a competing binding. A component drain request observed during local admission retains actual session accounting but refuses a running receipt. Process stop/reclaim cannot pass this local commit before actual session tracking.
P08-RT-001/002: RebindAdmissionAccountsSessionBeforeConcurrentComponentTeardown blocks actual EndpointSessionRegistry gate, observes channel allocator only as test coordination, requests real component drain and verifies pending teardown, refused running receipt, actual Reclaimable/empty routing and closed session owner history. Observation grants no permission/closure. OverlappingLocalRebindsKeepOneTrackedActiveDependency performs four pairs through existing sourced kernel consumer without concurrent native callbacks; one tracked/active dependency and actual final teardown. No new fake closure API or owner.
Build 0 errors/1 warning/8.51s before two tests; focused initial/final 382 passed (test synchronization corrected to avoid enumerating mutable channel dictionary); broad 4231 passed/0 failed/2 skipped. Evidence: artifacts/v6/iteration-20261003-p08-rebind-lifecycle-tracking. Bounded managed tracking VerifiedExisting/RuntimeEnforced; full component registry/global concurrency Partial. Concurrent native session/process drain callback registration lacks an interlock, next dependency-ready consumer repair. Process native initialization and failed-publication/unknown receipt reconciliation remain Partial/Missing; pins retained. Gates OFF; physical FutureGated; public V1 API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE; no commit.
