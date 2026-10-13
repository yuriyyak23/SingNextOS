# ADR-005 — Resource accounting quarantine trace

Status: accepted for the additive managed observation contour, 2026-10-02.

Existing ExternalOperationAuthority resource binding can become Quarantined while
completion, visibility and publication still progress. Exact kernel settlement retry
can reconcile the existing ResourceBudgetAuthority lease. Generic Quarantined denotes
possible external-effect containment ambiguity and forbids publication/release.
Conflating these facts rejects real owner histories; erasing either fact loses evidence.

Add ResourceAccountingQuarantined=12 to the software SemanticTraceEventKindV1 alphabet.
Existing values, DTO version, signatures and canonical encoding are unchanged. Older
validators reject the unknown mandatory kind; they must not discard it or silently
downgrade. Qualification remains bound to the exact checker/source/dependency tuple.
This is the demonstrated necessity for the narrow additive enum extension, not V2
migration and not an ISA/opcode/CPU encoding change.

The new observation preserves generic lifecycle state. It is legal only after the
possible-effect boundary and before accounting settlement/release. Repeated real failed
settlement attempts remain observable. Settled is still a separate exact owner fact;
completion/publication/accounting quarantine cannot replace it. Existing provider
Quarantined, GenerationChanged, EffectClosedWithoutPublication and Released rules
remain unchanged. A provider fault stays quarantined after accounting reconciliation.

Projection validates the existing resource association and owner writer history. Every
ResourceQuarantined/ResourceSettlementQuarantined produces its own accounting event
bound to the existing transition digest. Runtime admission, Region/budget/provider
closure and reclaim never read trace state. No new authority or closure API.

Required evidence: actual live kernel completion/visibility/publication and exact
settlement/reconciliation; managed journal-failure retry; provider loss remains generic
quarantine; malformed association/terminal order, invalid event positions, source
generation drift and differential comparison. New events cannot prove physical closure,
minimum service, deadline/WCET, persistence/durability or safe reclaim.

All v6 gates OFF. Managed observation guard ceiling RuntimeEnforced; physical
qualification FutureGated. Java excluded. HybridCPU package/ISA/opcode/CPU impact NONE.
