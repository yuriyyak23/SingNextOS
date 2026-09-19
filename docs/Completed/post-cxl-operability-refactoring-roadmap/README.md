# SingNextOS Post-CXL Operability Refactoring Roadmap

Status: implemented and qualified through Phase 12. Per-phase evidence and the final executable matrix are linked below; unsupported hardware/production claims remain explicitly FutureGated.

Post-closure completeness/correctness audit: [00_12_COMPLETENESS_CORRECTNESS_AUDIT.md](00_12_COMPLETENESS_CORRECTNESS_AUDIT.md).

## Baseline assumption

This roadmap intentionally treats the CXL, Virtualization, SecureCompute, and HybridCPU integration roadmaps as **architecturally completed prerequisites**, even when the current repository snapshot does not yet contain every implementation change described by those roadmaps.

The implementation order therefore assumes that the following semantic foundations already exist and are stable enough to consume:

- capability-native process/domain authority;
- `OwnedRegion` / MOVE / borrow / `RegionUse` ownership rules;
- provider-neutral platform mappings and device leases;
- external-operation lifecycle with explicit admission, submission, completion, visibility, publication, closure, and reclaim barriers;
- CXL.io / CXL.mem / CXL.cache integration below existing SingNextOS authority;
- VirtualDomain and SecureDomain composition, including exact generation-bound guest/security mappings;
- HybridCPU-v2 external runtime integration where replay/evidence remain distinct from authority;
- fail-closed reset, reconfiguration, stale-generation and ambiguous-effect handling.

If implementation work later discovers that one of these prerequisites is absent in code, that missing prerequisite is fixed in its owning earlier roadmap; this roadmap must not weaken its contracts to compensate.

## Objective

After the hardware-facing CXL / virtualization / SecureCompute cycle, the next objective is to make SingNextOS an operationally useful, diagnosable, composable capability-native OS rather than only a strong kernel/runtime architecture.

The work prioritizes high-value, medium/low-cost improvements that reuse existing authority and lifecycle machinery instead of introducing new authority roots.

## Mandatory design invariants

- authority is explicit, typed, scoped, revocable, and generation-bound;
- observation, evidence, trace and replay records never become authority;
- restart creates a new service/process generation and never silently inherits stale external authority;
- deadlines and cancellation do not imply that a submitted external effect disappeared;
- budgets constrain admission; they do not become execution capabilities;
- manifests request policy and resources; they do not mint authority;
- zero-copy is an implementation optimization, never an IPC ABI guarantee;
- checkpoint data is not a live provider lease and cannot restore unsupported external authority;
- telemetry projections are policy-controlled observations, not ambient host introspection;
- provider conformance proves lifecycle semantics, including ambiguous acceptance and exact closure, not only happy-path behavior;
- HybridCPU replay certificates remain replay evidence, not OS permission;
- CXL/virtualization/SecureCompute identities remain below the relevant generic SingNextOS authority abstractions unless an exact typed diagnostic projection is explicitly authorized.

## Phase index

0. `00-TECHNICAL-SPECIFICATION.md` — expanded technical specification, architecture, contracts, requirements and global acceptance criteria.
   Implemented current-state ownership and vocabulary: `00-current-state-mapping-and-operability-rules.md`.
1. `01-foundation-contracts-and-declarative-manifests.md` — shared lifecycle vocabulary, component/service manifests, feature requirements, dependency declarations and schema validation.
   Implemented with typed admission decisions and deterministic manifest correlation; see `01_PHASE_01_IMPLEMENTATION_EVIDENCE.md`.
2. `02-capability-aware-service-supervisor.md` — service lifecycle, dependency graph, health, restart/drain/replace and authority-safe recovery.
   Implemented with capability-gated mutations, exact dependency generations, bounded restart and quarantine-safe replacement; see `02_PHASE_02_IMPLEMENTATION_EVIDENCE.md`.
3. `03-authority-inspector-and-provenance-graph.md` — read-only authority graph, pin/reclaim diagnostics, delegation provenance and causal inspection.
   Implemented as self/system-scoped detached projections with typed `Why*` reasons and provider-private redaction; see `03_PHASE_03_IMPLEMENTATION_EVIDENCE.md`.
4. `04-typed-deadlines-cancellation-and-timeouts.md` — unified cancellation/deadline contracts across IPC, external effects, devices, compute and teardown.
   Implemented with generation-bound monotonic scopes, typed IPC wait cancellation, ExternalOperation closure mapping and drain timeout policies; see `04_PHASE_04_IMPLEMENTATION_EVIDENCE.md`.
5. `05-resource-budgets-quotas-and-admission-qos.md` — hierarchical budgets, quotas, reservations, admission policy and non-authoritative scheduling hints.
   Implemented with an exact System → Service → Process/Domain → Operation reservation ledger and managed-path enforcement for memory, IPC and external operations; see `05_PHASE_05_IMPLEMENTATION_EVIDENCE.md`.
6. `06-deterministic-tracing-and-os-boundary-replay.md` — causal tracing, deterministic event correlation, replay evidence and divergence diagnostics.
   Implemented with bounded per-producer sequences, explicit overflow, metadata-only causal events and non-authoritative diagnostic/model/external-runtime replay; see `06_PHASE_06_IMPLEMENTATION_EVIDENCE.md`.
7. `07-safe-high-performance-ipc-v2.md` — typed IPC, MOVE/borrow, scatter-gather, bounded fast paths and proven zero-copy optimization.
   Implemented as a transport-neutral semantic façade over the existing exact ownership/channel authorities, with bounded copy/scatter-gather and no zero-copy ABI claim; see `07_PHASE_07_IMPLEMENTATION_EVIDENCE.md`.
8. `08-ordinary-domain-checkpoint-and-restore.md` — checkpoint of ordinary logical SIP/domain state with explicit exclusion of unsupported external/private state.
   Implemented as capability-gated ordinary-state checkpoint/restore with explicit resource classification, image integrity, system-ledger storage reservation and fresh-generation admission; see `08_PHASE_08_IMPLEMENTATION_EVIDENCE.md`.
9. `09-structured-telemetry-and-evidence-projection.md` — typed telemetry, projection policy, visibility classes and host-evidence non-leak.
   Implemented with manifest/capability-scoped typed snapshots, budgeted generation-bound bounded subscriptions, supervisor integration, and strict reuse of the separate security-evidence path; see `09_PHASE_09_IMPLEMENTATION_EVIDENCE.md`.
10. `10-provider-conformance-and-fault-injection-framework.md` — reusable provider semantic qualification, deterministic fault injection and recovery-token testing.
   Implemented as a test-only reusable semantic suite across the generic ExternalOperation model and HybridCPU executable-adapter boundary, including deterministic fault matrices, exact containment/reclaim checks, and prerequisite closure hardening; see `10_PHASE_10_IMPLEMENTATION_EVIDENCE.md`.
11. `11-cross-cutting-integration-performance-and-hardening.md` — integration of supervisor, IPC, budgets, trace, telemetry and checkpoint paths; performance and abuse resistance.
   Implemented with end-to-end managed lifecycle/fault/deadline scenarios, bounded abuse tests, provider-loss trace correlation, and executable diagnostic baselines; see `11_PHASE_11_IMPLEMENTATION_EVIDENCE.md`.
12. `12-pr-slicing-validation-and-exit-criteria.md` — implementation order, PR slicing, migration rules, negative-test matrix and final acceptance gates.
   Closed by the executable roadmap matrix and full-solution qualification; see `12_FINAL_QUALIFICATION_MATRIX.md` and `12_PHASE_12_IMPLEMENTATION_EVIDENCE.md`.

## Intended end state

```text
Declarative Service Manifest
        -> Supervisor admission
        -> capability + budget materialization
        -> typed IPC / owned-region dataflow
        -> deadline/cancellation-aware operations
        -> provider-neutral external effects
        -> causal trace + structured telemetry
        -> authority inspector / provenance diagnostics
        -> optional ordinary checkpoint
        -> deterministic teardown or generation-changing restart
```

The target is not a second policy layer beside the kernel. The target is a thin operational layer that composes the authority, ownership and external-effect invariants already established by SingNextOS and HybridCPU-v2.
