# P10 — RAS and Failure-Domain Semantics

## Structured verdict

**PHASE:** P10 — RAS and Failure-Domain Semantics  
**BASELINE:** External operation quarantine/settlement and Region ownership exist; granular provider health/subrange damage semantics are not fully unified.  
**VERDICT:** Extend consequences in existing owners. Provider health is evidence; RegionAuthority owns damaged memory usability; ExternalOperationAuthority owns in-flight ambiguity.

### VERIFIED_EXISTING
- RegionAuthority can represent subranges/ownership/use concepts from prior architecture.
- External operation lifecycle already supports quarantine/reconcile-style closure.
- Provider adapters own device/fabric health observations.

### PARTIAL
- Granular states such as poisoned range/partial unavailability/reconfiguration are not uniformly executable across providers.

### GAPS
- ProviderHealthEvidenceV1 with generation/failure-domain scope.
- Region subrange damage/quarantine transitions.
- In-flight operation consequence rules.
- Remap/reconfigure generation increments.

### CONTRADICTIONS
- Health telemetry must not directly mutate CapabilityAuthority.
- Provider loss does not prove closure/reclaim safety.

### REMOVE_OR_MERGE
- No RasAuthority or second Region ledger.

### NEW_REQUIRED
- FailureConsequenceV1 semantic sidecar.
- Subrange health/damage state in RegionAuthority where it is an ownership/use consequence.
- Explicit quarantine closure protocol.

### AUTHORITY IMPACT
Provider owns health evidence; RegionAuthority owns memory usability consequences; ExternalOperationAuthority owns in-flight effect state; device/fabric owner owns reset/rebind.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT` for health/poison/reset evidence only.

### COMPILER IMPACT
None required.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Health evidence laundering into ownership mutation.
- Unsafe reclaim after ambiguous writer/provider loss.
- ABA after fabric remap/reset.

### CORRECTNESS RISKS
- Partial Region damage incorrectly closes whole Region or misses damaged subrange.
- Poison/remap races with publication.

### PERFORMANCE RISKS
- Quarantine can pin resources indefinitely; require bounded operational/reconciliation policy without weakening safety.

### REQUIRED TESTS
- ECC/poison subrange fault injection.
- Provider reset/path loss in every external-operation state.
- Partial Region loss/remap tests.
- Ambiguous write -> quarantine.
- Reconfiguration generation ABA tests.
- CXL memory degradation model tests.

### FORMAL WORK
- TLA+ provider-loss/in-flight/reclaim model.
- Alloy/finite model for subrange damage ownership.

### DEPENDENCIES
P01 hard; P04 for device/DMA consequences; P05 failure semantics. P02 only for persistent contours. P11 hard-depends on P10.

### EXIT CRITERIA
- Subrange damage is expressible without a new ownership ledger.
- No reclaim occurs while a possible writer is unclosed.
- Reconfiguration increments exact generations and stale bindings fail closed.

### ROADMAP PATCH
Center P10 on consequence ownership and closure, not a new RAS authority hierarchy.

## Objective

Extend consequences in existing owners. Provider health is evidence; RegionAuthority owns damaged memory usability; ExternalOperationAuthority owns in-flight ambiguity.

## Live baseline and existing mechanisms
- RegionAuthority.
- ExternalOperationAuthority.
- Provider/fabric/device owners.

## Required implementation changes
- Add health evidence schema.
- Add Region subrange consequence transitions.
- Add quarantine/reconcile state machine.
- Add fault injection adapters.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-RAS-PARTIAL-FAILURE`  
**Claim ceiling at emulator/runtime freeze:** `ModelOnly/ExecutableAdapter on emulator fault model; hardware RAS requires named platform evidence`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P10-A failure vocabulary.
- P10-B Region consequences.
- P10-C external-operation quarantine.
- P10-D provider fault injection.
- P10-E hardware qualification later.

## Rollback

Disable granular RAS gate; use existing coarse failure/quarantine behavior. Never assume provider loss means safe reclaim.

## Current unresolved-effect consumers — 2026-10-01

Invocation post-close ambiguity remains owned by EndpointSessionInvocationRegistry's
FailedSettlementMayHaveEffect; CloseSession/CloseResourceDonation preserve the pin.
Lost mapping receipts remain owned by DomainRecord.MappingMayHaveEffect and existing
Region/budget reservation owners. Lost IRQ receipts/uncertain delivery remain owned by
IrqBindingRecord and DeviceLeaseRecord.UnresolvedIrqCapabilityId. Root binding ambiguity
remains owned by _unresolvedDomainBinds. Exact provider reconciliation receipt producers
and consumers that can clear these ambiguities are Missing, independently per contour.

Known draining IRQ/MMIO closure uses existing RevokeCapability and exact provider revoke
consumers; NotAccepted retry does not clear uncertainty. Fault/reset/exception and terminal
response do not prove no-effect. Quarantine/pins retained; gates OFF, physical FutureGated.
Current tuple and consumer locations: `artifacts/v6/iteration-20261001-irq-delivery-permission`
and `artifacts/v6/iteration-20261001-mmio-closure-interlock`. ISA/opcode/CPU impact NONE.

## Type-3 health callback placement revalidation — 2026-10-01

Existing CxlType3MemoryAuthority now rejects changed admitted placement snapshots after initial backing validation, health callback and final backing validation. Reentrant Close cannot resurrect Released as MigrationRequired or apply stale Region damage. Exact backing generation remains independently revalidated. Named managed callback contour tested; full concurrent placement lifecycle synchronization remains Partial, physical RAS FutureGated. No new owner or closure API; gates OFF; ISA/opcode/CPU impact NONE.

## Placement commit/closure interlock — 2026-10-01

Existing Type-3 placement owner now synchronizes dictionary/snapshot commits. Health consequence commit holds owner gate through callback-free RegionAuthority mutation; Close commits Draining first and admits one closure attempt, provider callbacks run outside the gate, finally balances pending state and preserves quarantine on failure. Refresh and secure backing consumer reject changed snapshots. Managed overlap/fault tests qualify named owner interlock only. Independent mapping-admission/backing-close composition, provider-global concurrency and physical RAS remain Partial/FutureGated. A successful memory closure followed by failed fabric closure needs explicit retained exact step evidence for retry; absence of provider record is not closure. Next slice:retain that actual successful closure result in existing placement owner. Gates OFF; V1 compatible; ISA/opcode/CPU NONE.

## Exact successful closure step retention — 2026-10-02

Existing placement record now retains the exact binding returned successfully through ReleaseMemory/ReleaseFabric, solely for its existing Close retry. Retry continues unfinished closure steps and releases Region backing only after both real steps succeed. Direct and process teardown retries are tested after fabric failure/exception. Lost successful memory/fabric reply cannot populate the step receipt; not-found on retry preserves quarantine/backing pin. No new closure API/owner or permission from receipt; physical closure source remains model-only/FutureGated. Mapping-admission/backing-close composition remains Partial. Gates OFF; V1 unchanged; ISA/opcode/CPU NONE.

## CXL reset/hot-remove closure consequence — 2026-10-02

Hot-remove/reset changes the endpoint source tuple; a fresh binding from the new tuple is not closure of the admitted old tuple. Existing CXL closure consumers refuse rebound source substitution and retain receipt, backing and parent-device reservations when continuity is lost before/during closure. Prior model expectation allowing Close/reclaim after hot-remove contradicted this boundary and is corrected: migration indication does not prove containment. A healthy independent endpoint can still close/reclaim. Malformed child receipt/failed compensation retains the existing unknown-effect pin on the original process/owner; fabric/device closure cannot bypass it through a mismatched receipt association. Exact reconciliation evidence/consumer is Missing; physical qualification FutureGated; gates OFF; ISA NONE.

## Type-2 operation parent closure — 2026-10-02

The actual CxlType2AcceleratorService now acquires an opaque operation pin from the existing fabric owner before provider submission and retains it through callback, late receipt tracking and exact provider closure. The existing original device CapabilityAuthority/process/domain admission is freshly checked; fabric metadata is not permission. Fabric/device release and process teardown cannot bypass a pending or unresolved operation. Cancel requests cancellation; only separate exact Release success with endpoint/fabric source continuity records closure. Release exception, lost reply, stale/malformed receipt and reset retain quarantine, even if the provider has physically removed its model record. Cached successful closure supports existing local settlement retry without inferring closure from not-found. PendingAdmissions closes manager reconfiguration during submit; selected manager query/submit/drain callbacks run outside its owner gate.

Named managed submit/parent/closure contour RuntimeEnforced; provider evidence ModelOnly. Full operation overlap/publication serialization, manager CompleteReconfiguration/pool callbacks and physical source incarnation remain Partial/FutureGated. Unknown receipt reconciliation Missing. Public V1 signatures/enums/packages unchanged; comments clarify cancel/release semantics. Gates OFF; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-type2-parent-closure.

## Fabric drain settlement refusal — 2026-10-02

Existing CxlFabricManagerAuthority now propagates exact operation Query failure and pre-submit Cancel/Release errors. Missing or stale principal/operation lookup is not closure evidence; manager admission remains faulted/closed and provider reconfiguration is not invoked. Pre-submit Release passes ProviderResourcesClosed=false so the authoritative operation owner independently rejects a concurrent submitted state; the earlier Query snapshot grants no closure claim. Positive Prepared/Admitted drains remain compatible. Named managed refusal RuntimeEnforced; exact post-exit terminal receipt reconciliation remains Missing, broader manager callbacks Partial, physical FutureGated. Gates OFF; V1 unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-fabric-drain-settlement.

## Fabric completion callback interlock — 2026-10-02

Existing CxlFabricManagerAuthority now admits one exact completion attempt under its existing gate, captures record/ticket/previous binding, then invokes CompleteReconfiguration and fresh QueryResource outside that gate. CompletionInFlight is only an interlock; it grants no authority and is balanced in finally. Reentrant/overlapping attempts are refused without a second provider callback. Commit requires the same record, Draining state, exact ticket/previous binding, replacement generation and endpoint/device generation, plus provider observation matching the returned binding and Bound state. Exception, malformed receipt and changed source observation fault local admission and retain the existing ticket/operation correlations; terminal response is no closure proof. Existing fresh admission remains independently necessary after replacement.

Named managed completion interlock/source rejection RuntimeEnforced; tests use ModelOnly provider. Physical source continuity/atomicity and deployment remain FutureGated; no machine legality claim. Full manager pool lifecycle, Register concurrency and unknown reconfiguration reconciliation remain Partial/Missing. Gates OFF; V1 API/enums/packages unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-fabric-completion-interlock.

## Attached operation budget retention — 2026-10-02

Existing public ReleaseExternalOperation and process teardown reconciliation retain the exact operation/process/reservation generation association when ResourceBudgetAuthority refuses release. Successful local operation release remains distinct from quantitative settlement; Quarantined/Consuming accounting is not refunded from terminal operation state. The association gate only synchronizes admission, snapshot and exact conditional removal; budget/operation owners execute outside it. Removal follows successful exact budget release and rechecks the captured tuple. Existing repeated release and teardown remain the consumers; no new closure API or authority. Public and virtual admission, stale operation, repeated refusal and successful non-double refund are tested. Unknown accounting clearing remains Missing; concurrent admission versus teardown composition remains Partial. Gates OFF; managed guard claim ceiling RuntimeEnforced; physical FutureGated; V1/API/package unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-operation-budget-retention.

## Region accounting generation retention — 2026-10-02

Existing Region budget routing now keys the exact RegionHandle, not RegionId. Successful local ReleaseRegion, transfer and domain reclaim do not erase a reservation whose exact ResourceBudgetAuthority release was refused. Transfer retains the source-generation association and independently installs the target-generation association; a target refund cannot clear the prior source charge. The narrow association gate synchronizes dictionary access and conditional removal only; no owner/provider callback occurs under it. RegionAuthority still owns local lifecycle; budget owner still owns quantitative state. Public buffer/scalar callers and existing process cleanup are actual consumers. New tests cover Consuming/Quarantined/Reconciled refusal, exact stale process/ownership tokens, source/target generations, failed-transfer provisional refund, overlapping release and fresh charge isolation. Clearing unknown accounting and post-exit retry remain Missing; domain cohort budget cleanup and complete admission/teardown overlap remain Partial. All gates OFF; managed retention RuntimeEnforced; physical FutureGated. V1/public API/packages unchanged; ISA/opcode/CPU NONE; Java excluded. Evidence: artifacts/v6/iteration-20261002-p10-region-budget-retention.

## Exact domain cohort accounting cleanup — 2026-10-02

Actual FinalizeProcessCleanup now passes the exact RegionAuthority.ReclaimAllForDomain result to the existing budget cleanup helper. Reclaimed RegionHandle selection also reaches reservations of earlier retired cohort members using their captured exact ProcessHandle; current ProcessId resolution is not substituted. The existing final-process retry remains. Non-final exit does not refund still-owned domain Region charges. ResourceBudgetAuthority independently refuses Consuming/Quarantined/Reconciled accounting, preserving association/quota even after local Region reclaim. Exact newly reclaimed generation cannot clear a quarantined prior transfer generation; other domains and a reused PID/new process generation remain independent. Buffer/scalar and both exit orders are tested. Local reclaim results are routing inputs, not provider closure or quantitative settlement evidence. Named managed cohort guard closed, RuntimeEnforced; full concurrent cohort lifecycle and unknown/post-exit accounting clearing remain Partial/Missing. All gates OFF, physical FutureGated, V1/public API/package/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-domain-cohort-budget.

## IPC quantitative association retention — 2026-10-02

Existing receive and process teardown now preserve exact channel/sequence/process/reservation associations when ResourceBudgetAuthority refuses release. Actual ChannelRegistry.ChannelClosed notification, already consumed by response correlation cleanup and emitted after local queue clear, also routes queued budget release through the same helper. This notification is not provider effect closure or accounting settlement; quantitative owner release must independently succeed. Existing request-response correlation gate protects routing lookup/snapshot/conditional removal, and the captured full association is checked before removal. Public receive, sender/receiver teardown and actual local channel close are tested with normal, Consuming, Quarantined and Reconciled reservations, separate channels sharing sequence values, reused PID/new generation and sampled receive/teardown overlap. Unknown accounting clearing remains Missing; post-exit retry and complete deterministic overlap remain Partial/Missing. ChannelId/sequence exhaustion guards are currently Missing; no full ABA qualification is asserted. All gates OFF, tested managed retention ceiling RuntimeEnforced, physical FutureGated. V1/public API/enum/package/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-ipc-budget-retention. Next dependency-ready slice: existing channel owner identity/sequence exhaustion without partial ownership/protocol mutation.

### IPC counter gap follow-up — 2026-10-02

The channel identity/sequence exhaustion gap recorded above is now closed for actual V1 queued and inline owner admission contours under existing caller gates. Exhaustion returns CapacityExhausted before ownership/protocol effects; sequence cannot wrap and allocator terminal value is reserved. This does not qualify complete standalone owner concurrency or physical effects. Unknown accounting clearing remains independently Missing. Gates OFF; managed RuntimeEnforced; ISA NONE. Exact tuple: artifacts/v6/iteration-20261002-c0-channel-counter-exhaustion. IPC ownership transfer composition with OwnedMemoryBytes source/target budget routing still needs a separate actual-consumer audit.

## IPC ownership memory-budget composition — 2026-10-02

Actual queued MOVE, inline MOVE, pair Borrow+Consume and queued/inline ownership response paths now use a TCB-only existing kernel budget composition route. Exact live process records/new-effect acceptance and Region owner generation are independently checked. Target OwnedMemoryBytes reservation occurs before Region transfer and before pair loan preparation. Existing RegionAuthority owns transfer/loan; existing ResourceBudgetAuthority owns quantitative reservation/release/quarantine. Source/target charge routing follows old/new RegionHandle generations; actual payer comes from existing association because V1 domain/generation peers may differ from the quantitative payer. A refused source accounting release preserves the old charge while new target charge remains separate. Legacy wholly unbudgeted callers remain compatible; budgeted ownership cannot silently downgrade into an unbudgeted target. Borrow/reference contours do not acquire a target ownership charge.

Normal owner refusal compensates the provisional target reservation; pair owner refusal retains/revokes its exact local loan through existing Region consumer. Local preparation/transfer exception preserves target budget quarantine instead of inventing no-effect/refund; exact ambiguous provisional clearing consumer remains Missing. Full payload-factory exception settlement, standalone owner concurrency and response/teardown overlap remain Partial. Managed caller gates are retained; the pair callback is only actual TCB local loan preparation, not a provider callback or admission permission. Tests cover five real queued/inline request/response contours, positive/negative quota/not-configured admission, source quarantine, cohort payer distinction, owner refusal, exception pin and sampled competing moves. Gates OFF; managed admission/accounting ceiling RuntimeEnforced; physical FutureGated; no minimum service/deadline/WCET claim. Public V1 APIs/enums/packages/schema unchanged; internal owner constructors accept the composition route. Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-ipc-memory-budget-composition.

Final process incarnation and new-effect acceptance are revalidated after pair preparation, before Region transfer; reentrant source/target exit tests deny transfer and refund only the provisional target reservation. Broad verification first exposed four real GUI/compute/diagnostic fixtures without ownership target budgets. Their intended round trips now explicitly admit memory budgets through existing manifest/process consumers; the guard was retained. Failed TRX inputs/results remain in evidence. No quota is implicitly granted by service discovery or component identity.

## Runtime-attached budget release provenance — 2026-10-02

Existing ResourceBudgetAuthority records whether a reservation was admitted through the actual ReserveIfAttached runtime consumer. Public ReleaseBudget now uses the owner's explicit-reservation release path; it cannot release active/bound runtime-attached capacity merely from an observed exact handle. Existing local resource release and admission rollback still call the TCB owner release consumer. Provenance is immutable in the existing quantitative record, under the same budget gate, never authorization or a second ledger. Explicit V1 reservations remain releasable independently of lifetime; Released remains idempotent, stale/wrong owner cannot refund, and consuming/quarantined/reconciled settlement refusal is preserved. Buffer/scalar Region, real IPC receive and trace stop consumers, active/bound states and sampled public/resource release overlap are tested. Complete platform/telemetry callback concurrency remains Partial. Checkpoint-storage and explicit temporal-composition release provenance are separate Partial contours; no global closure/quota guarantee. Gates OFF; named managed release guard RuntimeEnforced; physical FutureGated; V1/API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-attached-budget-release.

## Checkpoint storage release provenance — 2026-10-02

ReserveCheckpointStorage now atomically records runtime-attached provenance in the existing ResourceBudgetAuthority record. Public generic release cannot refund live ordinary checkpoint images or bound stateful captured storage. Real ordinary delete/failure rollback and stateful resume/discard/provider reconciliation retain their existing TCB quantitative consumer. Ordinary source process retirement does not erase persistent local checkpoint accounting; the actual delete consumer uses the captured exact old ProcessHandle. Tests cover source live/retired delete, stale reservation and wrong process generation, stateful resume/discard and discard-loss quarantine followed by the existing modeled reconciliation consumer. This is managed storage/accounting evidence, not physical persistence/durability/provider containment. Explicit caller reservations including CheckpointImage lifetime remain compatible. Generic checkpoint-storage release gap closed for this contour; temporal-composition release remains Partial. Gates OFF; managed guard ceiling RuntimeEnforced; physical FutureGated; public V1/API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-checkpoint-budget-release.

## Temporal composition generic release guard — 2026-10-02

Existing ResourceBudgetAuthority temporal claim now refuses generic Release/ReleaseExplicit before quantitative mutation, for provisional and committed composition. Snapshot/handle observation cannot detach the charged bound lease while the actual managed temporal coordinator retains capacity. Exact unpublished abandonment and actual coordinator cancel/settlement remain the consumers; failed model capacity reserve abandons its exact claim before explicit release becomes available. Released/CancelledPreSubmit idempotence and unclaimed explicit V1 release remain compatible. New tests cover actual bound external-operation and unbound coordinator consumers, provisional exact/wrong abandonment, capacity-admission refusal, wrong process/stale reservation and sampled public release versus actual admission. Managed release guard RuntimeEnforced; managed provider capacity remains ModelOnly, physical minimum service/deadline/WCET FutureGated. Generic pre-submit cancellation is an independent Partial contour: ResourceAdmissionProtocol/ResourceDonationProtocol still share CancelLeasePreSubmit with temporal coordinator and require exact composition-consumer audit. No stronger global quota/closure claim. Gates OFF; public V1/API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-temporal-budget-release.

## Temporal pre-submit cancellation consumer — 2026-10-02

Existing ResourceBudgetAuthority CancelLeasePreSubmit now checks the same exact committed temporal consumer used by consumption and settlement, before idempotence or ledger mutation. Real temporal coordinator cancellation and submit compensation pass their existing binding.Id; generic ResourceAdmissionProtocol/ResourceDonationProtocol callers preserve default unclaimed V1 behavior and cannot cancel a claimed temporal lease. Provisional claims deny even their exact ID until commit or exact abandonment. Missing/foreign consumer remains denied after cancellation; the exact committed consumer keeps idempotence. Real coordinator closes exact unused model capacity before refund. Existing fault-injection test that directly cancels admitted temporal accounting now explicitly selects its exact committed consumer; it does not become physical closure evidence. New tests cover bound/unbound coordinator generic/foreign refusal, provisional claim/abandonment and sampled foreign cancellation versus actual coordinator. The previously recorded shared cancellation consumer gap is closed for this managed temporal contour; full independent resource admission composition arbitration, trace/telemetry quantitative retention and physical closure remain separate Partial/FutureGated. All gates OFF; guard RuntimeEnforced, capacity provider ModelOnly; no minimum service/deadline/WCET or production claim; public V1/API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-temporal-budget-cancellation.

## Trace/telemetry quantitative retention — 2026-10-02

Actual trace stop/teardown now retain exact session/process/reservation routing until ResourceBudgetAuthority release succeeds. A narrow association gate protects lookup/add/snapshot/conditional removal only; owner calls run outside it. Already stopped sessions with refused accounting also reach the existing teardown consumer. Local trace Stopped success remains independent from quantitative settlement. Telemetry existing immutable BudgetReservation in its subscription record, repeated close and owner/subject teardown release already preserve refused accounting: VerifiedExisting, no telemetry source change. Tests cover Consuming/Quarantined/Reconciled across trace stop/teardown/stopped-then-teardown and telemetry close/teardown; normal single refund, sampled overlapping stops, fresh session isolation, stale session generation and wrong owner. Unknown clearing/post-exit retry remain Missing; whole admission versus teardown interlock remains Partial and requires a separate source/consumer slice. All gates OFF, managed retention guard RuntimeEnforced, physical FutureGated; no telemetry/trace permission or closure proof. V1/public API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p10-trace-telemetry-budget-retention.

## Trace/telemetry admission lifecycle interlock — 2026-10-02

Actual StartTraceSession and StartTelemetrySubscription now share the existing platform memory-use/lifecycle gate with kernel process teardown through validation, budget reserve and record publication. Telemetry owner and subject independently require exact live ProcessHandle and new-effect acceptance. Budget trace records can invoke configured TimeProvider; reentrant teardown is therefore followed by fresh process checks and telemetry projection validation before publication. Refusal compensates only the provisional budget through its existing owner. Tests cover live/stale process admission, sampled concurrent admission/teardown, and deterministic reentrant clock owner/subject exit for trace, self telemetry and cross-service telemetry. No late active record survives the tested kernel teardown paths. Standalone low-level owner mutation, inspection-capability operation interlock, clock exception semantics and post-exit unknown accounting clearing remain separate Partial/Missing contours. The new lifecycle gate does not grant authorization, reservation/service guarantees or physical closure. All gates OFF, managed caller admission ceiling RuntimeEnforced, physical FutureGated; public V1/API/enums/packages/schema unchanged, Java excluded, ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p04-trace-telemetry-admission-lifecycle.

## Trace clock failure/publication boundaries — 2026-10-02

DeterministicTraceAuthority calls the configured clock outside owner/producer locks, then revalidates local session state under the existing producer gate before enqueue. Stop and process-wide stop commit under the same gate with authority-to-producer lock order; no clock callback runs under either. Nonfatal clock exceptions return PlatformFaulted without sequence/buffer/drop counter mutation and mark incomplete observation in the existing producer record; diagnostic replay consumes existing Complete=false. Best-effort kernel budget trace therefore cannot throw an observational clock exception after reserve and orphan provisional resource accounting. Producer sequence exhaustion and dropped-counter exhaustion refuse before mutation; maximum sequence commits once and cannot wrap, failed observation cannot become complete evidence. Existing backpressure/stop/drop policy remains, no synthetic timestamp or event. Tests cover clock failure with available/full Drop buffer, reentrant stop/exit, blocked clock versus snapshot/stop, real trace/telemetry admission budget consumers and counter boundaries. Nonfatal managed guard RuntimeEnforced; physical closure/time FutureGated, trace never grants permission. OOM/fatal failures, inspection capability operation interlock and low-level cross-owner concurrency remain separate Partial contours. All gates OFF; V1/API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-c0-p10-trace-clock-publication.

## 2026-10-02 C0/P10 telemetry snapshot clock failure/incomplete consumer

Closed bounded managed nonfatal snapshot-clock failure slice. BuildTelemetry catches nonfatal configured GetTimestamp exceptions (including cancellation/timeout) and returns existing PlatformFaulted before capture sequence allocation, no synthetic timestamp/snapshot. Fatal OOM/stack overflow remain uncaught/Partial. Existing subscription record now owns ObservationFailed, set for exact-handle clock/capture-allocation failure; no queue/Dropped/state/charge mutation. Existing TelemetrySubscriptionBatch Complete is false when Dropped is nonzero or ObservationFailed. Marker persists through drain/recovery/close; fresh subscription has fresh local fact. No new authority/ledger/API/enum/package/schema.

Requirements C0-TEL-CLOCK-001..004: nonfatal error before capture mutation, exact failed-observation incomplete consumer, queue/state/charge retention, no cancel/timeout/failure as closure/refund. Eight new cases cover clock failure with full buffers across three policies, empty capture exhaustion, direct/supervisor error without snapshot/ID, cancel/timeout active-charge retention, blocked clock failure versus actual close. Build 0 errors/9 warnings 20.51s before three final fault/race cases; initial focused 102, final focused 105 passed/0 failed. Final broad/current tuple/hashes: artifacts/v6/iteration-20261002-c0-p10-telemetry-clock-failure/audit.json.

Failure marker is diagnostic only and requires no execution permission; buffered read still uses actual fresh capability/source guard. Local close and quantitative refund are existing separate owners/consumers, not inferred from observational failure. Physical coherent snapshot/clock/containment FutureGated, fatal compensation/full injected cross-owner concurrency Partial, controlled Start source consumer Missing. All gates OFF, named managed guard RuntimeEnforced only. Java excluded/skipped; ISA/opcode/CPU impact NONE. Next actual P04 slice: operation capability GetUtcNow callback staging outside capability gate with fresh source/consumer revalidation.
