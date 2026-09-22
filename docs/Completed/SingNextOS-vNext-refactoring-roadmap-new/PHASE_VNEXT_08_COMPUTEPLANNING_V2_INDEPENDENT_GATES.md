# P08 — COMPUTEPLANNING V2 INDEPENDENT GATES

**Live disposition:** closed at internal host/JIT `RuntimeEnforced` for staged `ComputeTime/Nanoseconds` resource-aware plan admission with independent effect/resource/budget/Region/provider/CPU-legality gates on HEAD `8c3f55e47555b2db99356b861ee404211d072edc`; `FG-VNX-COMPUTE-V2` remains OFF and ordinary ComputePlan behavior remains default. P09 provider mapping remains FutureGated. See `EVIDENCE_P08_COMPUTEPLAN_INDEPENDENT_GATES.md` and `P08_QUALIFICATION_TUPLE.json`.

**2026-09-22 sequential re-audit:** current HEAD is `800027893dcf1ed23d8d7fe775841dd9d01a9fff`. Duplicate live provider identities now fail closed before admission instead of throwing. Current evidence is `P08_REAUDIT_20260922.md` with tuple `P08_REAUDIT_20260922_TUPLE.json`; claim and OFF gate remain unchanged.

## Purpose

Extend compute planning with semantic resource requirements while preserving independent effect, resource, ownership, provider and CPU-legality gates.

## Preconditions

- P07 closed.

## Architectural decisions

- ComputePlan is non-authoritative and may contain only semantic execution/resource requirements and provider candidate evidence.
- No lane/opcode/slot/queue/token/topology enters source-facing ABI.
- Plan selection may use load/performance evidence but execution revalidates all exact owners and generations.
- Fallback between providers requires explicit semantic compatibility; no unit laundering.

## State / linearization model

Planner has no authoritative mutable state. Cached plans are hints keyed by versions/generations and always revalidated live.

## Negative-space obligations

- stale plan after grant revocation;
- stale provider generation;
- resource class reinterpretation during fallback;
- effect/resource gate accidentally fused;
- provider-private identifier leakage into public contracts.

## Required executable tests

- Same intent chooses different provider without changing authority.
- Stale plan/cache fails live revalidation.
- Independent truth-table tests for each gate false while others true.
- Public reflection test forbidding provider-private fields.
- Incompatible fallback rejected.

## Expected code / contract owners

- ComputePlanner / ComputeIntent / ComputePlan contracts
- existing authority owners only for validation

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Plan remains policy/evidence.
- All gates can independently deny submit.

## Prerequisite for next phase

P09 maps semantic envelopes to HybridCPU/provider boundary without granting authority.
