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
HybridCPU.ExternalRuntime.Contracts 1.14.0
HybridCPU.ExternalRuntime 1.6.0
HybridCPU compiler/runtime contract v6
```

### Additive v6 sidecars

The current package vocabulary above follows the executable adapter project and its
lock file. It is dependency identity, not executable coverage or permission. Exact
package/assembly hashes, consumers and contour qualification remain required; historical
baseline package tuples are not current qualification identities. All new v6 gates remain OFF.

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

### C0/P04 unknown cancellation admission — 2026-10-02

Unknown ExternalCancellationSupport is rejected by the existing ExternalOperationAuthority before acquiring Region uses or storing admission. Both kernel entry points (public admission and virtual/exact-mapping admission) use the same owner-hosted pure structural validator before binding cancellation consumers, recording cancellation outcome or reserving an attached budget. This is mandatory contract validation, not a second authority or cancellation ledger. Principal/operation authorization and exact generation resolution remain independent; the validator cannot grant permission. Three unknown enum boundaries, both kernel routes, active/requested scopes, direct owner/exact-mapping owner calls, unchanged owner history/Region uses/scope sequence/budget usage and subsequent supported admission are tested. BeforeSubmissionOnly and ProviderCooperative remain compatible; no silent fallback or stronger assurance is invented. Named managed admission guard RuntimeEnforced; physical closure FutureGated; gates OFF, public V1 APIs/enums/packages/schema unchanged, Java excluded, ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-c0-cancellation-admission.

### C0 channel identity/sequence exhaustion — 2026-10-02

Existing ChannelRegistry refuses exhausted identity allocation (0 or terminal ulong.MaxValue sentinel) before inserting a record; its sole kernel caller propagates existing CapacityExhausted without process/response attachment. Internal Create now returns KernelResult; public V1 APIs/enums/packages/schema are unchanged. Queued send and copied/borrow/move inline admission refuse sequence ulong.MaxValue before any Region loan/transfer, payload mutation, queue enqueue or protocol transition. A final sequence ulong.MaxValue can commit once from MaxValue-1; it cannot wrap. Caller authorization, protocol/payload/capability checks remain independent. Existing kernel gates serialize real admission contours; standalone owner-global concurrency is not newly qualified. All seven queued/inline shapes, pair Borrow+Consume, ordinary/max boundaries, direct owner creation, sampled competing final identity and provisional IPC budget refund are tested. Initial new fixture failures (wrong ownership namespace/undersized IPC budget) are retained and corrected against actual contracts. Named managed guard RuntimeEnforced; physical FutureGated; gates OFF; Java excluded; ISA/opcode/CPU NONE. Evidence: artifacts/v6/iteration-20261002-c0-channel-counter-exhaustion.
