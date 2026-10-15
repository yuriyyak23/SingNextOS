# ADR-011 — Exact child-create invocation disposition

Accepted for bounded managed create consumer, 2026-10-08; gates OFF.

ADR-010 create/map capacity guards expose exact neutral NotInvoked, but translation to generic platform NotAccepted loses its provenance. Generic status alone cannot prove absence of effect. Blanket pinning also breaks the verified capacity refusal path and usable root facts.

Add optional IPlatformChildCreateDispositionProvider overload with invocation-local out bool refusedBeforeEffect. Existing V1 create signatures, enums and contract versions remain. True asserts no create effect for this invocation's exact supplied parent/intent; false/absent is ambiguous on failure. It is not permission, containment, previous invocation closure or reclaim evidence. No mutable disposition lookup/ledger or provider-type check is introduced.

HybridCPU reports true only for its local pre-effect interlock/identity refusal or actual neutral NotInvoked. Bridge accepts it only with NotAccepted, retains raw first, then freshly checks original source/parent/epoch before removing only this invocation fact. Unknown/generic failures and lost receipts retain pins. Earlier effects are unchanged. Concurrent creates retain separate original invocation facts at existing domain owner.

Required affected checks: generic failed create after fake effect/no-repeat/root/process refusal, lost receipt/reset/concurrency; actual adapter capacity zero/max/final usable reservation/no-effect; fresh-source failure/reentry and V1 callers. Focused plus stable applicable broad; failed history preserved. No package/ISA change; Java skipped. No physical/ProductionQualified claim.
