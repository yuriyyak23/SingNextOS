# SingNextOS ↔ HybridCPU semantic co-design refactoring — corrected implementation roadmap

**Audit/update baseline:** 2026-09-22T01:23:00+03:00  
**SingNextOS:** `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e` (tree `b0a9e343df10a99e26adddbe57669ed2533d1b8e`)  
**HybridCPU-v2:** `794c4a53494f503855ac8cf209efab23fde083b2` (tree `189f59a4b95fb822618117c60c8224bed701f9a5`)

## Status

This package is a corrected implementation roadmap. It does **not** claim that the new semantic co-design implementation exists or that tests were executed in this audit environment. Live-code inspection is authoritative; documents are subordinate.

## Non-negotiable thesis

SingNextOS defines semantic obligations. Provider/HybridCPU exposes and enforces execution guarantees. Typed refinement proves whether guarantees satisfy obligations. HybridCPU runtime legality remains independently authoritative. Before irreversible execution:

`AuthorizedBySingNext && ProviderAdmission && GuaranteesRefineObligations && HybridCpuRuntimeLegal`

## Authority boundaries

- SingNextOS: semantic/effect authority, capability derivation/revocation, invocation authority, Region ownership/use, local quantitative resource truth, ExternalOperation identity/lifecycle, publication decision/policy, reclaim.
- HybridCPU: runtime/machine-state legality, typed-slot/lane materialization, execution, retire, replay legality, runtime measurement/enforcement/evidence.
- Provider: provider-local admission/resources/environment/generation/completion/visibility mechanisms.
- Planner/Scheduler: policy, candidate selection, placement/topology/performance evidence only.

## Critical corrections applied to the previous package

1. Baseline corrected from SingNextOS `1890a8e…` to `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`.
2. P10 no longer proposes a second heterogeneous reservation mechanism: `ResourceBudgetAuthority.Reserve(IReadOnlyList<BudgetAmount>)` already provides atomic multi-dimensional reservation in the single ledger.
3. P11 removes the CPU-global `EffectEpoch` idea. Containment is an optional provider-contour close/drain receipt implemented in existing Lane6/Lane7 runtime seams only when enforceable, with ISA unchanged.
4. P07/P08 extend existing `ExternalOperationState`, `ExternalEffectPolicy`, `RegionAuthority` and `MutationEpoch`; no parallel publication/Region ledger.
5. P05 layers `SemanticExecutionBinding` above—not instead of—HybridCPU `ExternalOperationAdmissionBinding`.
6. P05 conservative 1.14.0 compatibility mapping only claims exact lifecycle/generation, independent CPU+provider admission, existing visibility/publication/cancellation/replay semantics. Missing QoS/preemption/containment remains Unsupported.
7. P12 treats compiler typed-slot facts as `ValidationOnly`; they never replace `IRuntimeLegalityService`/`LegalityDecision`.
8. P17 reuses executable MatrixTile/L7/Lane6 substrate. No new ISA/core architecture is required.
9. P14 sharding/escrow is measure-first and non-critical unless benchmarks prove the current owner implementation insufficient.
10. P16 declarative OperationContract generator is deferred; MatrixMultiply vertical no longer depends on it.

## Toolchain/package tuple

| Item | Frozen value / status |
|---|---|
| SingNextOS SDK | `11.0.100-rc.1.26425.128`, rollForward disabled |
| SingNextOS LangVersion | C# `13.0` (preview only by explicit opt-in) |
| HybridCPU-v2 SDK | `10.0.201` |
| HybridCPU Contracts project | `HybridCPU.ExternalRuntime.Contracts 1.14.0`, `net11.0` |
| ExternalOperation schema | `1.4.0` |
| Contracts nupkg Git blob | `2614d43f8a9e2dc8e0fef525c494d7023d4cc806` |
| Runtime 1.3.0 nupkg Git blob | `d528fd497f141417d18b2bbbd1310dfa0913baa9` |
| Contracts SHA-256 | `B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2` — **RECORDED_NOT_RECOMPUTED in this audit** |
| Runtime 1.3.0 SHA-256 | `191A1976DECAF607425B3F93378BA11446B32AF2EBFD09DFF45A26944E7E765F` — **RECORDED_NOT_RECOMPUTED** |
| Current executable test result | **NOT RUN**; test existence/static inspection only |

## Claim discipline

Use only: `ModelOnly`, `StaticAdmission`, `RuntimeEnforced`, `ExecutableAdapter`, `EnforcedUpperBound`, `GuaranteedReservation`, `ProductionQualified`. DTO presence never promotes a claim. Evidence never grants authority.

## ISA/core gate

**ISA impact = NONE.** ISE/runtime code may change in existing execution/admission/measurement/fence/commit seams. CPU architecture, instruction encoding, registers, pointer model and OS-specific ISA objects must not change for this roadmap.
