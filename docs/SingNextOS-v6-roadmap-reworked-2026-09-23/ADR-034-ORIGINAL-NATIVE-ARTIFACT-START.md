# ADR-034 — Original native artifact Start

Accepted 2026-10-11 for the external CPU artifact execution consumer, extending
ADR-033 local attempt retention and ADR-017 independent artifact release.

Optional IHybridCpuOriginalChildExecutionStartRuntimeV1 names the exact admitted
artifact receipt and original Start request. Native Start checks original child,
Bind fact/self identity, child/mapping admission objects captured by Bind, mapping
factory and current canonical parent lifetime under existing parent→child locks.
Generation capacity is reserved atomically before possible execution. The child
retains self-owned invocation and issued marker before ExecuteToTerminal, then raw
success/fault on that same fact before response. Unknown exception has no response
fact, never inferred no-effect. V3 legacy callers use the same producing core.

Same-original repeated calls cannot invoke execution again. Surviving original raw
is returned through pure source/factory validation; missing/copied fact or history
returns unknown. Explicit pre-effect refusal is NoEffect for that invocation, not
artifact backing release. Equal receipt/request/fact copies cannot transfer an
original observation. Completed observation lookup does not require fresh active
execution permission; current identity/factory continuity remains mandatory.

The adapter retains native request before effect and raw typed response before
callbacks/source probes. Native reply loss may query the exact original request,
retain raw cleanup evidence and return Ambiguous; this never republishes an
execution receipt or authorizes another Start. Existing exact artifact release
joins the original execution and is the independent no-further-use/backing owner.
Region and quantitative cleanup must consume that release plus remaining
child/guest/root lifetimes, with independent source/history/backing pins intact.

Contracts1.24.0 and runtime1.16.0 were initially adopted by actual package/assets
inputs; final self/admission fixes are adopted through runtime1.16.1. This ADR does not
assert that package adoption or final broad verification is complete: consult the
current modernize queue and native-original-start report for terminal inputs.
No new authority, ledger, schema or machine semantics. No cold durability or
all-fact-loss guarantee. Gates OFF; ProductionQualified false; ISA NONE.

## No-effect history extension

The same native child owner retains exact pre-effect refusal facts in issued/self
checked history, using the existing Start fact type. Known no-effect repetition
returns that completed observation; a fresh admission requires a distinct request
and correlation. Copied/absent history remains unknown and fences both optional
and legacy Start. No-effect storage is atomic with the refusal under existing
parent→child locks and occurs before caller transport/callbacks. Pure lookup
requires original factory identity but no new active execution permission.

The adopted adapter retains pure no-effect resolution before source callbacks,
then consumes only exact same-request/current original history and generation.
This is cleanup-only: reply-loss path still returns Ambiguous, does not publish
Start or revive child state/permission. No-effect alone leaves CPU backing and
Region/budget reservations held. Independent exact artifact Release must join
backing before guest/child/root/Region/charge cleanup. Foreign/missing query proofs
remain unknown/no-repeat; observed source ABA is an independent pin. Runtime1.16.2
adopts this extension; terminal verification belongs to the no-effect report.