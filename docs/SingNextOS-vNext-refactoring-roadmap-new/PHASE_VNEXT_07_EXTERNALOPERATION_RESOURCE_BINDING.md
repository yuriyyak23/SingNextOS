# P07 — EXTERNALOPERATION RESOURCE BINDING

**Live disposition:** closed at internal host/JIT `RuntimeEnforced` for exact `ComputeTime/Nanoseconds` ExternalOperation↔lease correlation and settlement on HEAD `8c3f55e47555b2db99356b861ee404211d072edc`; `FG-VNX-EXTOP-RESOURCE-BIND` remains OFF. Provider receipts remain evidence, ordinary ExternalOperation/SIP fallbacks remain default, and no provider artifact is qualified. See `EVIDENCE_P07_EXTERNALOPERATION_RESOURCE_BINDING.md` and `P07_QUALIFICATION_TUPLE.json`.

## Purpose

Bind one exact budget lease to one exact external-effect generation and define provider ambiguity, usage settlement and quarantine without collapsing effect, Region, publication or resource owners.

## Preconditions

- P06 closed.
- Existing ExternalOperation lifecycle and provider-generation semantics green.

## Architectural decisions

- Binding is internal/opaque and generation-exact; public DTO copies are not consumable authority.
- Provider receipts are evidence only and must correlate operation generation + provider generation + resource class.
- Duplicate/cross-operation receipts are rejected/idempotent.
- Provider loss after possible submit => quarantine or conservative charge, never automatic refund.
- Budget settlement never publishes output or releases Region uses.

## State / linearization model

```text
ExternalOperation: Prepared -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published -> Released
BudgetLease:        Reserved -> Bound -> Consuming -> Settling/Quarantined -> Released
```

The machines correlate but neither owns the other's truth.

## Negative-space obligations

- cancel before submit vs after submit;
- provider timeout/disconnect/restart;
- duplicate/cross-op/reordered receipt;
- partial completion/visibility delay;
- publication failure after resource consumption;
- release failure;
- OS process/session restart while operation remains in flight.

## Required executable tests

- Provider-loss test proving no auto-refund.
- Cross-operation receipt replay rejection.
- Completion-before-visibility test: no publication.
- Publication failure with correctly settled consumed resource.
- Region reclaim remains blocked by its owner despite budget settlement.
- Provider generation drift stale handling.

## Expected code / contract owners

- `ExternalOperationAuthority`
- `ResourceBudgetAuthority`
- provider adapters / `PlatformAuthorityBridge`
- response/publication owners

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Exact binding and ambiguity policy executable on host-model contour.
- Effect publication behavior unchanged.

## Prerequisite for next phase

P08 planners may reference required envelopes but never own these bindings.
