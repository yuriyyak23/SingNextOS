# P01 evidence — resource model and dimensional algebra

## Disposition

P01 is closed at `ModelOnly` for immutable contracts. No mutable owner, handle, reservation, grant minting path, admission path, or enabled gate was added.

## Phase snapshot

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Reviewed HEAD: `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`.
- Entry dirty worktree contained only P00-owned additive files listed in `EVIDENCE_P00_ARCHITECTURAL_FREEZE.md`; no user-owned baseline/P14 file was changed.
- Applied invariants: VNX-002–006, VNX-013, VNX-023–024. VNX-003 live-ledger integration remains P02 FutureGated.

## Contract and truth table

`ResourceEnvelopeV1` distinguishes time, throughput and occupancy by closed family/class/unit tuples. The initial class is `ComputeTime` in canonical nanoseconds with an exact semantic scope. Throughput requires an explicit nonzero nanosecond window; occupancy forbids a window. No cross-family conversion or universal scalar exists.

`ResourceUseConstraintV1` is an immutable descriptor for future P02 integration. It has version, envelope, validity, assurance ceiling and delegation depth. It contains no authority identity and cannot be minted or validated as live authority.

| Concept | P01 representation | Authority/mutation |
|---|---|---|
| accounting | existing budget contracts only | unchanged |
| resource permission | pure `ResourceUseConstraintV1` model | no live grant until P02 |
| reservation/lease/settlement | none added | remains `ResourceBudgetAuthority`; P03 FutureGated |
| guarantee | `ResourceAssuranceV1` vocabulary only | no guarantee gate or claim |
| provider evidence | none added | no provider claim |

## Fail-closed behavior and regression coverage

- Version and every enum are closed; unknown values fail canonicalization/subset checks.
- Amount zero and `ulong.MaxValue` are rejected rather than treated as ambiguous sentinels.
- Window multiplication uses checked arithmetic and overflow is executable-tested.
- Family/class/unit/window/scope mismatch fails subset and canonicalization.
- Subset is reflexive/transitive for generated narrowing chains; amount, validity, assurance and delegation depth may only narrow.
- Two valid siblings cannot be combined into an envelope exceeding the parent; no union API exists.
- JSON round-trip must re-enter canonicalization before use.

## Commands and results

| Command | Result |
|---|---|
| P01 focused test | 11 passed, 0 failed |
| P01 + existing constraint/budget regression lane | 32 passed, 0 failed |
| affected Contracts build | succeeded, 0 warnings, 0 errors |
| full solution build | succeeded, 0 warnings, 0 errors |
| full non-GUI suite | failed: 1579 passed, 9 failed, 2 skipped; same nine pre-existing identities recorded by P00 |

The repeated full-suite failures are unchanged historical evidence/profile/P14 tuple drift and do not run in the P01 test class. They continue to block any full-suite or production claim.

## Gates, future work, and exclusions

- Enabled `FG-VNX-*`: none.
- Exact claim: `ModelOnly` immutable dimensional algebra on host/JIT.
- `FG-VNX-RESOURCE-GRANT` remains OFF because `CapabilityAuthority` has no live resource-use constraint integration yet; treating the DTO as authority would bypass realm/generation/revocation checks.
- `FG-VNX-RESOURCE-LEASE` and all provider/temporal/non-compute gates remain OFF because no quantitative lease or executable adapter contour was added.
- Ordinary SIP/Compute/ExternalOperation paths are unchanged. Plans, caches, receipts and telemetry remain non-authoritative.
- No HybridCPU ISA/microarchitecture, NativeAOT, provider, QEMU, firmware, CXL boot or hardware work was performed or claimed.
