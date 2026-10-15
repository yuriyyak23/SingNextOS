# P03 evidence — atomic resource lease and settlement core

## Disposition

P03 is closed at `RuntimeEnforced` for the internal host/JIT `ResourceBudgetAuthority` contour. `FG-VNX-RESOURCE-LEASE` remains OFF because cross-owner admission and SIP resolution are P04/P05 prerequisites.

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Reviewed HEAD: `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`.
- Entry dirty worktree contained only additive P00–P02 work. No user-owned P14/historical artifact was changed.
- Applied invariants: VNX-004, VNX-007, VNX-010–012, VNX-022–024, VNX-028.

## Authoritative state and conservation

The existing `_reservations` dictionary and `_gate` remain the only quantitative owner and linearization boundary. No child ledger or DTO authority was added. The additive state machine is:

```text
Reserved -> Bound -> Consuming -> Settling -> Released
    |          |
    +-> CancelledPreSubmit
               +-> Quarantined -> Reconciled -> Settling -> Released
```

- `Release`/pre-submit cancellation can restore capacity only before possible consumption.
- `Consuming`, `Quarantined`, `Reconciled`, and `Settling` reject ordinary release.
- Settlement releases only unused capacity and retains exact charged usage in the ancestor accounting chain.
- Duplicate settlement/release returns the existing terminal snapshot and cannot create credit.
- Split atomically transfers part of the parent reservation into a child record without incrementing ancestor usage.
- Exact reservation generation and process generation are checked on every transition.
- `ComputeTimeNanoseconds` is the one initial quantitative dimension. No cross-dimensional conversion exists.
- Snapshots expose state/charged amounts as evidence and retain `AuthorizesEffect=false` and `AuthorizesReclaim=false`.

Legacy `Active` is an enum alias for `Reserved`; existing ordinary budget callers and release semantics remain compatible and all existing budget regressions pass.

## Negative and race coverage

- 32 contenders for the last 10 units produce exactly one winner.
- Split-vs-bind/consume race repeated 100 times never exceeds the ancestor limit.
- Bound/consuming/quarantined leases block process retirement.
- Post-consumption release and automatic refund are rejected.
- Reconciliation is required before ambiguous consumption can settle.
- Oversettlement, stale generation, zero/wrap/exhausted reservation IDs and ledger underflow paths fail closed.
- Pre-submit cancellation and duplicate terminal actions are idempotent.

The first identity-exhaustion test reached `BudgetExceeded` because capacity was intentionally still held. The test was corrected to release capacity first, isolating identity exhaustion without changing production behavior.

## Qualification

| Lane | Result |
|---|---|
| P03 focused | 6 passed, 0 failed |
| P03 + budget/inspector/cross-cutting regression | 29 passed, 0 failed |
| full solution build | succeeded, 0 warnings, 0 errors |
| first full non-GUI run | 1590 passed, 10 failed, 2 skipped; one DSC1 concurrency failure appeared |
| exact DSC1 reruns | 5 passed, 0 failed |
| repeated full non-GUI run | 1591 passed, 9 failed, 2 skipped; only the previously recorded historical/P14 failures remained |

The nine known failures still block full-suite/production claims. No P03 test failed in the final qualification.

## Gates and exclusions

- Enabled gates: none.
- Exact claim: internal `ResourceBudgetAuthority` host/JIT state machine only.
- P04 cross-owner prepare/revalidate/commit is absent; bypassing it would create check-then-act and compensation gaps.
- Ordinary SIP/Compute/ExternalOperation fallback remains intact. Plans, caches, receipts and telemetry remain non-authoritative.
- No provider, NativeAOT, HybridCPU ISA/microarchitecture, QEMU, firmware, CXL boot or hardware claim/work occurred.
