# Corrected Cross-Project Contracts — SingNextOS ↔ HybridCPU-v2

## 1. Responsibility split

```text
SingNextOS:
  permission / effect authority
  Region ownership/use
  resource accounting
  semantic obligations
  publication/reclaim

Provider / ExternalRuntime:
  provider-local admission
  concrete resource availability
  device/IOMMU/provider generations
  completion/visibility/containment evidence

HybridCPU ISE/runtime:
  machine/runtime legality
  typed-slot/lane materialization
  execution + retire
  replay legality
  CPU-side measurements/evidence

Compiler:
  lowering + optional verifiable evidence
```

## 2. Direction of data

SingNextOS sends **requirements and non-authoritative correlation**; provider/HybridCPU returns **guarantees/evidence and independent legality decisions**. Neither side imports the other's authority objects.

Forbidden across the CPU/runtime ABI:

- SingNext `CapabilityId` or capability object as machine permission;
- Region ownership handles as ISA/runtime authority;
- provider topology IDs as application authority;
- compiler proof as `LegalityDecision` authority source.

## 3. Contract families

### Stable base

```text
OperationObligationsV1
ExecutionGuaranteesV1
SemanticExecutionBindingV1
HybridCPU ExternalRuntime contracts/runtime (resolve actual project/assets inputs)
HybridCPU compiler/runtime contract (resolve actual selected inputs)
```

### Additive v6 sidecars

Sidecar names below are target shapes; use existing contracts and only add a type when the actual consumer needs it. Package presence/version is not executable coverage or permission. Resolve dev dependency identity from actual project/assets/global inputs; exact artifact integrity and deployment qualification follow the applicable claim. All new v6 gates remain OFF.

```text
OperationSemanticExtensionsV1
ExecutionGuaranteeExtensionsV1
SemanticBindingExtensionSetV1
MemorySemanticsV1
DmaExecutionBindingV1
TemporalSemanticsV1
PreemptionGuaranteeV1
PersistenceSemanticsV1
TrustObligation/TrustEvidenceV1
FailureConsequenceV1
LocalityCostEstimateV1
ProviderEnergyEvidenceV1
```

Only sidecars used by a selected contour need to exist in its first PRs.

## 4. Version/canonicalization rules

- deterministic canonical serialization;
- schema ID + schema version for each sidecar;
- canonical ordering of extension classes;
- mandatory/optional bit is covered by digest;
- unknown mandatory class denies;
- package version is not enough — source/digest/schema tuple is recorded;
- no in-place reinterpretation of old enum values;
- changed live generation requires fresh runtime check even when schema is unchanged.

## 5. Independent-gate rule

The adapter SHALL expose enough information to keep these decisions independent:

```text
SingNext authorization result
provider admission result/generation
refinement result
HybridCPU LegalityDecision
```

A convenience API may aggregate them for diagnostics but may not collapse them into one mutable authority store.

## 6. Submit linearization

The final SingNext dependency revalidation occurs immediately before the existing external-operation/provider submit commit. The system records an `EffectPossible`-equivalent state before any callback that may create an irreversible external effect. Provider execution occurs outside unrelated owner locks.

## 7. Completion/publication

Provider completion is never publication. The adapter must expose explicit visibility/containment semantics or force a conservative staged barrier before SingNext publication.

## 8. Replay

HybridCPU machine replay and SingNext semantic/external replay are different facts. Reuse of replay evidence cannot bypass changed SingNext/provider generations.

## 9. Contract claim ceiling

Adding a contract type is `ModelOnly`/`StaticAdmission` evidence at most. `ExecutableAdapter` requires a concrete executing adapter and negative/fault tests.

## Existing bounded contract guards

Unknown cancellation support fails closed before owner/resource admission; channel identity/sequence exhaustion refuses before partial allocation or transfer. Recorded details: [cancellation](../../artifacts/v6/iteration-20261002-c0-cancellation-admission/audit.md), [channel counters](../../artifacts/v6/iteration-20261002-c0-channel-counter-exhaustion/report.md). These are implemented cuts, not new dispatch; inspect the report and actual caller only if affected.
