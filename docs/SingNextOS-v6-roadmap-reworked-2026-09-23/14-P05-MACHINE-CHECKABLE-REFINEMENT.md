# P05 — Machine-Checkable Cross-Layer Refinement

## Pre-submit local release observation — 2026-10-02

ADR-006 adds CancelledBeforeSubmit=13 and LocalAuthorityReleasedBeforeSubmit=14 because actual kernel cancellation/release and process teardown commit these facts without Submit. Their checked owner histories no longer disappear as empty prefixes. The separate branch accepts repeated committed cancellation and exact local release, rejects all post-submit effect/settlement/closure/drift events and every event after local release. Existing Submit-starting traces and enum values/DTO format remain unchanged. Projection verifies contiguous writer history, Cancelled disposition, NotCrossed boundary and absent submission binding; no budget settlement or provider/mapping closure is inferred. Actual kernel stale-generation refusal, no-mutation and idempotent release/retry plus existing Prepared/Admitted teardown consumers are tested. Normal live registration remains post-RecordSubmission, so pre-submit live instrumentation is Partial and not claimed. Gates OFF; named managed projection/validation guard RuntimeEnforced, physical FutureGated; unsupported mandatory events require fail-closed consumers, no executable old-binary compatibility claim. Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-pre-submit-release.

## Non-resource local release observation — 2026-10-02

"Without resource binding" specifically means absent ExternalOperationResourceBinding/ResourceLeaseBound in this owner's complete history. Independent temporal associations may still exist: the actual temporal coordinator test preserves Quarantined budget after operation local release and reconciles it only through its own consumer; its settlement does not manufacture an external ResourceSettled transition or alter this local trace. Observed ResourceAccountingQuarantined blocks kind15; absence of that observation does not prove absence of other owners' accounting ambiguity.

ADR-007 adds LocalAuthorityReleasedWithoutResourceBinding=15 for actual post-submit owner release without a ResourceLeaseBound association. Generic Released retains its ordered budget-settlement/closure rules; no absent Settled or physical closure is fabricated. Projector now checks the existing Released writer state/disposition/policy and complete guarded history. Checker permits the distinct terminal local observation only from Published, Quarantined or EffectClosed, refuses prior accounting quarantine or any generation drift, and rejects all later events. Earlier effect/quarantine observations remain in the trace; terminal local stream is not provider containment. Actual HybridCpuExternalOperationProvider Release tests cover published, faulted, cancelled completion and provider loss, no-mutation refusal without caller closure decision, idempotent retry and forged active-completion release rejection. Type-2's exact provider Release/source continuity and parent pin consumer remains separate and unchanged. Caller-supplied closure bool does not qualify physical closure. All gates OFF; named managed observation RuntimeEnforced, physical FutureGated; older mandatory-kind consumers must fail closed, executable old-binary compatibility not claimed. Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-non-resource-release.

## Pre-submit failure live observation — 2026-10-02

Existing submit/close interlock now invokes an additive optional internal observation callback only for the winning pre-submit failure, after authoritative compensation and lease disposal. Successful submit registration remains after RecordSubmission; an overlapping loser cannot register another caller's sink or refund its budget. Memory/temporal wrappers rebuild the captured sidecar against its declared provider generation and exact commit/base/obligations/process/budget tuple, then require actual Cancelled/NotCrossed/no-binding/no-Submitted owner history before registering the existing sink. This is source attribution, not fresh admission or a provider generation observation. Unknown/foreign captured semantics produce no trace. Cancellation registration stays until actual local Released; existing delivery interlock drains reentrant teardown/release without invoking sink under owner/trace locks. Sink rejection/exception cannot change compensation, release or reclaim. Actual failed memory/temporal, generation drift/runtime loss, duplicate winner, sink fault and reentrant process teardown consumers are tested. Compensation quarantine histories unsupported by the current alphabet, disposal-only failures and physical cross-provider observation remain Partial/FutureGated; no missing closure is inferred. All gates OFF; named managed live observation RuntimeEnforced. Public V1 enum/API/package/schema unchanged in this slice; optional internal callback added compatibly. Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-pre-submit-live.

## Pre-submit accounting quarantine observation — 2026-10-02

ADR-008 adds ResourceAccountingQuarantinedBeforeSubmit=16 because existing compensation can commit ResourceQuarantined before Submit and even after pre-submit local Released. Projector requires preceding ResourceLeaseBound and absence of submitted/cancelled-resource/settled history; it emits the actual writer digest without fabricating Submit or generic effect Quarantined. Kind16 preserves Initial/PreSubmitCancelled/PreSubmitReleased state, including the narrow late-accounting exception after local release. All other effects after local release remain forbidden; kind16 cannot substitute after Submit and counts as accounting ambiguity against no-resource release laundering. An actual conservative Submit/EffectPossible prefix after prior resource quarantine remains observable, not authorized by the trace. Real failed compensation/live observer tests cover quarantine before/after cancellation and after local release, retry refusal/no mutation, reservation/resource binding retained through process teardown, and phase substitution refusal. Local process/Region reclaim is independent of quantitative budget quarantine; it does not settle/release the reservation. Pre-submit clearing consumer/exact accounting evidence remains Missing and reservation quarantine stays retained. Gates OFF; named managed observation RuntimeEnforced, physical FutureGated; older mandatory-kind consumers must fail closed, executable old-binary compatibility unqualified. Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-pre-submit-accounting-quarantine.

## Structured verdict

**PHASE:** P05 — Machine-Checkable Cross-Layer Refinement  
**BASELINE:** V1 semantic refinement exists in live runtime; current relation is narrower than v6 dimensions and lacks a common trace algebra.  
**VERDICT:** Make P05 a cross-cutting semantic spine. Build a small executable partial-order/trace checker first; no theorem prover and no "full formal verification" claim.

### VERIFIED_EXISTING
- `SemanticExecutionRefinementV1.Evaluate`-style live refinement exists.
- HybridCPU runtime legality is explicitly independent and returns LegalityDecision through IRuntimeLegalityService.
- ExternalRuntime provides versioned generation/admission/publication contracts.

### PARTIAL
- Existing refinement is executable but not yet expressive for all memory/DMA/time/failure dimensions.
- Current evidence is local to selected contours rather than a single cross-project trace schema.

### GAPS
- Dimension-specific partial orders.
- Mandatory/optional clause semantics.
- Numeric comparison rules with units and overflow/canonicalization.
- Trace projection from provider/HybridCPU events to SingNext semantic events.
- Counterexample recording and differential trace harness.

### CONTRADICTIONS
- A formal model must not become a runtime authority or GlobalState owner.
- Passing model checking cannot promote a hardware/production claim.

### REMOVE_OR_MERGE
- Remove any goal of proving all P01–P12 at once.
- Do not require theorem prover integration for v6 entry.

### NEW_REQUIRED
- SemanticTraceEventV1 schema.
- Dimension-specific refinement evaluators.
- Executable trace checker and counterexample format.
- Qualification artifact binding model version to source tuple.

### AUTHORITY IMPACT
None. Formal model is specification/evidence only. Live owners remain authoritative.

### HYBRIDCPU IMPACT
`RUNTIME_ONLY` instrumentation for trace events plus existing legality outputs; no legality ownership change.

### COMPILER IMPACT
Optional: emit evidence tags used by the checker. No compiler authority.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Evidence laundering: treating refinement proof as permission.
- Schema mismatch/unknown mandatory dimension silently accepted.

### CORRECTNESS RISKS
- Wrong partial-order direction.
- Numeric unit mismatch or overflow.
- Projection erases an irreversible effect or generation change.

### PERFORMANCE RISKS
- Trace instrumentation overhead; support sampled/offline modes after semantic equivalence is proven.

### REQUIRED TESTS
- Refinement truth-table tests per dimension.
- Unknown mandatory/optional tests.
- Mutation tests that weaken one guarantee and must fail.
- Differential SIP vs SipJob/HybridCPU traces.
- Counterexample reproducibility tests.
- Trace canonicalization tests.

### FORMAL WORK
- TLA+/PlusCal for cross-owner transitions.
- Finite exploration for configuration/refinement.
- Property tests for partial-order laws: reflexivity/transitivity/antisymmetry where applicable.

### DEPENDENCIES
Hard core starts after C0 and P01 vocabulary; each later phase adds a dimension. First qualification vertical requires minimal P01/P04 refinement, not all phases.

### EXIT CRITERIA
- Minimal mathematical core documented and executable.
- First vertical produces identical allowed semantic projection on reference and heterogeneous path.
- Counterexamples are actionable and tuple-bound.
- No claim exceeds model/trace evidence.

### ROADMAP PATCH
Recast P05 from a late phase to a continuously extended refinement framework with per-contour closure.

## Objective

Make P05 a cross-cutting semantic spine. Build a small executable partial-order/trace checker first; no theorem prover and no "full formal verification" claim.

## Live baseline and existing mechanisms
- V1 runtime refinement evaluator.
- HybridCPU LegalityDecision/IRuntimeLegalityService.
- ExternalRuntime generation/admission/publication artifacts.

## Required implementation changes
- Add trace schema and projection.
- Add memory/DMA refinement dimensions first.
- Add temporal/failure/durability dimensions only when their contracts stabilize.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-FORMAL-REFINEMENT`  
**Claim ceiling at emulator/runtime freeze:** `RuntimeEnforced for refinement check itself; ModelOnly for unimplemented semantic dimensions`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P05-A math/core definitions.
- P05-B executable evaluator library.
- P05-C trace schema/harness.
- P05-D first vertical differential checker.
- P05-E model-check artifacts.

## Rollback

Disable refinement-dependent optimized contour and fresh-admit on the reference staged path. The formal tooling itself has no runtime rollback semantics.

## Managed DMA grant closure observation — 2026-09-30

ADR-003 adds `DmaGrantClosureObservationV1` separately from generic trace events.
The existing grant owner captures successful closure epoch after exact provider success and
post-callback incarnation/epoch checks. Its internal query supplies the latest traced Visible
snapshot, exact grant identity digest and closure tuple. The offline projection consumer
requires the exact valid prefix and expected tuple. An untraced later cycle clears the snapshot;
missing prefix, sink loss, stale grant and ambiguous closure cannot become a valid projection.
No generic Published, Settled or Released event is synthesized, and no observation field
participates in authorization/reclaim. Region mapping remains independently active after grant
closure. This closes a selected managed observation gap only; physical containment and complete
cross-project closure producers remain FutureGated. Gates OFF; ISA/opcode/CPU impact NONE.

Evidence: `artifacts/v6/iteration-20260930-p05-grant-closure`.

## IRQ delivery boundary review — 2026-10-01

IRQ delivery admission, provider Complete, exact mailbox publication and provider closure
are distinct transitions. Managed interlock tests and generic QV1/P05 regressions do not
provide an exact IRQ refinement trace producer or independently executing machine legality.
That exact contour remains Partial/FutureGated; no generic release event or trace-derived
permission is synthesized. See `artifacts/v6/iteration-20261001-irq-delivery-permission`.

## Settlement before publication — 2026-10-01

Existing ExternalOperationAuthority allows resource settlement after DeviceComplete or
Visible. The trace validator now preserves that independent accounting fact while later
visibility/publication proceeds. Early settlement cannot authorize release, imply publication,
or substitute exact no-publication closure. Generation drift preserves the accounting fact
but requires fresh quarantine/closure evidence; a second settlement remains invalid.
Four real runtime/direct-stream cases and negative model cases cover these paths.
Evidence: `artifacts/v6/iteration-20261001-p05-early-settlement`. RuntimeEnforced for the
named managed trace consumer only; generic physical/refinement qualification remains
FutureGated. Gates OFF, V1 public APIs/enums unchanged, ISA/opcode/CPU impact NONE.

## Snapshot/history observation consistency — 2026-10-01

The existing external-operation projection now compares snapshot Disposition and
EffectBoundary with the supported existing owner-history writers. A published snapshot
cannot erase the crossed boundary or claim Discarded/Completed, and a pre-submit snapshot
cannot synthesize cancellation/effect. The nonexistent PublicationFailed owner event is
rejected rather than treated as quarantine evidence. This is observation validation only;
no projection participates in effect permission, closure or reclaim. Evidence:
`artifacts/v6/iteration-20261001-p05-snapshot-facts`. Unsupported owner contours remain
Partial; physical qualification FutureGated, gates OFF, ISA/opcode/CPU impact NONE.

## Observable owner event guards — 2026-10-01

Projection checks the current writers' observable guards before reconstructing facts:
Active for Admit/Submit; Completed for visibility/publication; exact pre-submit or
submitted cancellation state; staged completed result discard; and preceding ambiguous
publication before no-publication closure. A connected history cannot cancel and then
reuse the same lifetime to admit/submit. These checks do not infer leases, authenticated
provider receipt, publication callback outcome or machine legality. Evidence:
`artifacts/v6/iteration-20261001-p05-owner-guards`. RuntimeEnforced for the named managed
observation guards only; other unsupported event contours remain Partial. Gates OFF;
ISA/opcode/CPU architecture impact NONE.

### Teardown observations — 2026-10-01

Existing AdvanceForTeardown drain and staged discard writers now project cancellation/quarantine, with repeated drain observed once semantically. Neither event projects closure or release. Real owner regression retains the draining operation on retry. Pre-submit teardown/release and physical containment remain Partial/FutureGated. Gates OFF; ISA/opcode/CPU impact NONE.

### Live teardown observation consumer — 2026-10-01

Terminate/Fault and Observe deliver committed supported external-operation teardown transitions through existing registered trace stream after releasing teardown/owner locks. Registration snapshot and exact principal generation limit observation; existing delivery interlock handles retries/reentrancy. Sink rejection/exception cannot change drain/reclaim. Tests cover submitted drain and completed discard, reentrant Observe, cross-thread Query during callback and sink failure. Pre-submit teardown/release and physical closure remain Partial/FutureGated. Gates OFF; public V1 API unchanged; ISA/opcode/CPU impact NONE.

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

### Late completion under resource quarantine — 2026-10-02

ADR-004 permits one actual RetireOrComplete observation after quarantine without clearing quarantine. The existing exact Submitted-binding completion writer and exact kernel budget settlement are the consumers; no new authority or closure API. Duplicate completion across quarantine is rejected, generation drift remains fail closed, and neither completion nor settlement permits visibility/publication/release. Full post-quarantine visibility/publication and fault/cancel completion vocabulary remains Partial. Public enum/API/package/schema unchanged; gates OFF; managed observational guard RuntimeEnforced; physical closure FutureGated; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-late-completion.

### Terminal completion fidelity — 2026-10-02

Under ADR-004, Cancelled/Faulted owner completion now projects RetireOrComplete before a new quarantine marker, or retains an existing quarantine marker. The outcome remains bound into the existing transition EvidenceDigest. Actual kernel completion/live stream and exact settlement/retry are tested; stale/duplicate completion refuses without history mutation and visibility stays forbidden. Terminal response and accounting never establish closure or release. Named managed terminal observation closed (RuntimeEnforced); post-quarantine visibility/publication vocabulary Partial and physical closure FutureGated. Gates OFF; V1 enum/API/package/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-terminal-completion.

### Direct publication owner observation — 2026-10-02

P05 now preserves actual DirectPublicationObserved from the existing DirectCoherent owner path, used by HybridCpuExternalOperationProvider and CxlType2AcceleratorService. This records bookkeeping for an effect already observable at its submit boundary; it never invents a withheld publication action. Exact Visible-to-Published writer edge, Completed disposition, policy and absence of publication ambiguity are checked. Staged Published and DirectPublicationObserved cannot substitute for each other. Actual kernel tests confirm irreversible submit boundary, zero publication callbacks, valid published prefix, closure-free release refusal without history mutation, and policy/event substitution rejection. No budget settlement, closure or release is fabricated. Generic successful prefix does not qualify physical coherent memory/DMA. Named managed observation closed, RuntimeEnforced; full non-resource settlement/release and resource-quarantine vocabulary remain Partial. All gates OFF; public V1 API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-direct-publication.

### Irreversible direct cancellation observation — 2026-10-02

P05 now preserves actual DirectWriteCannotBeUndone from existing DirectCoherent cancellation in DeviceComplete/Visible. It reconstructs Faulted disposition, retains the irreversible/external effect boundary and records Quarantined with the exact writer EvidenceDigest. Writer guard requires DirectCoherent, Completed disposition and no ambiguous publication; repeated impossible writer after Faulted and staged-policy substitution are rejected. Actual kernel cancellation/retry and stale-generation refusal are the tested consumer; HybridCpuExternalOperationProvider.RequestCancellation and Type2 failed-completion paths already call this owner. ProviderResourcesClosed=true alone does not make this direct fault locally releasable. No undo, containment, settlement or closure event is fabricated. Named managed observation closed, RuntimeEnforced; resource-quarantine/non-resource release vocabulary Partial; physical FutureGated. Gates OFF; V1 APIs/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-direct-cancellation.

### Accounting quarantine is independent of effect quarantine — 2026-10-02

ADR-005 adds ResourceAccountingQuarantined=12 to the V1 software trace alphabet because real resource settlement/reconciliation can proceed alongside completion/visibility/publication. Existing enum values/DTO version/encoding and provider Quarantined/GenerationChanged/closure/release rules stay unchanged. Every real ResourceQuarantined or ResourceSettlementQuarantined owner event is preserved separately with its existing transition digest. Resource association and terminal-order guards remain. Actual kernel/live sink and exact budget settlement/reconciliation consumers are tested at pre-completion, pre-visibility settlement and post-publication quarantine, including managed journal failure after budget settlement and exact retry without resubmit. Provider loss produces both independent facts; accounting recovery cannot clear effect quarantine or drift. No observation is permission or closure proof; Budget accounting terminal state is not Region/provider release. Named managed distinction closed, RuntimeEnforced; full pre-submit/non-resource release vocabulary Partial; physical FutureGated. Gates OFF; older unsupported event consumers must fail closed and may not discard the mandatory event; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-p05-accounting-quarantine.

## 2026-10-03 P04 legacy ModelOnly creation / publication owner

Bounded legacy CreateVirtualDomain creation guard closed; execution/provider contour remains ModelOnly. Existing bridge model-domain owner now owns per-subject pending creation and unresolved owner/backend tuples, exact known lease validity/epoch/quarantine and close admission flags. Root/child and legacy ModelOnly creation share checked source-aware issuance. Fresh process/Create capability commit under lifecycle -> bridge -> CapabilityAuthority precedes callback; known model receipt registers existing VirtualDomainAuthority cleanup record even after stop. Invalid lease, exception, non-NotAccepted unknown response, reset or lost local receipt retain pins; real process teardown consults this owner. No unknown outcome becomes closure/no-effect.

Publication is separate from record existence/created state. Existing VirtualDomainAuthority owns AuthorityPublished; new root/child/model records start unpublished. Its exact-record owner commit occurs only after all actual grants and fresh source/process/binding checks. ResolveVirtualCapability, secure composition and virtual-I/O consumers require publication before admission. Observation/identity Resolve remains compatible; flag alone grants no permission. Known cleanup remains reachable by existing teardown. Closed model records are not reopened by guest-mapping/composed teardown. Close reset balances copied admission flags without clearing quarantine/continuity pins. Transition/effect overlap and fresh late state publication remain separate Partial work.

Actual Host ModelOnly producer stale revoke formerly removed entry before comparing full lease tuple. It now validates current lease before removal under a private lock for the same existing model table; create/transition/revoke and ID/counter mutations serialize. Stale generation/owner refusal preserves current lease; 32 concurrent creates keep unique owner tuples. No new authority ledger, provider contract/API/enums/schema/package changes; public V1 retained.

Fifteen new cases: nine source/stop/lost receipt/unknown/reset/malformed/nonce/revoke scenarios; publication gap with actual Configure denial then positive path; true concurrent stop/overlap with callbacks outside owners; source-generation/profile refusal; reset during revoke counter balance; actual Host stale generation/owner revoke; concurrent Host owner tuples. Initial build2 signature errors/10.37s corrected; build0 errors/9 warnings/32.58s before final cases/producer fix. One test compile error used nonexistent CloseVirtualDomain name; corrected to actual DestroyVirtualDomain. Focused354/0 then355/0 (reset),355/0 (publication owner lock), final357/0 (Host fix). Initial broad4431/0/2 skipped preceded Host defect repair; it is superseded for current inputs. Final broad4433/0/2 skipped (4149+60+166+58). Evidence: artifacts/v6/iteration-20261003-p04-model-virtual-create.

Claim split: provider/execution/acknowledged close stays ModelOnly; named managed source/tuple/publication guards RuntimeEnforced; physical containment/closure/reclaim/legality FutureGated. Model ack is not physical closure evidence. Unknown model creation/revoke/reset lacks demonstrated exact reconciliation consumer and remains Missing/pinned independently from invocation/mapping/IRQ/root/child ambiguity. All v6 gates OFF; Java excluded; ISA/opcode/CPU architecture NONE. P04/P05/Nested creation and model transition/final publication globally Partial. Next ready slice: model transition exclusive provider admission, fresh capability/process/domain state commit, unknown outcome pin and real closure consumers, preserving ModelOnly ceiling and V1.

## 2026-10-03 P04 ModelOnly transition / fresh state commit

Bounded Configure/Start/Park/Resume guard closed; full P04 and physical contour remain Partial/FutureGated. Existing CapabilityAuthority owns exact process/domain generation, source epoch/lineage, rights and resource; VirtualDomainAuthority owns exact published record/state; existing bridge model table owns exact binding/provider lease/backend and transition pending/quarantine/unknown outcome. Pending sets are admission interlocks, not permission. Pure commits order lifecycle -> bridge -> capability -> local virtual record; provider callbacks run outside those gates.

Admission captures exact process/record/from state and reserves exclusive delivery under fresh source validation. Pending spans provider callback through fresh local commit and refuses overlapping transition/closure. Successful receipt cannot publish after source revoke, process stop, record/state/binding change or backend reset. Unknown enum/refused source cannot start callback. Exception, reset, unknown returned failure or lost continuity preserve TransitionMayHaveEffect/quarantine; NotAccepted permits only existing known model drain/revoke cleanup. Known successful late effect after revoked source retains cleanup record and real teardown can drain/revoke. No fabricated reconciliation API.

Ten new cases: four transition/stop/overlap/closure races; exception/Denied/reset/NotAccepted boundaries; source revoke during callback with no replay; unknown transition and revoked source before callback. Build 0 errors/9 warnings/25.85s before new cases. Initial focused366 passed/1 failed: test incorrectly expected Configured after actual reset quarantine; corrected expectation. Final focused367/0; broad4443/0/2 managed skips (4159+60+166+58). Evidence artifacts/v6/iteration-20261003-p04-model-virtual-transition; fresh source/package/toolchain/binary hashes and baseline, no transferred prior qualification.

SingNext managed source/process/state guards RuntimeEnforced for named software paths; provider admission/execution/closure acknowledgement ModelOnly. Semantic refinement Partial outside this bounded evidence; HybridCPU machine legality not qualified here. All v6 gates OFF; Java excluded; ISA/opcode/CPU NONE. Unknown model transition exact reconciliation consumer Missing/pin retained, independently of invocation effects, lost mapping/IRQ receipts and root binding ambiguity. Child transitions/executable artifact final state and nested issuance remain Partial; next dependency-ready slice: child transition pending/source/fresh local state commit through existing provider and teardown consumers.

## 2026-10-03 P04 child transition / fresh local state

Bounded Configure and lifecycle child Start/Park/Resume managed guard closed; executable-artifact Start/bind and nested issuance remain separate Partial paths. Existing bridge child owner retains TransitionInFlight through known provider-state commit and kernel fresh local state commit. Admission and final commit use current process reference/generation, exact published VirtualDomainAuthority record/from state/child, exact child/parent binding/backend, fresh CapabilityAuthority rights/resource/epoch/lineage. Configure uses a pure current-child/active-parent/local-state owner commit with no provider callback. Shared kernel pending set is admission only. Lock order lifecycle -> bridge -> capability -> local virtual record; provider callback stays outside owner locks.

A returned successful provider transition is owned by the child table even when local source/process permission has since been revoked. It supplies only known cleanup state; fresh kernel commit refuses revoked source/stop and cannot replay. The existing drain/child closure/process teardown consumer can close that known child after pending delivery ends. Overlapping transition/close/effect admission is refused through local commit. Exception, reset, unknown returned failure or lost exact record/backend continuity preserves existing Faulted child pin; no fake reconciliation, cancellation/no-record/terminal inference, or new owner. BeginDrain remains real cleanup after local revoke; root/new-effect admission requires active exact parent.

Eleven new cases: six true concurrent Start/Park/Resume with source revoke or process stop, closure/overlap refusal and known cleanup retry; exception/reset/Denied pins with balanced pending; revoked Configure and unknown transition before provider; completion gap refusing close/overlap. Existing secure-composition regression formerly expected successful Start after TerminateProcess inside callback; corrected to failed late publication/not Running/one known effect/real teardown success. Build0 errors/9 warnings/26.00s before new tests. Initial focused377/1 (old contradictory expectation); final378/0. Broad4454/0/2 managed opt-in skips (4170+60+166+58). Evidence artifacts/v6/iteration-20261003-p04-child-virtual-transition has exact commands, times, fresh tuple/hashes/manifests.

Named SingNext managed guards RuntimeEnforced; controlled child-provider test effects/closure ModelOnly. These tests do not qualify physical child effects or production/hardware. Provider production admission/refinement/HybridCPU legality remain independent and unqualified beyond prior exact contours. P04/P05 overall Partial; unknown child effect exact reconciliation consumer Missing/pinned independently from invocation/lost mapping/IRQ/root/model ambiguity. All v6 gates OFF; public V1 APIs/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Next ready slice: executable-artifact bind/Start fresh source/process/exact receipt/state publication through actual consumers.

## 2026-10-03 P04 executable artifact source / receipt / state publication

Bounded root-child executable artifact Bind/Start managed guard closed; exact release remains Missing/pinned, nested lineage/issuance Partial. Existing ProcessRegistry/CapabilityAuthority own fresh process/subject generation/epoch/lineage/Execute rights/exact resource. VirtualDomainAuthority owns exact published local record/from state, known artifact cleanup receipt retention and local state commit. Existing bridge child/guest/parent-mapping/artifact records own exact binding/lease/backend/provider generation and known artifact/execution receipts. Pending sets/counters are admission interlocks only.

Kernel pending covers callback and local receipt/state commit. Pure local lock order lifecycle -> bridge -> source -> virtual owner; provider callback and provider-incarnation observation stay outside these gates. Bridge rechecks exact current record references, child/root/mapping authorization/backend, pending closure/effect and identity capacity. Request validation precedes mutation/provider; package gets a private bounded copy outside gates and receipt digest must equal SHA256 of captured bytes. Start operation identity increments only after fresh source commit. Already Started retains V1 PlatformDenied; stale generation/owner refuses without callback/identity mutation.

Known successful bind receipt is retained before mutable authorization/continuity checks and reaches existing kernel cleanup record even after stop/revoke. Source failure cannot forget it or authorize Start. Known execution receipt/Started fact is likewise retained independently of local permission; revoked source/stop prevents Running publication and replay. Exception/reset/unknown return/malformed receipt/package mismatch pins existing child/guest/parent dependencies; no invented consumer. Pending spans completion consumer and balances on every tested outcome. Explicit NotAccepted permits fresh bind admission only, no closure grant. Terminal retired-work receipt never releases artifact or mapping; existing unmap consumer refuses retained artifact, exact release evidence/consumer absent and teardown/reclaim remain pinned.

Nineteen new cases: four true concurrent bind/Start with source revoke/stop; ten bind/Start exception/reset/Denied/malformed/NotAccepted boundaries; caller-buffer capture/duplicate bind; source/bounds no-mutation; two completion gaps; stale artifact generation/owner with start identity preserved. Initial build0 errors/9 warnings/26.09s before tests. Focused395/1 exposed known bind receipt dropped before cleanup retention after stop; production fixed, focused396/0. Stronger source/receipt assertions and stale identity case397/0; V1 Started error restored, final397/0. Broad4473/0/2 managed opt-in skips (4189+60+166+58). Evidence artifacts/v6/iteration-20261003-p04-executable-artifact-publication contains exact commands/TRX durations/current tuple/source/binary/manifests.

Named SingNext managed guards RuntimeEnforced; controlled provider fixture ModelOnly. Broad adapter tests preserve their exact software evidence; no upgraded physical/production/refinement/machine claim. Full P04/P05 Partial, release/reconciliation Missing, physical FutureGated. All v6 gates OFF; public V1 contracts/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Next ready slice: actual nested creation fresh source/parent-child lineage, checked staged authority issuance/publication, existing closure consumer.

## 2026-10-03 P04 nested creation / source / staged publication

Bounded nested creation guard closed; full P04/P05 and nested effect consumers remain Partial. Existing capability owner freshly validates exact parent Configure source/subject generation/epoch/lineage/resource before provider admission and every derived grant/publication. Kernel uses existing owner creation interlock through callback, known record attachment, issuance and final publication; immediate process revoke remains first and pending prevents teardown snapshot freeze/reclaim before late record. Existing parent record/reference/state/child and bridge root/ancestor lineage are revalidated, callbacks outside owner locks. Pure commit order lifecycle -> bridge -> capability -> virtual owner. No new authority or ledger.

Known nested receipt reaches existing VirtualDomainAuthority staged record even after source revoke/stop; AuthorityPublished=false prevents actual effect consumers from using partial grants. Shared checked issuance now supports existing source guard and optional nested authority subset; absent rights return default V1 capability slots, partial committed grants remain in real process/cap/virtual owners. Publication requires fresh process/local parent/from state/source and exact current child/active root/ancestor chain; parent closure/drain/transition blocks fresh admission/publication. Unknown mandatory authority bits denied before callback. Root and ModelOnly five-capability basis remains compatible.

Known staged child closes through real recursive nested/process teardown consumers after pending ends, including partial mint and late source/stop cases. Lost receipt, reset and unknown provider response retain existing immediate-parent NestedCreateMayHaveEffect/Faulted pin; exact reconciliation consumer Missing. Terminal/no record/model response cannot prove physical closure. This slice does not qualify all nested lifecycle/event/mapping/I/O/artifact lineage consumers or identity-capacity edge contours.

Eleven new cases: seven source/stop/exception/reset/nonce revoke/nonce stop/Denied cases; partial publication/subset positive path; true concurrent stop/overlap; two-level ancestor drain with no new admission/Configure publication and real cleanup; unknown authority/revoked source refusal without admission mutation. Initial build0 errors/9 warnings/26.76s before final tests/ancestor admission guard. Focused406/0 before two added boundary cases;407/1 test compared count before legitimate source revoke; corrected count after revoke; final408/0. Broad4484/0/2 managed opt-in skips (4200+60+166+58). Evidence artifacts/v6/iteration-20261003-p04-nested-source-publication includes exact commands/times/current source/dependency/package/toolchain and binary/manifests.

Named SingNext managed nested creation/staging guards RuntimeEnforced; controlled provider effects/closure ModelOnly; physical FutureGated. All v6 gates OFF; public V1 API/enums/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Source grant guard is a transaction creation/publication check, not a new persistent delegation/revocation authority. Independent artifact release/invocation/lost mapping/IRQ/root/model/unknown child closure remain Missing/pinned. Next ready slice: fresh ancestor/local lineage admission and late publication for existing nested lifecycle/effect consumers.

## 2026-10-03 P04 nested lifecycle lineage / owner state commit

Bounded nested Configure/lifecycle Start/Park/Resume ancestor admission and late publication guard closed. Existing bridge child owner now supplies one pure exact active root/ancestor validator shared by creation/publication and lifecycle consumers. New lifecycle admission rejects closed/draining/faulted/pending/wrong/cyclic ancestor chains; final local publication repeats lineage checks while own pending remains. Known returned provider transition state stays owned independently of failed local permission. BeginDrain and exact closure/recursive teardown remain existing cleanup paths after ancestor drain/revoke; no active-ancestor requirement is imposed as closure evidence.

Existing VirtualDomainAuthority owns local exact ParentDomain/owner-generation/current-reference/published-state lineage. Record.State reads/writes now use that same authority gate, including existing direct setters; no per-record second owner or ledger. Pure CommitEffectAdmission checks current published record/from state and active ancestors atomically with trusted admission/state mutation while CapabilityAuthority source guard remains held. Nested creation, child Configure/lifecycle, executable artifact and ModelOnly transition source commits consume this boundary. Lock order lifecycle -> bridge -> capability -> virtual owner; provider/nonce/trace callbacks outside those commits. Identity/receipt/publication alone is no permission.

Ten new cases: six Start/Park/Resume ancestor drain before/during callback, denied admission/late publication/no replay plus known cleanup; real root artifact-unmap refusal creates local ancestor quarantine despite active bridge identity and actual Configure denies without mutation; true concurrent nested delivery/ancestor drain/stop with closure/reclaim refusal and real retry; stale local ancestor after earlier admission snapshot refuses at actual source commit; actual State setter serializes with owner commit. Shared root/ModelOnly/artifact guards regressed. Initial build0 errors/9 warnings/26.42s before final owner changes. Focused415/0, strengthened real consumer/concurrent case416/0; initial broad4492/0/2 skipped superseded after review found local snapshot/state writer gap. State owner serialization/atomic source commit fixed, owner focused416/0; ModelOnly same boundary plus two new owner tests final418/0. Final broad4494/0/2 managed opt-in skips (4210+60+166+58). Evidence artifacts/v6/iteration-20261003-p04-nested-lifecycle-lineage records exact commands/times/current tuple/source/binary/hashes/manifests.

Named managed lineage/state/source guards RuntimeEnforced; controlled provider effects/closure ModelOnly; physical FutureGated. Gates OFF; public V1 enums/API/packages/schema unchanged; Java excluded; ISA/opcode/CPU NONE. Overall P04/P05 Partial. Nested event/trap/mapping/I/O/executable bridge lineage and unknown closure/release/identity capacity edges remain Partial/Missing independently. Next ready slice: actual nested event/trap and mapping/I/O effect admission/publication through existing ancestor/source owners.

### 2026-10-03 P04 nested bridge event/trap lineage
Bounded bridge guard implemented: exact current child/root/ancestor generations and active lineage are checked before provider callback and after valid receipt before publication. ResolveChild dictionary reads share the existing lifecycle gate. Known receipt sequence remains cleanup fact after late ancestor drain; pending counters span callback/publication and block actual closure. Provider callbacks remain outside owner locks. Six controlled model cases cover pre/during drain and concurrent closure refusal followed by existing recursive teardown retry.
Final focused 424 passed/0 failed; solution 4500 passed/0 failed/2 managed opt-in skipped. Kernel event/trap fresh source capability and local publication remain Partial, next dependency-ready slice. Unknown receipt/reset/exception closure consumers remain Missing/pinned. Gates OFF; named bridge managed guards RuntimeEnforced, controlled provider ModelOnly, physical FutureGated. V1/API/package/schema unchanged; Java excluded; ISA/opcode/CPU impact NONE. Evidence: artifacts/v6/iteration-20261003-p04-nested-bridge-delivery-lineage/audit.json.

### 2026-10-03 P04 trap source publication
Kernel trap observation now uses existing pending virtual transaction, exact process/local running child/ancestor and CapabilityAuthority Traps.Read guard both for bridge admission and late publication. Existing bridge pending spans completion; known receipt sequence survives late source refusal. Composition/provider revalidation stays outside owner/source locks; pure commit order lifecycle -> bridge -> capability -> virtual owner. No new owner or public V1 contract.
Focused-final 429/0 (five new source/stop/local-ancestor cases); solution broad-final 4504 passed/1 failed/2 skipped. Failure: concurrent user Boot reference to tools/SingPlus.Admission violates existing PrivilegedMechanism -> Tooling rule. Architecture follow-up 25 passed/1 failed also preserves this refusal; exact ManagedCap sample is classified Sip, with negative privileged/provider edge checks. Boot/user files preserved, no blanket Tooling exemption. Bounded trap behavior verified; repository-wide architecture exit remains Contradicted, full P04/P05 Partial. Gates OFF; controlled provider ModelOnly, named managed source guard RuntimeEnforced; physical FutureGated. Event source/local commit consumer next. Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261003-p04-trap-source-publication/audit.json.
