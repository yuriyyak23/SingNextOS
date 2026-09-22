# Audit corrections applied to the roadmap

This file records changes to the roadmap itself, not implementation completion.

- Old SingNext SHA: `1890a8e921cfe903b5b44857e4661168bf7bbceb` → current `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`.
- P10 vector reservation: reclassified from new mechanism to PARTIALLY_EXISTING; existing `Reserve(IReadOnlyList<BudgetAmount>)` is reused. Banker/escrow task is REMOVE_OR_MERGE unless a provider-local gap is demonstrated.
- P11 EffectEpoch: previous global abstraction removed. Correct replacement is provider-contour containment closure evidence implemented in existing runtime/fence/drain seams only where enforceable.
- P14 sharding/escrow: DEFERRED pending measured contention.
- P15 ManagedCap: current live tree inventory does not expose a runtime type named ManagedCap; do not use historical docs/fixture as executable evidence.
- P16 OperationContract generator: DEFERRED and removed from Matrix vertical dependency.
- P17: HybridCPU MatrixTile/Lane6/Lane7 are not model-only; executable substrate is present. Roadmap now reuses it and adds semantic binding/guarantee conformance without ISA changes.
- Publication: OS decision is preserved; HybridCPU `ExternalOperationPublicationGate` remains local staged eligibility. No “CPU owns PublishPermit”.
- Typed-slot facts: explicitly stay `ValidationOnly`; no compiler-evidence promotion to runtime legality.
