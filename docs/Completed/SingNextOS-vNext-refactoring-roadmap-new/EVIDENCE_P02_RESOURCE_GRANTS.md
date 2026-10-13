# P02 evidence — resource-use grants in CapabilityAuthority

## Disposition

P02 is closed for the internal `CapabilityAuthority` owner contour at `RuntimeEnforced`; `FG-VNX-RESOURCE-GRANT` remains default OFF because no public/SIP admission contour exists before P03–P05.

Baseline is `6227ea7cf258ef6ffce52001d4d2ffee07355b35`; reviewed HEAD is `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`. Entry dirty worktree contained only additive P00/P01 work. No user-owned P14/historical file was modified.

## Owner and behavior

- `ResourceUseConstraintV1?` is stored in the existing `CapabilityRecord` and canonical capability serialization. No second dictionary/ledger was added.
- Mint/derive/revoke/retire and ancestor-lineage behavior reuse the existing `_records` lock and state machine.
- Derivation uses `EffectiveCapabilityConstraints.IsSubset`; class, amount, validity, assurance, semantic scope and depth can only narrow. Absence of a resource grant is narrower and cannot become a grant in a child.
- `ValidateResourceUse` performs live record lookup plus exact subject generation, exact resource generation, current revocation epoch, active lineage, validity interval and requested-envelope subset checks.
- A resource-only grant intentionally lacks `Execute`; an effect-only capability lacks the resource constraint. Each is denied when substituted for the other.
- Public DTO/JSON copies have no capability identity and return `CapabilityNotFound` without an exact live record.
- Derivation reserves no quantitative capacity. P03 remains the sole future quantitative owner.

## Tests and qualification

Focused P02 tests: 6 passed, including derive/revoke race repeated 100 times, stale subject/resource generation, revoke, future validity, widening of amount/scope/assurance, effect/resource independence and serialized-forgery rejection. Combined P02 plus existing capability ledger/algebra regression lane: 39 passed, 0 failed.

Full solution build succeeded with 0 warnings and 0 errors. Full non-GUI suite: 1585 passed, 9 failed, 2 skipped; the same nine pre-existing historical evidence/profile/P14 tuple failures recorded in P00 remain. No P02 test failed. `git diff --check`, JSON and manifest hashes are validated after evidence finalization.

## Gates, FutureGated work, and exclusions

- Enabled gates: none. Exact implemented claim is the internal host/JIT live-owner contour only.
- `FG-VNX-RESOURCE-GRANT` stays OFF until P03–P05 supply quantitative lease, cross-owner admission and generated SIP resolution. Enabling it now would allow callers to bypass independent budget/effect checks.
- Ordinary SIP/Compute/ExternalOperation fallback is unchanged. Budget snapshots, plans, caches, receipts and telemetry remain non-authoritative.
- No provider, NativeAOT, HybridCPU executable, ISA/microarchitecture, QEMU, firmware, CXL boot or hardware work was performed or claimed.
