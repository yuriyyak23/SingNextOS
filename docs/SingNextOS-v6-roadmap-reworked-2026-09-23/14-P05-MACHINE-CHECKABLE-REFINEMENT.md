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
