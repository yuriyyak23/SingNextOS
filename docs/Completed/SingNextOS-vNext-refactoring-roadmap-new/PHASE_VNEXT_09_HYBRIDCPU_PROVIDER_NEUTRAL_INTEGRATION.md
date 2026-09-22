# P09 — HYBRIDCPU PROVIDER NEUTRAL INTEGRATION

## Purpose

Integrate the corrected resource model with HybridCPU-v2 through versioned semantic ExternalRuntime contracts only where necessary, preserving HybridCPU runtime legality and provider admission as independent gates.

## Preconditions

- P08 closed.
- HybridCPU-v2 exact SHA/package/contracts re-pinned.

## Architectural decisions

- First try SingNext-only enforcement; do not change provider contract if unnecessary.
- If needed, add additive versioned semantic request/usage evidence contracts; never transport SingNext capability handles as provider authority.
- Runtime legality remains owned by HybridCPU; compiler metadata/certificate does not override it.
- Retire, provider completion, visibility, OS publication and resource settlement remain distinct.
- No ISA/VLIW/register/typed-lane change.

## State / linearization model

Provider contract objects are requests/receipts/evidence. SingNext owners remain local. HybridCPU machine state and retire machinery are unchanged unless a separately justified runtime-only enforcement hook is needed.

## Negative-space obligations

- local allowed false / provider true;
- resource lease absent / others true;
- CPU guard stale;
- provider restart/generation drift;
- receipt replay;
- provider reports unsupported guarantee;
- fake/parser support mistaken for executable support.

## Required executable tests

- Adapter truth-table conformance for independent gates.
- Exact package/contract-version mismatch fails closed.
- Receipt operation+generation correlation.
- Completion/visibility/publication ordering tests.
- No public lane/opcode/slot/queue/topology fields.
- Executable evidence required; parser/helper-only contour stays ModelOnly.

## Expected code / contract owners

- `HybridCPU_ExternalRuntime.Contracts` additive version if required
- SingNext HybridCPU provider adapter
- HybridCPU runtime legality/retire tests without ISA changes

## Claim boundary

`ExecutableAdapter`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- No layer impersonates another authority owner.
- No ISA change category populated.
- Exact adapter contour executable or gate remains OFF.

## Prerequisite for next phase

P10 may schedule among qualified semantic provider contours only.

## Current disposition (2026-09-21)

Closed for the exact existing ordinary host/JIT `ExecutableAdapter` contour recorded in `EVIDENCE_P09_HYBRIDCPU_PROVIDER_NEUTRAL_INTEGRATION.md` and `P09_QUALIFICATION_TUPLE.json`. The local Contracts 1.14.0 package is exact-hash pinned, but the roadmap HybridCPU source SHA is not locally verifiable. Both resource-specific HybridCPU gates remain OFF because 1.14.0 has no resource request or usage-evidence schema. This closure does not transfer P08 resource enforcement into the ordinary adapter and makes no ISA, hardware, NativeAOT, guarantee or production claim.

## Sequential re-audit (2026-09-22)

Current HEAD is `800027893dcf1ed23d8d7fe775841dd9d01a9fff`. Local package hash and executable ordinary-adapter coverage were reconfirmed; provider source and the referenced HybridCPU SHA remain unavailable locally. Current evidence is `P09_REAUDIT_20260922.md` with tuple `P09_REAUDIT_20260922_TUPLE.json`. Both resource gates remain OFF and no stronger claim is made.
