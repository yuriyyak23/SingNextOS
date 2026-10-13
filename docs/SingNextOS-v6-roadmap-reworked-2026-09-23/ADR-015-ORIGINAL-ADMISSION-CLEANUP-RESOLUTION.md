# ADR-015 — Original admission cleanup resolution

Producing-owner fence refinement: actual kernel Bind/Map source-swap repro produced
foreign native effects despite pre/post incarnation checks (two failures, fence-repro).
Known raw outcomes alone therefore cannot authorize cleanup adoption. The optional
INeutralOriginalAdmissionProvider carries original request/incarnation into the
ModelOnly producer; comparison and Bind/Map admission share its existing lifecycle
gate. Map additionally binds the original parent Bind invocation and same native
owner reference. Existing native owner records retain the admission fact; no new
ledger, authority or machine legality rule is introduced. Provider stores the exact
envelope before source callbacks; cleanup queries require a matching successful
source fence. Incapable/V1 outcomes remain conservative and cannot use this new
resolution path. A source mismatch proves only no effect of this request in that
runtime, not closure. Exact response carries ProvenNoEffect only with a default
lease/mapping. Bridge retains this original fact independently of platform closure;
actual teardown rolls back the staged Region reservation and consumes the original
budget charge. New revoke/effect is refused. Fresh source/canonical/dependency checks
apply until local consumption; completed exact facts do not require active admission.
Native lease validation rejects copies of admission owners and changed original
parent references. Terminal native, affected and main integration exits are recorded in
[the bounded report](../../artifacts/v6/iteration-20261010-unpublished-admission-resolution/report.md); native root Close source fencing remains separate. This additive local contract does not update ExternalRuntime NuGet packages
or establish production/all-fact-loss containment.

Accepted for actual RuntimeKernel root Bind/exact Map → HybridCPU ModelOnly provider → process teardown, 2026-10-10; gates OFF. Before this change, both known unpublished native outcomes survived late source refusal, but the bridge could not address the generic failed response. Initial kernel repro retained Region and mapping charge after the original source returned. The implemented request-correlated consumer now performs exact cleanup or distinct no-effect rollback. Original bridge fact, process generation and provider root/mapping fact already exist; no recovery authority or second ledger is needed.

An optional additive IPlatformOriginalAdmissionResolutionProvider accepts immutable nonzero invocation keys before Bind/Map effect and resolves only that retained exact request/outcome. Queries never Bind/Map/Close, never infer absence/no-effect, and return cleanup correlation, not permission or closure. Generic V1 paths keep existing semantics. Unknown native response, lost original owner, foreign/equal tuple and source change remain unresolved. This is surviving-original dev resolution, not durable all-fact-loss/reboot or production containment.

Provider keeps request and known raw outcome in existing root/mapping admission owners. Any unpublished cleanup record belongs to that original fact; routing can attach the same record after source/owner checks, never a copied owner. Such records deny new provider admission. Bridge retains its original request, resolution observation and same owner; only a fresh exact result can attach cleanup lease. Root remains quarantined, mapping authorization revoked. Existing Close/completion consumers establish closure; existing Region/budget owners perform exact local consumption. No charge is released by query/adoption. Consumed closure requires no repeat query or effect.

Actual process teardown invokes reconciliation outside kernel/provider owner locks, then checks the same ProcessRegistry/teardown record before attaching cleanup routing. Existing progression and mapping closure interlocks balance exceptions/reentry/overlap. Failed query retains all facts. Storage freshness, authenticated durable lifetime continuation, remaining legacy native root Close source fencing, general concurrent atomicity, artifact release and production execution remain separate gaps. No enum/V2/durable schema/package/ISA delta.
