# ADR-016 — Original native root Close

Accepted 2026-10-10 for RuntimeKernel.TerminateProcess → platform bridge/provider →
HybridCPU neutral ModelOnly runtime. V6-013/014/016/026; ISA NONE; gates OFF.

The terminal native-root-close-fence repro demonstrated that a source check before
legacy Close permits an equal numeric lease in a replacement realm to be closed.
Original Bind/Map fencing (ADR-015) does not fence root Close. This is a concrete
producing consumer need for an optional additive contract, not a new authority.

INeutralOriginalDomainCloseProvider carries original Close invocation, captured
runtime incarnation, original Bind invocation and domain lease. The existing native
lifecycle gate validates the same admission owner and dependent inventory and
retains the Close intent before changing lifecycle. The same native owner retains
the exact outcome. Pure ResolveDomainClose addresses only that original surviving
fact; absence never implies closure. The optional Close is exposed through its
interface to preserve legacy source calls such as Close(default).

The platform provider records intent before invocation and raw envelope before
source callbacks. Missing/malformed responses pin and never repeat Close. Lost reply
can use pure lookup with fresh source/canonical checks; late publication refusal
retains the outcome. Exact source-fence rejection is consumed as no-effect in the
addressed realm, separately from closure; it permits a fresh invocation only after
source returns. ActiveDependents is pre-effect refusal. Original owner copies,
foreign requests and outcomes cannot transfer closure. Final cleanup publication
uses existing local owners/interlocks and dependent inventories; existing Region,
budget and process consumers perform reclamation. Callbacks remain outside provider
and kernel commit locks. Completed exact lifetime does not require new permission.

IPlatformOriginalDomainClosureProvider exposes pure same-owner cleanup lookup to
the existing bridge root consumer. A bridge may-have-effect pin permits this query,
never another Revoke. Provider success retained after loss of its published reply
is still a completed exact fact; lookup returns that fact without another native
call or fresh active permission. Native/incapable absence stays pinned.

V1/incapable consumers retain conservative semantics. This surviving native original
Close resolution does not establish durable recovery, all-fact-loss containment,
production provider containment, artifact release, physical properties or
ProductionQualified. No enum, V2, package, ISA or machine legality change.
