# P04 — Translation and DMA Authority Composition

## Structured verdict

**PHASE:** P04 — Translation and DMA Authority Composition  
**BASELINE:** Region ownership exists; provider/external operation generations exist; address-space/IOMMU generation composition is not yet a single qualified runtime contract.  
**VERDICT:** High-priority correctness/security phase. Add generation-bound correlation, not a new authority ledger.

### VERIFIED_EXISTING
- RegionUse remains the byte-access/ownership fact.
- ExternalOperationAuthority and provider generation sets already support generation-aware external lifecycles.
- Provider/platform layers already own device-local mapping and execution details.

### PARTIAL
- Some PASID/SVA/IOMMU-style concepts exist in models/adapters, but presence is not proof of invalidation/revoke closure.
- Device reset/provider restart generations exist in external-runtime style contracts but are not yet bound to a full DMA execution composition.

### GAPS
- Address-space incarnation generation.
- Translation/mapping generation and invalidation completion evidence.
- Device lease/reset generation binding.
- Page-fault revalidation rules.
- In-flight DMA behavior when capability/Region/mapping changes.

### CONTRADICTIONS
- PASID/IOVA/raw address must not become authority.
- Capability revoke cannot be claimed to stop already-submitted DMA without provider containment/closure evidence.

### REMOVE_OR_MERGE
- No TranslationAuthority.
- No IOMMU ledger inside RegionAuthority.
- Keep P04 separate from P01.

### NEW_REQUIRED
- Non-authoritative `DmaExecutionBindingV1` or equivalent correlation record.
- TranslationInvalidationEvidenceV1 with provider generation and completion semantics.
- Effect-ambiguity quarantine rule for revoke/reset races.

### AUTHORITY IMPACT
RegionAuthority does not own mappings. Process/VM address-space owner owns incarnation; platform/IOMMU provider owns mapping; device owner owns lease/reset; ExternalOperationAuthority owns operation lifecycle.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT`/`RUNTIME_ONLY` in ExternalRuntime/provider adapter for current mapping/reset generation and invalidation/containment evidence.

### COMPILER IMPACT
No required compiler change. Compiler may later supply footprint evidence only.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Confused-deputy use of a valid mapping for the wrong Region/use generation.
- Stale PASID/IOMMU handle reuse (ABA).
- DMA continues after authority revoke while OS assumes cancellation.

### CORRECTNESS RISKS
- TOCTOU between final mapping check and device submit.
- Page fault remaps to bytes outside authorized Region.
- Device reset loses containment state while operation is considered closed.

### PERFORMANCE RISKS
- Frequent invalidation/generation checks can be expensive; use narrow pinned leases/caches only for immutable facts.

### REQUIRED TESTS
- Unmap-vs-submit race.
- Capability/Region revoke after provider admission and before submit.
- Translation change during in-flight DMA.
- PASID/domain ID reuse ABA.
- Page-fault revalidation positive/negative tests.
- Device reset at every lifecycle state.
- Guest/SecureDomain mapping mismatch tests.
- Provider-loss with possible write -> quarantine test.

### FORMAL WORK
- TLA+ for map/unmap/revoke/submit/reset interleavings.
- Finite model for RegionUse × address-space × translation × device lease composition.

### DEPENDENCIES
Hard: P01 + contract extension core. P05 closes the refinement relation. Required before DMA-capable P08/P10 and before multi-host.

### EXIT CRITERIA
- Every DMA effect binds RegionUse + address-space + device + translation + provider generations.
- No raw address-like value passes an authority boundary as permission.
- Invalidation/closure is executable for the selected provider contour.
- Ambiguous possible writes quarantine rather than reclaim.

### ROADMAP PATCH
Make P04 the generation/TOCTOU closure phase. Treat DmaExecutionBinding as correlation evidence only.

## Objective

High-priority correctness/security phase. Add generation-bound correlation, not a new authority ledger.

## Live baseline and existing mechanisms
- RegionAuthority and external operation lifecycle.
- ExternalGenerationSet-style provider generation contracts.
- Platform/provider adapter boundaries.

## Required implementation changes
- Introduce DmaExecutionBindingV1 sidecar.
- Add final live revalidation immediately before submit.
- Add invalidation/containment adapter contract.
- Add device page-fault revalidation hook.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-DMA-TRANSLATION-BINDING`  
**Claim ceiling at emulator/runtime freeze:** `RuntimeEnforced + ExecutableAdapter for named emulator/provider adapter; physical IOMMU isolation not ProductionQualified`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P04-A contract sidecar.
- P04-B owner-generation capture/revalidation.
- P04-C provider invalidation/reset adapter.
- P04-D race/fault suite.
- P04-E qualification artifact.

## Rollback

Disable DMA-binding gate; new operations use existing staged/copy path. In-flight operations must still reconcile/contain before Region reclaim.

## Managed mapping revocation observation — 2026-09-30

Evidence: `artifacts/v6/iteration-20260930-p04-revoke-fault-matrix`.

## Managed IRQ delivery and MMIO closure interlocks — 2026-10-01

IRQ Poll/Complete now use the existing capability owner for fresh exact source admission,
with a trusted local route check under its gate and provider callbacks outside delivery locks.
The existing IRQ record holds DeliveryInFlight through provider callbacks and exact staged
mailbox publication; overlapping delivery and closure are rejected. Revoke during Poll
prevents fresh Complete admission. Already-admitted successful Complete can publish the
exact staged event; closure retry must wait for delivery settlement. Fault/reset/exception
or uncertain post-Poll permission loss retains route/parent pins. NotAccepted permits
exact rollback and retry without interpreting a terminal failure as containment.

The existing MMIO record owns ClosureInFlight and local route denial at closure begin.
Inline/concurrent nested closure is rejected before the provider callback; the flag covers
pre/post generation getters, and finally balances it. Exact success with stable generation
precedes closed publication. MMIO bind admission now uses existing parent PendingMmioBinds
and capability admission gate. Known late receipts are closed on revoked/process-exit
rejection; ambiguous cleanup or generation loss retains receipt/pin. Lost receipts retain
source correlation on the device record and block capability closure/reclaim. Pending
counters are admission interlocks only. Evidence:
`artifacts/v6/iteration-20261001-mmio-bind-admission`. Reconciliation remains Missing.
Stable NotAccepted is separately covered from reset-before-NotAccepted, Denied/Faulted/
Revoked responses and receipt loss with/without reset. Both pending counters balance;
settled unresolved effects report PlatformFaulted instead of active Draining and retain
the source/parent pin. Evidence: `artifacts/v6/iteration-20261001-mmio-fault-matrix`.

Evidence: `artifacts/v6/iteration-20261001-irq-delivery-permission` and
`artifacts/v6/iteration-20261001-mmio-closure-interlock`. Claims apply to the recorded
source/package tuple only: RuntimeEnforced for these managed interlocks, ModelOnly for
provider fault models. Physical closure and exact machine qualification remain FutureGated.
All v6 gates OFF; V1 APIs/enums preserved; ISA/opcode/CPU architecture impact NONE.
The selected V1/V2 mapping contour now marks local mapping authorization immediately after
CapabilityAuthority revoke and before provider cascades. Pending mapping admissions interlock
closure; late publication revalidates capability permission while preserving the closure handle,
Region reservation and budget association. Snapshot/mark uses the bridge lifecycle gate.

Lost-receipt exceptions retain a faulted record in the existing mapping owner, including the
original capability correlation and backend epoch, with no inferred provider lease. Repeated
revoke cannot report closure from an empty mapping snapshot. This is ambiguity tracking,
not exact reconciliation. The exact consumer for recovering a lost receipt remains Missing.

The matrix covers V1/V2 callback and overlapping-thread revoke, stable NotAccepted versus reset
continuity loss, receipt loss with/without reset, stale mapping and closure generations, retry
with exact closure, budget/reservation retention and release, and DSC1 cancellation callback
ordering. These tests establish managed RuntimeEnforced behavior only. The full physical DMA,
IOMMU and deployment exit criteria remain FutureGated; all v6 gates remain OFF. ISA impact NONE.

## Region backing closure versus mapping admission — 2026-10-02

Existing RegionAuthority now owns a narrow persistent backing-closure admission flag tied to exact lease/owner/Region generation. BeginBackingClosure refuses existing mapping/grant reservations and atomically forbids new mapping/grant/backing effects; it records no provider closure. CXL placement Close and tracked orphan-backing teardown consume this admission before provider release. Failure retains the flag and backing pin; existing ReleaseBacking clears it only on the actual consumer's successful closure path. Stale/wrong owner causes no partial mutation. Concurrent tests cover map/backing-bind denial during closure and after fault, then fresh V1 map after successful release. Physical closure/reset qualification remains FutureGated, gate OFF; ISA/opcode/CPU NONE.

## In-flight CXL backing creation admission — 2026-10-02

Existing Region backing record now holds balanced pending creation counters through provider callback, receipt tracking and placement publication. Existing BeginBackingClosure/ReleaseBacking deny while pending; bridge process teardown also refuses closure before late receipts are tracked. Process owner gate validates exact principal/state for local creation admission and Active placement publication. Fabric/memory callbacks run outside owner locks; fresh process/fabric/device checks reject late activation after exit/reset. Known receipts remain with existing consumers for real teardown retry; unknown acceptance retains existing uncontained pin. Managed reentrant/concurrent exit and reset tests pass; counters carry no capability authority. Physical provider-global/device-parent closure composition remains Partial/FutureGated; no production claim. Gates OFF; V1 compatible; ISA/opcode/CPU NONE.

## CXL zero-effect rejection tuple — 2026-10-02

CXL NotAccepted is provider-contract zero-effect evidence, not terminal-response closure. Existing bridge accepts it as a non-quarantined rejection only after fresh exact endpoint generation/features and (for memory/coherent) fabric binding equality. Generation drift or failed/throwing observation retains the existing uncontained-effect pin; the observation itself never proves closure. Stable rejection preserves V1 compensation/reclaim. Managed backing fabric/memory tests cover stable and reset rejection plus failed fabric observation; direct fabric/coherent branches share the guard but branch-specific coverage remains Partial. Unknown receipt reconciliation and device-parent closure composition remain Missing/Partial. Gate OFF; managed RuntimeEnforced only; physical FutureGated; Java excluded; ISA/opcode/CPU NONE.

## CXL parent device lifetime — 2026-10-02

Existing device record holds opaque CXL child reservations from local fabric admission through exact fabric closure. They carry no capability permission or provider closure fact. For managed fabric/memory/coherent binding lifetime, device/domain revoke cannot report closure while their CXL child reservations remain. Type-2 operation lifetime is not yet composed with this bridge reservation: the existing fabric-manager consumer must be connected before extending the claim to Type-2 effects. Bridge retains receipts; existing Type-3 Close/process teardown consumers release the reservation after exact provider closure with endpoint and platform source continuity. Fresh original device capability/process/domain admission runs before all bridge fabric/memory/coherent creation callbacks; no provider code under local commit locks. Fabric creation/child admission/closure interlocks prevent late child tracking after fabric release; collided/malformed unknown receipts preserve existing uncontained pin. Stable zero-effect rejection releases the unused reservation. Managed tests cover reentrant/concurrent device and capability revoke, late receipts, exact subject/generation failures, multiple placements, actual closure retry and independent healthy endpoints. SharedReadMostly/DirectCoherentWrite remain FutureGated; coherent positive callback behavior is unqualified. Physical containment and exact unknown receipt reconciliation remain FutureGated/Missing. Gates OFF; V1 public API/enums/packages unchanged; ISA/opcode/CPU NONE.

## Type-2 operation parent closure — 2026-10-02

The actual CxlType2AcceleratorService now acquires an opaque operation pin from the existing fabric owner before provider submission and retains it through callback, late receipt tracking and exact provider closure. The existing original device CapabilityAuthority/process/domain admission is freshly checked; fabric metadata is not permission. Fabric/device release and process teardown cannot bypass a pending or unresolved operation. Cancel requests cancellation; only separate exact Release success with endpoint/fabric source continuity records closure. Release exception, lost reply, stale/malformed receipt and reset retain quarantine, even if the provider has physically removed its model record. Cached successful closure supports existing local settlement retry without inferring closure from not-found. PendingAdmissions closes manager reconfiguration during submit; selected manager query/submit/drain callbacks run outside its owner gate.

Named managed submit/parent/closure contour RuntimeEnforced; provider evidence ModelOnly. Full operation overlap/publication serialization, manager CompleteReconfiguration/pool callbacks and physical source incarnation remain Partial/FutureGated. Unknown receipt reconciliation Missing. Public V1 signatures/enums/packages unchanged; comments clarify cancel/release semantics. Gates OFF; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-type2-parent-closure.

## Fabric drain settlement refusal — 2026-10-02

Existing CxlFabricManagerAuthority now propagates exact operation Query failure and pre-submit Cancel/Release errors. Missing or stale principal/operation lookup is not closure evidence; manager admission remains faulted/closed and provider reconfiguration is not invoked. Pre-submit Release passes ProviderResourcesClosed=false so the authoritative operation owner independently rejects a concurrent submitted state; the earlier Query snapshot grants no closure claim. Positive Prepared/Admitted drains remain compatible. Named managed refusal RuntimeEnforced; exact post-exit terminal receipt reconciliation remains Missing, broader manager callbacks Partial, physical FutureGated. Gates OFF; V1 unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-fabric-drain-settlement.

## Fabric completion callback interlock — 2026-10-02

Existing CxlFabricManagerAuthority now admits one exact completion attempt under its existing gate, captures record/ticket/previous binding, then invokes CompleteReconfiguration and fresh QueryResource outside that gate. CompletionInFlight is only an interlock; it grants no authority and is balanced in finally. Reentrant/overlapping attempts are refused without a second provider callback. Commit requires the same record, Draining state, exact ticket/previous binding, replacement generation and endpoint/device generation, plus provider observation matching the returned binding and Bound state. Exception, malformed receipt and changed source observation fault local admission and retain the existing ticket/operation correlations; terminal response is no closure proof. Existing fresh admission remains independently necessary after replacement.

Named managed completion interlock/source rejection RuntimeEnforced; tests use ModelOnly provider. Physical source continuity/atomicity and deployment remain FutureGated; no machine legality claim. Full manager pool lifecycle, Register concurrency and unknown reconfiguration reconciliation remain Partial/Missing. Gates OFF; V1 API/enums/packages unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-fabric-completion-interlock.

## Fabric manager registration owner commit — 2026-10-02

Register now checks and commits duplicate admission under the existing manager gate, with provider QueryResource outside that gate. Commit repeats the presence check after callback; concurrent/reentrant registration cannot overwrite the first record or resurrect its Draining state. One local record remains the authoritative manager state. Registration is metadata correlation, not device capability permission, closure evidence or physical source continuity; every effect still requires fresh existing admission. Named managed duplicate-owner interlock RuntimeEnforced, provider ModelOnly. Pool assignment/release parent lifetime and unknown receipt reconciliation remain Partial/Missing; physical FutureGated. Gates OFF; V1 unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-fabric-register-interlock.

### Remaining standalone fabric-manager authority gap

Standalone Begin/CompleteReconfiguration and AssignPool currently receive metadata bindings/Region owner rather than the bridge's original device capability and parent reservation. Source search finds no production caller composing these methods with that original owner; current callers are model/conformance tests. The new callback and duplicate-registration interlocks do not qualify these standalone APIs as permission enforcement. Exact original-device authorization/parent composition and real deployment consumer remain Missing/Partial, gates OFF; no synthetic authority or closure API is introduced. Continue independent P05 pre-submit teardown/release trace work with its existing runtime consumer while this contour awaits a concrete authorized composition.

## 2026-10-02 P04 telemetry inspection subscription publication interlock

Closed bounded managed admission slice. Existing CapabilityAuthority now provides a narrow trusted local commit for telemetry subscription publication: exact capability/domain/process generation, lineage, active state, Read right and KernelService/TelemetryInspection resource are revalidated under its existing revocation gate. The callback only inserts the existing subscription record under telemetry gate; no provider, clock, trace or budget callback runs under capability/telemetry gates. Lock order: kernel lifecycle -> capability -> telemetry. SelfOperational and ServiceAggregate self projections retain existing manifest admission without requiring an inspection capability. Privileged self and cross-service projections require the dedicated source. Provisional budget refund on refusal/counter exhaustion occurs after owner gates release.

Requirements P04-TEL-PUB-001..004: exact source permission at publication, revocation/publication arbitration, failed publication no active record/provisional charge, V1 self fallback. Four new cases in TraceTelemetryAdmissionLifecycleTests cover pre-revoke, budget callback revoke, sampled commit/revoke and post-revoke refusal, exact domain/process generation refusal. Build: 0 errors/9 warnings, 21.09 seconds. Focused: 61 passed, no failures. Final broad and exact tuple/hashes: artifacts/v6/iteration-20261002-p04-telemetry-inspection-publication/audit.json.

CapabilityAuthority remains the sole capability owner, existing subscription record owns local state, ResourceBudgetAuthority owns charges. Admission first may publish before a later revoke; subsequent sampling uses existing projection validation. Sample enqueue and buffered read capability linearization are independently Partial and next dependency-ready work. Direct low-level process mutation outside lifecycle consumer, fatal allocation compensation and unknown accounting clearing remain Partial/Missing. No synthetic closure, reconciliation or authority. All v6 gates OFF, named managed guard ceiling RuntimeEnforced; physical contours FutureGated. Java excluded/skipped; ISA/opcode/CPU architecture impact NONE.

## 2026-10-02 P04 telemetry sample/read inspection permission boundary

Closed bounded managed subscription sample/read slice. Existing capability owner trusted commit is named CommitTelemetryPublication and used for admission, final enqueue and buffered read. Sample builds its observational snapshot outside owner locks, then revalidates projection, exact owner/subject lifecycle and capability before local queue/overflow mutation. Read captures the immutable subscription source tuple, then validates projection/capability under kernel lifecycle -> capability -> telemetry lock order before copying/draining the queue. Capability revoke completing before that commit denies publication; admission/publication before later revoke remains ordered earlier. No clock/provider/trace/budget callbacks under capability or telemetry gates. Refusal leaves buffered data, Dropped, state and charge unchanged; explicit close still uses existing accounting consumer without requiring inspection permission.

Requirements P04-TEL-USE-001..004: source permission at enqueue/read, no mutation on revoked source, snapshot callbacks outside permission locks, V1 authorized/self read compatibility. Seven new cases cover cross-service and privileged self revoke, callback revoke after projection validation, blocked snapshot/revoke for all three overflow policies, authorized cross-service/self drain after close. Initial build exposed two fixture errors (incorrect V1 overflow enum name), fixed to DropOldestWithMarker. Corrected build 0 errors/9 warnings, 21.01s; initial focused 70 passed; final focused 73 passed after three blocked-clock cases. Final exact broad/source/dependency tuple and SHA manifests: artifacts/v6/iteration-20261002-p04-telemetry-sample-read-permission/audit.json.

CapabilityAuthority remains the only permission owner, existing subscription record owns queue/state, existing ResourceBudgetAuthority owns charge. Local observation, publication, closure and refund remain distinct. Direct ProjectTelemetry result publication remains an independent Partial contour and next slice; full low-level process concurrency, fatal allocation compensation and unknown quantitative clearing remain Partial/Missing. Gates OFF, RuntimeEnforced only for named tested managed guards, physical FutureGated. Java excluded/skipped; ISA/opcode/CPU architecture impact NONE.

## 2026-10-02 P04 direct telemetry projection result permission

Closed bounded managed direct ProjectTelemetry slice. Initial source validation remains before observation collection; after BuildTelemetry and its configured clock callback, existing publication helper freshly validates requester/subject exact Process handles, manifest projection and dedicated inspection capability under lifecycle -> capability lock order. The existing capability owner trusted local commit is the permission linearization point for result admission; a later revoke does not retrospectively undo an earlier admitted result. Snapshot/time observations never supply permission. No clock/provider callback under final permission locks. Subscription sample/read reuse the helper with their original local queue mutation boundaries.

Requirements P04-TEL-RESULT-001..004: exact source permission after observation callback, exact requester/subject generation, clocks outside permission locks, preserved self/cross/privileged V1 projections. Six new cases: cross-service/privileged-self callback revoke, requester/subject exit during clock, blocked clock versus revoke, subject replacement with same ProcessId/new generation (old result denied, fresh request permitted). Build 0 errors/9 warnings 20.36 seconds before final ABA case; final focused compiles final test source, 79 passed/0 failed. Broad/source/package/toolchain tuple, hashes and durations: artifacts/v6/iteration-20261002-p04-telemetry-projection-permission/audit.json.

Supervisor projection uses a distinct ServiceSupervisor control source and bypasses this inspection projection route; that contour is independently Partial and next dependency-ready work. Snapshot metrics are observations, not coherent physical/closure evidence. Full multi-owner snapshot coherence, fatal allocation compensation, direct low-level process mutation and unknown clearing remain Partial/Missing. All gates OFF, named managed guard RuntimeEnforced, physical FutureGated. Java excluded/skipped; ISA/opcode/CPU architecture impact NONE.

## 2026-10-02 P04 supervisor telemetry control-source result admission

Closed bounded managed control-capability result slice. Real CapabilityAwareServiceSupervisor consumer forwards immutable principal/control source and subject to the existing kernel supervisor projection path. Snapshot collection/clock remains outside final admission locks. Under lifecycle -> capability lock order, kernel freshly resolves exact principal and subject process generations and existing CapabilityAuthority admits result only for live Configure|Execute KernelService/ServiceSupervisor source (domain/process generation and lineage included). SupervisorDenied remains the compatible control-refusal result. No supervisor lock acquired under lifecycle/capability, no callback in capability commit, no new authority/closure API.

Requirements P04-SUP-TEL-001..004: final exact control permission, exact principal/subject process tuple, clock outside final locks, V1 control/refusal compatibility. Five new cases: revoke before observation, clock callback revoke, principal/subject exit, blocked clock versus control revoke. Build 0 errors/9 warnings 20.92s. Focused 54 passed; real supervisor consumer regression 13 passed (actual Phase02ServiceSupervisorTests, initial filter mistyped Phase08 class). Final broad/tuple/hashes: artifacts/v6/iteration-20261002-p04-supervisor-telemetry-permission/audit.json.

Existing supervisor owner still validates ServiceInstanceHandle and captures metrics under its own gate before collection. The aggregate snapshot is observational, not atomic multi-owner state/physical proof; final supervisor-record/service-instance coherence is independently Partial. Other mutating supervisor operations retain initial-control-to-local-effect gaps; next dependency-ready bounded slice is Register local registration control interlock, using the actual existing owner. Fatal allocation/direct low-level process concurrency/unknown clearing Partial/Missing. All gates OFF, managed guard RuntimeEnforced only, physical FutureGated. Java excluded/skipped; ISA/opcode/CPU architecture impact NONE.

## 2026-10-02 P04 supervisor Register control-source publication

Closed bounded managed local registration slice. Actual Register still performs initial control validation, then under existing supervisor gate freshly resolves principal and calls narrow existing CapabilityAuthority registration commit. Its existing capability gate revalidates exact control source/domain/process generation, active state, lineage, Configure|Execute and ServiceSupervisor resource and spans only local registry insertion/graph validation/normal rollback. Lock order supervisor -> capability; no callback acquires supervisor gate under capability, and no clock/factory/provider/kernel-lifecycle/trace call in trusted mutation. Record/graph remains existing supervisor owner, capability permission remains existing capability owner.

Requirements P04-SUP-REG-001..004: source permission at publication, revoked refusal no record/process effect, exact source tuple and V1 duplicate/graph rollback compatibility. Four new cases: worker waiting on held supervisor gate after initial validation then capability revoke/domain revoke/principal exit; stale generation/wrong domain/rights/resource callback refusal plus positive registration/duplicate. Existing cycle rollback, start/replacement, telemetry and teardown regressions included. Build 0 errors/9 warnings 20.64s; focused 42 passed. Broad/current tuple/hashes: artifacts/v6/iteration-20261002-p04-supervisor-registration-permission/audit.json.

All gates OFF, named managed source/publication guard RuntimeEnforced only; full cross-owner injected clock/factory concurrency remains Partial (other existing owner paths still invoke clocks under gates). Fatal allocation/graph-exception compensation and direct low-level process mutation Partial; no physical qualification. Other supervisor Start/effect admission source continuity remains Partial and next actual consumer slice. Unknown quantitative/invocation/mapping/IRQ/root clearing Missing, pins retained. Java excluded/skipped; ISA/opcode/CPU architecture impact NONE.

## 2026-10-02 P04 operation capability clock staging and consumer revalidation

Closed bounded managed operation admission slice. Existing CapabilityAuthority validates static exact source under gate, collects configured GetUtcNow outside gate, returns PlatformFaulted for nonfatal clock exception, then freshly validates exact capability/domain/process/resource generation, lineage/state, operation/session/lifetime constraints before consumptive commit. V2 opaque handle resolution releases its outer gate before calling V1 acquisition, preserving realm/nonce validation and V1 compatibility. Clock snapshot is policy input, not standalone permission or physical deadline evidence.

Actual AdmitSessionCapabilityEffect consumer revalidates exact caller acceptance and existing session pin after clock, before quota/one-shot/lease mutation. Optional internal consumer preparation callback executes outside capability gate; source is validated before and again after it. Existing finally releases pins/leases on refusal; quota/one-shot not refunded after real commit. No new owner/authority/ledger/closure API. Existing lease grandfather policy unchanged. Other actual resource admission/protected information flow consumers retain existing post-acquire validation and regressions.

Requirements P04-OP-CLOCK-001..004: no operation clock under capability gate including V2, fresh exact source after clock/callback, clock fault/revoke no consumption, session/process consumer validation before consumptive commit. Nine new cases: blocked clock query/V1/V2 with concurrent revoke, nonfatal fault query/V1/V2 and recovery, actual session clock-close/fault/source-revoke with balanced pins and unchanged quota. Build 0 errors/9 warnings 20.17s before new tests; focused compiles final tests, 122 passed/0 failed. Final broad/current tuple/hashes: artifacts/v6/iteration-20261002-p04-operation-clock-admission/audit.json.

Configured logical clock lifetime checks are not hard deadlines/WCET or physical time proof. Resource-use GetUtcNow remains under its gate, independently Partial and next actual contour. Full cross-owner callbacks/fatal allocation compensation Partial, controlled Start source barrier and unknown closure consumers Missing. Gates OFF, named managed guard RuntimeEnforced only; Java excluded/skipped, ISA/opcode/CPU impact NONE.

Final actual-consumer qualification: Phase144ComposedAdmissionTests requires grandfathered pinned commit when close occurs after final session revalidation. A trial keeping the hook before clock then revalidating its closed pin contradicted that consumer: focused 123 passed but broad trial 4074 passed/1 failed/2 skipped. Final hook follows actual final session check after clock; initial preparation also rejects already closed pins before clock. QualificationHookFollowsActualFinalSessionRevalidationAndClockObservation verifies the final ordering. Actual Phase144 consumer added to focused filter: final 134 passed. Final broad-consumer-final 4075 passed/0 failed/2 skipped; prior TRX retained, not reused as final proof.

## 2026-10-02 P04 resource-use clock staging and actual resource consumer

Closed bounded managed resource-use query/acquire slice. Existing capability owner statically checks exact source under gate, collects configured GetUtcNow outside gate, then revalidates domain/process/resource generation, active state/lineage, resource grant lifetime and canonical envelope subset under gate. Acquire no longer calls clock-reading validation under an outer owner lock. Nonfatal clock fault returns existing PlatformFaulted before lease allocation. Actual resource admission rechecks exact live process/new-effect acceptance and Prepared external operation after clock via internal consumer preparation outside capability gate; source is checked before/after preparation. No new authority/ledger/closure API.

Requirements P04-RES-CLOCK-001..004: clock outside query/acquire owner gate, fresh exact grant/source subset, no lease on fault/revoke, actual process/operation consumer revalidation and only provisional charge compensation. Six new cases: blocked query/acquire with concurrent revoke, nonfatal query/acquire fault/recovery with no consumer callback/lease, actual budget-prepared clock fault and actual changed-operation state refusal. Existing ancestor revocation, clock boundaries, envelopes, dispatch, resource donation/durable admission, composed session and teardown regressions included. Build 0 errors/9 warnings 26.59s before new tests; focused final compiles new tests, 159 passed/0 failed. Final broad/source/dependency tuple/hashes: artifacts/v6/iteration-20261002-p04-resource-clock-admission/audit.json.

Actual pre-submit budget consumer compensates its unbound provisional charge; no reset/rollback of externally changed operation state and no containment/settlement inference from error. Time/grant/lease identity is not permission or physical expiry/minimum service/deadline/WCET proof. Gates OFF, named managed guard RuntimeEnforced only, physical FutureGated. Fatal/full arbitrary cross-owner callback contours Partial, controlled Start/unknown closure consumers Missing. Java excluded/skipped; ISA/opcode/CPU impact NONE. Next independent local consumer: supervisor Query control-source publication interlock.
