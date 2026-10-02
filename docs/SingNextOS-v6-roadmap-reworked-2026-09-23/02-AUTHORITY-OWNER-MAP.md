# Corrected Authority-Owner Map

### Pre-submit accounting quarantine observation — 2026-10-02

ExternalOperationAuthority owns exact resource association/quarantine journal; ResourceBudgetAuthority owns reservation accounting independently. Existing compensation/failure-winner observer and local release/teardown are real consumers. ADR-008 kind16 preserves accounting facts even after local pre-submit release; that local operation/Region/process lifecycle does not clear the budget. Pre-submit CancelLeasePreSubmit refuses Quarantined, and external exact settlement requires submitted completion; no pre-submit clearing consumer is established. Quantitative reservation remains quarantined, reclaim Missing; physical FutureGated. No new authority/ledger/closure API. Gates OFF; named managed observation RuntimeEnforced; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-pre-submit-accounting-quarantine.

### Pre-submit failure live observation — 2026-10-02

Existing ResourceAdmissionCommit submit/close interlock selects the failure winner; ExternalOperationAuthority and ResourceBudgetAuthority still own compensation facts. Optional internal callback after compensation observes those existing facts through the existing sink. Exact captured memory/temporal sidecar/process/operation/budget tuple is attribution only; no provider callback or fresh effect permission is introduced. Cancelled-only trace cleanup now waits for actual local Released using existing completion/delivery interlock; sink status never controls owner release. Existing process teardown, compensation and duplicate-submit consumers are tested. Unsupported pre-submit accounting quarantine remains Partial, pins/unknown closure unchanged; gates OFF, named managed live observation RuntimeEnforced, physical FutureGated, Java excluded, ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-pre-submit-live.

### Non-resource local release observation — 2026-10-02

ExternalOperationAuthority and RegionAuthority retain local release ownership; ResourceBudgetAuthority remains independent. ADR-007 kind15 projects actual Released without a resource association, not provider closure or accounting settlement. Existing HybridCpuExternalOperationProvider.Release is a tested consumer; Type-2 exact provider Release/source continuity and parent pin remain separate. Snapshot has no closure receipt, so physical containment is FutureGated; accounting/drift markers cannot use this local branch to bypass reconciliation. No authority/ledger or closure API added. Gates OFF; named managed observation RuntimeEnforced; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-non-resource-release.

### Pre-submit local release observation — 2026-10-02

ExternalOperationAuthority owns cancellation-before-submit and local operation/Region-use release. RegionAuthority commits ReleaseUsesAtomically; ResourceBudgetAuthority retains independent reservation/cancellation/settlement facts. ADR-006 observations project existing exact owner journal transitions through the offline checker and grant no provider closure or reclaim permission. Kernel cancellation/release and process teardown are actual writers/consumers; normal semantic live registration begins after RecordSubmission, so pre-submit live delivery remains Partial. No new owner/ledger. Gates OFF; named managed observational guard RuntimeEnforced; physical FutureGated; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-pre-submit-release.

The map is reconstructed from live architecture surfaces and previous completed architecture contracts. Descriptor/evidence producers are not promoted to owners.

| Logical fact | Authoritative owner | Generation/epoch | Mutation / linearization point | Evidence producers | Forbidden substitute owners |
|---|---|---|---|---|---|
| capability/effect permission | `CapabilityAuthority` | capability/derivation/revocation generation | capability grant/derive/revoke commit | sentries, audit logs | provider receipt, compiler proof, device identity |
| process/service incarnation | `ProcessRegistry` / service incarnation owner | process/service generation | create/replace/terminate registration commit | boot/runtime registry events | PID value, PASID, endpoint name |
| endpoint session lifecycle | `EndpointSessionRegistry` | session generation | open/close/rotate commit | transport events | connection ID, provider session |
| invocation lifecycle | invocation owner | invocation generation | create/terminal transition | IPC trace | completion receipt alone |
| Region ownership/use | `RegionAuthority` | Region generation / `MutationEpoch` / use generation | allocate/borrow/transfer/reclaim/subrange state commit | provider memory evidence | mapping, coherence, physical address |
| sealed object state | `SealedObjectAuthority` | seal/object generation | seal/unseal/rotate commit | crypto/provider evidence | ciphertext blob, measurement |
| external operation lifecycle | `ExternalOperationAuthority` | operation generation | prepare/admit/submit/terminal/quarantine transition | provider receipts | provider correlation ID |
| publication / response truth | publication/response owner | response/publication generation | publish/commit response transition | visibility/completion evidence | completion, visibility, retire |
| quantitative resource accounting | `ResourceBudgetAuthority` | budget/lease generation | reserve/bind/consume/settle/release | usage measurement | scheduler, capability ledger, telemetry |
| scheduler choice | scheduler policy component | policy snapshot only, not authority generation | scheduling selection | cost/telemetry | permission, budget truth, legality |
| provider admission | concrete provider/runtime admission subsystem | provider/session/manifest generation | provider-local admit/submit commit | adapter receipts | SingNext capability, compiler proof |
| HybridCPU machine legality | `IRuntimeLegalityService` / GuardPlane path | runtime/guard/certificate generation as applicable | legality decision for current machine state | SafetyVerifier, certificates | SingNext capability, compiler metadata |
| machine replay state | HybridCPU runtime replay machinery | replay phase/certificate generation | replay-state transition/retire boundary | replay certificates | OS permission |
| semantic external replay/dedup | external operation/invocation lifecycle owner | operation/invocation generation | dedup/terminal transition | request correlation | HybridCPU replay state |
| compiler evidence | compiler/toolchain produces; verifier accepts/caches evidence | compiler contract/schema/digest | proof validation/cache insertion | compiler | never an authority owner |
| virtual domain state | VirtualDomain runtime/registry | domain generation | create/reconfigure/destroy | VMX projection evidence | VMCS field identity, topology |
| secure domain state | SecureCompute domain owner | secure-domain/policy generation | create/reconfigure/destroy | measurement/attestation | attestation token, VirtualDomain owner |
| device lease / assignment | platform/device owner | device lease/reset generation | assign/revoke/reset transition | device/provider events | device ID, attestation |
| address-space incarnation | process/VM address-space owner | address-space generation | map-space replace/destroy transition | page-table/IOMMU adapter | VA/PASID |
| IOMMU/translation mapping | platform/IOMMU provider owner | translation generation | map/unmap/invalidate completion | IOMMU/provider evidence | RegionAuthority, IOVA, PASID |
| CXL/provider topology state | platform/provider adapter | provider/fabric generation | reconfigure/reset/disconnect | topology/health events | app/capability ABI |
| boot evidence | boot/loader evidence owner | boot measurement/generation | handoff record commit | firmware/loader | runtime capability/Region authority |
| remote delegated lease | original logical owner issues; remote holder owns local lease state only | owner epoch + lease epoch | issue/renew/revoke/expiry transition | fabric transport | route/topology, remote cache |
| energy/thermal measurement | provider/runtime measurement subsystem | measurement/provider generation | measurement sample/limit transition | counters/firmware | budget ledger unless committed budget fact |

## Cross-owner commit rule

Any operation depending on multiple mutable owners uses the established discipline:

```text
read/prepare immutable intent
-> obtain/pin narrow owner leases where available
-> provider-local preparation/admission
-> final revalidation of every unpinned mutable generation
-> single existing submit/commit linearization point
-> provider call outside unrelated owner locks
-> record effect-possible state
-> close visibility/publication/settlement/reclaim in owner order
```

There is no global runtime `GlobalStateManager` and no second coordinator ledger.

## CXL parent lifetime refinement — 2026-10-02

PlatformAuthorityBridge.Device existing device record owns CXL parent-lifetime reservations; CxlAuthorityBridge owns exact fabric/memory/coherent receipts and existing ambiguity pins; RegionAuthority owns backing; CapabilityAuthority remains the only capability permission owner. Reservation registration/removal is local owner state and never proves closure. Release consumes the existing CXL provider closure result plus exact source continuity. Lock order: kernel process gate -> capability gate -> device lifecycle gate -> local bridge child admission gate. Provider callbacks and generation reads stay outside these new local commit locks. New device reservation tokens grant no authority; bridge child counters only interlock admission/closure. Unknown receipt reconciliation remains Missing, with no duplicate authority or closure API.

### Type-2 operation pin owner — 2026-10-02

Existing bridge fabric record owns local parent-lifetime operation pins; actual Type-2 adapter consumer owns exact provider receipt/callback/closure-step correlations. Existing ExternalOperation owner owns operation lifecycle/settlement and RegionAuthority owns use/backing reservations. CapabilityAuthority alone owns source permission. Pins/receipts never authorize effects. Selected manager admission/drain callbacks execute outside its gate; new local admission lock order remains kernel -> capability -> device -> bridge. Adapter receipt gate never wraps provider callbacks. Cancellation acknowledgement is separate from exact Release closure; failed local settlement retains parent pin for the existing process teardown retry. Unknown closure/source continuity remains quarantined. Broader manager completion/pool and operation concurrency are Partial; physical gate OFF.

## Fabric completion callback interlock — 2026-10-02

Existing CxlFabricManagerAuthority now admits one exact completion attempt under its existing gate, captures record/ticket/previous binding, then invokes CompleteReconfiguration and fresh QueryResource outside that gate. CompletionInFlight is only an interlock; it grants no authority and is balanced in finally. Reentrant/overlapping attempts are refused without a second provider callback. Commit requires the same record, Draining state, exact ticket/previous binding, replacement generation and endpoint/device generation, plus provider observation matching the returned binding and Bound state. Exception, malformed receipt and changed source observation fault local admission and retain the existing ticket/operation correlations; terminal response is no closure proof. Existing fresh admission remains independently necessary after replacement.

Named managed completion interlock/source rejection RuntimeEnforced; tests use ModelOnly provider. Physical source continuity/atomicity and deployment remain FutureGated; no machine legality claim. Full manager pool lifecycle, Register concurrency and unknown reconfiguration reconciliation remain Partial/Missing. Gates OFF; V1 API/enums/packages unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-fabric-completion-interlock.

## Fabric manager registration owner commit — 2026-10-02

Register now checks and commits duplicate admission under the existing manager gate, with provider QueryResource outside that gate. Commit repeats the presence check after callback; concurrent/reentrant registration cannot overwrite the first record or resurrect its Draining state. One local record remains the authoritative manager state. Registration is metadata correlation, not device capability permission, closure evidence or physical source continuity; every effect still requires fresh existing admission. Named managed duplicate-owner interlock RuntimeEnforced, provider ModelOnly. Pool assignment/release parent lifetime and unknown receipt reconciliation remain Partial/Missing; physical FutureGated. Gates OFF; V1 unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-fabric-register-interlock.

### Remaining standalone fabric-manager authority gap

Standalone Begin/CompleteReconfiguration and AssignPool currently receive metadata bindings/Region owner rather than the bridge's original device capability and parent reservation. Source search finds no production caller composing these methods with that original owner; current callers are model/conformance tests. The new callback and duplicate-registration interlocks do not qualify these standalone APIs as permission enforcement. Exact original-device authorization/parent composition and real deployment consumer remain Missing/Partial, gates OFF; no synthetic authority or closure API is introduced. Continue independent P05 pre-submit teardown/release trace work with its existing runtime consumer while this contour awaits a concrete authorized composition.

### Pre-submit owner prefix validation — 2026-10-02

Projection now accepts actual TeardownCancelledBeforeSubmit and owner-confirmed Released from Prepared/Admitted, including repeated teardown after explicit cancellation, only with a cancelled owner disposition and NotCrossed effect boundary. Complete exact owner history/state/binding validation still runs. Actual kernel process teardown is the consumer; tests query its existing committed owner record after exit. Removed cancellation writer, forged disposition or changed effect boundary is rejected. No Submit, EffectPossible, Settled or Released trace event is fabricated for these pre-submit states.

This closes named managed owner-prefix validation only (RuntimeEnforced). The V1 semantic vocabulary begins with Submit and has no pre-submit cancellation/release events: observation of those lifecycle facts remains Missing/Partial, not a qualified full trace or settlement/closure proof. The empty prefix is stuttering relative to the supported post-submit semantic alphabet, never evidence of physical no-effect. No enum/API/schema change without ADR. Submitted release still requires ordered real ResourceSettled evidence; temporal settlement cannot fabricate it. Gates OFF; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-pre-submit-owner-prefix. Next independent consumer: verify actual trace registration cleanup after pre-submit cancellation/teardown; full vocabulary requires an explicit additive-contract/ADR decision.

### Resource pre-submit writer guards — 2026-10-02

Existing ExternalOperationAuthority now binds a resource lease only to Active Admitted state, and marks CancelledPreSubmit only after its own Cancelled disposition with no submitted binding in Prepared/Admitted or already Released pre-submit lifetime. Bound budget/resource state alone is not proof of no submit; a Submitted operation can still have an unconsumed Bound lease between owner submit and consumption. Rejection performs no resource/budget/history mutation. Actual PrepareResourceExternalAdmission and CancelUnsubmittedResourceBinding callers remain compatible; Released pre-submit compensation retains its real existing consumer.

P05 observation matches these writers: ResourceLeaseBound occurs once in Active Admitted; ResourceCancelledBeforeSubmit requires preceding exact resource association, owner-confirmed pre-submit cancellation and no submitted history. Duplicate binding, association after cancellation and fabricated resource cancellation are rejected without inferring budget settlement/authority. These are local writer/history guards, not a second resource ledger. Named managed guards RuntimeEnforced; V1 compatible, gates OFF; Java excluded; ISA/opcode/CPU NONE. Unknown receipt reconciliation and full pre-submit vocabulary remain Missing/Partial, physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p05-resource-pre-submit-guards.

Trace registration cleanup is VerifiedExisting for current callers: ResourceAdmissionProtocol invokes submissionObservation after committed RecordSubmission; V6 memory/temporal registration therefore observes Submitted. Owner-confirmed pre-submit cleanup guard remains compatible and no pre-submit sink API is invented. Next dependency-ready audit: resource quarantine/settlement owner writer edges and observable projection, without deriving closure from receipt absence.

### Resource terminal history association — 2026-10-02

P05 projection now requires the real preceding ResourceLeaseBound owner event for resource quarantine and settlement, forbids these after pre-submit resource cancellation or terminal settlement, and checks settlement writers' DeviceComplete/Visible/Published state edge. Missing association, forged settlement from Submitted and quarantine after terminal settlement are rejected. Actual kernel settlement and existing operation writers produce the positive histories; durable admission/settlement regression remains covered. Resource association and observational counters never authorize access or prove budget settlement/closure; the existing budget owner and exact consumer remain authoritative. Unknown receipt absence cannot supply missing association or closure.

Named managed observational guard closed (RuntimeEnforced); full pre-submit vocabulary remains Missing/Partial; physical/provider containment remains FutureGated. No owner/API/enum/package/schema change; all gates OFF, Java excluded, ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-resource-terminal-history. Next dependency-ready slice: budget/owner settlement reconciliation when provider loss overlaps an in-flight exact receipt, using existing consumer and reservation owner.

### Cancellation support admission owner — 2026-10-02

ExternalOperationAuthority remains the authoritative admission owner. Its one pure cancellation-support validator rejects unknown mandatory enum values; public and virtual/exact-mapping kernel routes call it before cancellation consumer binding or budget reservation, and AdmitCore rechecks it before Region acquisition/admission publication. CancellationScopeAuthority, RegionAuthority and ResourceBudgetAuthority retain their separate facts. Validation is not permission and creates no owner/ledger. Unknown contracts cannot silently downgrade to BeforeSubmissionOnly. Current evidence: artifacts/v6/iteration-20261002-c0-cancellation-admission; gates OFF, managed guard RuntimeEnforced, physical FutureGated, Java excluded, ISA/opcode/CPU NONE.

### Independent accounting quarantine observation — 2026-10-02

Existing ExternalOperationAuthority owns resource association and settlement transition history; ResourceBudgetAuthority owns exact reservation/accounting. P05 ResourceAccountingQuarantined=12 observes those existing records, while Quarantined retains external-effect ambiguity semantics. Neither trace event grants authorization/closure/reclaim. Actual kernel exact settlement/reconciliation is the consumer and no additional authority/ledger is added. ADR-005 explains the narrow additive software enum requirement and fail-closed older consumers. Gates OFF, named managed distinction RuntimeEnforced, physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p05-accounting-quarantine; Java excluded; ISA/opcode/CPU NONE.

### IPC ownership quantitative routing — 2026-10-02

RegionAuthority remains the sole local ownership/loan owner; ResourceBudgetAuthority remains the quantitative owner. Existing kernel routing composes exact process/generation admission, target memory reservation and actual Region transfer for ChannelRegistry/ResponseRegistry consumers. TCB pair loan preparation runs only after target budget admission. Captured payer association is billing routing, never Region authorization: V1 shared-domain peer authorization remains independently validated. Old quarantined payer and new target generation charges are retained separately. Exception budget quarantine is not closure; provisional ambiguity clearing remains Missing. No new authority/ledger/GlobalState. Named managed guard RuntimeEnforced; gates OFF; physical FutureGated; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-ipc-memory-budget-composition.

### Budget release provenance — 2026-10-02

ResourceBudgetAuthority owns immutable runtime-attached release provenance in its existing reservation record, set atomically by ReserveIfAttached. Public explicit release cannot detach a live runtime resource's charge. Existing kernel resource/rollback consumers retain TCB release through that same owner. Snapshot/trace/inspection of a reservation handle confers no lifecycle permission. Exact process/reservation generation and state remain independently checked. Checkpoint-storage/temporal-composition provenance separately Partial; no new ledger or closure API. All gates OFF, managed guard RuntimeEnforced, physical FutureGated, Java excluded, ISA NONE. Evidence: artifacts/v6/iteration-20261002-p10-attached-budget-release.

### Checkpoint budget release consumer — 2026-10-02

Existing ResourceBudgetAuthority runtime-attached provenance also covers ReserveCheckpointStorage. Ordinary checkpoint record and stateful suspension record retain their distinct lifecycle owners; actual delete/resume/discard/reconciliation route quantitative release to the same existing budget owner. Local stored-image closure and budget release do not prove persistence/durability/provider closure. Captured exact original source generation survives process retirement for ordinary delete. No authority/ledger added; temporal-composition generic release separately Partial, gates OFF, RuntimeEnforced managed guard, physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p10-checkpoint-budget-release; Java excluded; ISA NONE.

### Temporal generic release consumer — 2026-10-02

Existing ResourceBudgetAuthority temporal claim is the authoritative quantitative consumer association. Generic release refuses both provisional/committed claims; exact abandonment, coordinator cancellation and settlement remain real consumers. Coordinator receipt/provider model reservation never confers SingNext execution permission. Public snapshot handle cannot cancel that relationship. Shared generic CancelLeasePreSubmit consumers remain independently Partial; no second ledger/authority. All gates OFF, named managed guard RuntimeEnforced, capacity producer ModelOnly and physical FutureGated, Java excluded, ISA NONE. Evidence: artifacts/v6/iteration-20261002-p10-temporal-budget-release.

### Temporal cancellation quantitative consumer — 2026-10-02

Existing budget owner validates exact committed TemporalComposition for CancelLeasePreSubmit, under its existing gate before mutation/idempotence. Actual coordinator supplies binding.Id only after its real unused model capacity close; default V1 consumers remain valid only for unclaimed reservations. Composition identity selects consumer, never SingNext/provider permission or physical closure. Provisional claims remain pinned until commit/exact abandonment. No new authority/ledger/closure API. All gates OFF, managed guard RuntimeEnforced, provider ModelOnly, physical FutureGated, Java excluded, ISA NONE. Evidence: artifacts/v6/iteration-20261002-p10-temporal-budget-cancellation.

### Telemetry subscription effect permission — 2026-10-02

Existing CapabilityAuthority owns exact inspection source validation and serializes revoke with local admission/enqueue/buffered-read commits. Existing subscription record owns queue/overflow/state; existing budget owner and actual close/teardown consumers own reservation accounting. Lifecycle -> capability -> telemetry locks; clock/snapshot collection outside capability/telemetry gates. A buffered record/handle never grants read permission. Revoke refuses later commits without queue/charge mutation; close remains authorized cleanup. Direct projection publication separately Partial, no new owner/closure producer. Gates OFF, tested managed guard RuntimeEnforced; physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p04-telemetry-sample-read-permission.

### Direct telemetry result admission — 2026-10-02

Existing CapabilityAuthority trusted commit now admits direct inspection projection result only after clock/observation collection and exact process/source revalidation. Existing ProcessRegistry and kernel lifecycle consumer select exact requester/subject generations. No observation or snapshot identity confers permission. Result admission linearizes under existing capability gate; later revoke is ordered after an already admitted result. Supervisor ServiceSupervisor control capability has a separate actual consumer and remains Partial. Gates OFF, managed guard RuntimeEnforced only; physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p04-telemetry-projection-permission.

### Supervisor telemetry control result — 2026-10-02

Existing CapabilityAuthority validates exact Configure|Execute KernelService/ServiceSupervisor principal source at final result admission after observation clock. Existing ProcessRegistry/lifecycle consumer resolves principal/subject process generations. Existing CapabilityAwareServiceSupervisor remains the instance/metrics owner; its snapshot does not grant permission or prove atomic physical state. Final admission acquires no supervisor gate and invokes no callbacks. Other supervisor mutation/instance coherence remains Partial; no duplicate authority. Gates OFF, named managed guard RuntimeEnforced, physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p04-supervisor-telemetry-permission.

### Supervisor local registration — 2026-10-02

Existing CapabilityAuthority spans exact control-source permission and Register trusted local mutation under its existing revocation gate. Existing supervisor record/graph owns local registration; supervisor -> capability order, no clock/factory/provider/lifecycle/trace call in mutation. Existing process owner fresh principal resolution precedes owner commit; arbitrary low-level process mutation remains independently Partial. No second source authority/ledger, no observation authorization. Gates OFF, bounded managed guard RuntimeEnforced; physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p04-supervisor-registration-permission.

### Telemetry failed observation — 2026-10-02

Existing subscription record owns persistent ObservationFailed diagnostic; existing batch Complete derives from this fact and Dropped, never supplies permission/closure. Configured clock remains an observation source; nonfatal failure returns PlatformFaulted before capture allocation with no synthetic observation. Existing capability owner guards read/publication, existing budget owner/actual close consumers alone decide refund. Failure/cancel/timeout keeps state/charge unchanged; marker cannot reopen closed record. Gates OFF, named managed guard RuntimeEnforced; physical/fatal/cross-owner clock contours independently FutureGated/Partial. Evidence: artifacts/v6/iteration-20261002-c0-p10-telemetry-clock-failure.

### Operation clock policy input — 2026-10-02

Existing CapabilityAuthority remains source/constraint/quota/one-shot/lease owner. Configured operation GetUtcNow runs outside its gate and never grants permission; exact source constraints are checked freshly at final owner commit. Actual session pin/process owners revalidate after clock via existing consumer, outside capability gate before consumption; existing cleanup remains authoritative. V2 resolves realm/nonce before releasing gate, no blanket V2 migration. Logical lifetime observation is not physical deadline or temporal authority. Resource-use clock contour separately Partial. Gates OFF, named managed guard RuntimeEnforced; physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p04-operation-clock-admission.

### Resource-use clock policy input — 2026-10-02

Existing CapabilityAuthority remains resource grant/source/lineage/generation/lease owner. Clock observation is collected outside gate and does not confer permission; exact authoritative source and canonical subset checked freshly at final query/lease publication. Actual process/Prepared-operation consumer revalidates after clock, outside capability gate, then source is checked again. Existing budget owner/actual pre-submit consumer decides only its provisional charge cancellation; errors never prove provider closure or change another owner's operation state. Gates OFF, named managed guard RuntimeEnforced; physical FutureGated. Evidence: artifacts/v6/iteration-20261002-p04-resource-clock-admission.
