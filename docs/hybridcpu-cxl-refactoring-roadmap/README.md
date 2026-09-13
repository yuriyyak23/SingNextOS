# HybridCPU-v2 + Compiler CXL 3.x/4.x Refactoring Roadmap

Status: proposed cross-project implementation roadmap stored in SingNextOS docs.

## Scope

This roadmap covers only future changes in:

- `yuriyyak23/HybridCPU-v2`;
- `HybridCPU-v2/Compilers`.

It defines how HybridCPU-v2 and its compiler should consume the SingNextOS CXL 3.x/4.x substrate documented in `../cxl-refactoring-roadmap/`.

This roadmap does **not** schedule additional SingNextOS implementation work. SingNextOS is treated as the external authority/runtime provider whose stable contracts are consumed by HybridCPU-v2.

## Architectural direction

CXL is **not** introduced as a new CPU lane, ISA execution class, replay authority, or application-visible transport identity.

The intended dependency is:

```text
HybridCPU compiler semantic intent
        -> HybridCPU ISA / descriptor carriers
        -> HybridCPU runtime legality + replay model
        -> HybridCPU ExternalRuntime bridge
        -> SingNextOS external-operation contract
        -> SingNextOS provider selection / authority / CXL backend
        -> CXL.io / CXL.cache / CXL.mem hardware path
```

The compiler and CPU may express semantic constraints such as:

- external execution required;
- staged publication required/preferred;
- coherent access required/optional;
- device-readable / device-writable region intent;
- replay effect classification;
- cancellation or idempotence requirements.

They must not encode raw CXL topology such as HDM decoder IDs, DPA, switch routes, CXL port IDs, FM bindings, or fabric-internal identities.

## Mandatory invariants

- CXL does not become a new `LegalityAuthoritySource` merely because it is the transport/provider.
- SingNextOS admission receipts are runtime authority inputs; replay certificates remain replay/reuse evidence, not OS authority.
- coherence != ownership.
- coherence != publication.
- device completion != visibility.
- visibility != architectural publication.
- publication != ownership return unless the operation contract explicitly says so.
- replay certificate != permission to re-submit an external side effect.
- `GuardPlane` checks remain before reuse/materialization where existing HybridCPU contracts require them.
- stale OS/provider generation must fail closed before the next external hardware effect or before architectural publication, according to lifecycle stage.
- direct coherent writes are not the default optimization path.
- zero-copy is a proven optimization, not an ABI promise.
- fault/reset/reconfiguration are first-class execution outcomes.

## Existing HybridCPU-v2 mechanisms to preserve and extend

The roadmap is built around current executable mechanisms, including:

- `LegalityDecision` / `LegalityAuthoritySource`;
- `GuardPlane` and guard-before-reuse;
- structural/replay certificates;
- `LoopBuffer` / replay phase / `ReplayToken` rollback contour;
- lane6 `DmaStreamCompute`;
- lane7 `SystemSingleton` / L7-SDC;
- external accelerator descriptor, guard, token, fence and commit models;
- `AcceleratorCommitCoordinator` staged publication;
- mapping/domain epoch checks already modeled around external accelerators;
- retire/publication separation;
- `HybridCPU_ExternalRuntime` and its contracts;
- compiler IR slot/resource classes, typed-slot admission and bundle construction.

## Phase index

1. `00-baseline-gap-matrix-and-decisions.md` — freeze code-grounded boundaries and non-goals.
2. `01-cross-project-runtime-contract.md` — define the HybridCPU-facing SingNextOS external-operation ABI.
3. `02-legality-guardplane-and-authority.md` — refactor legality so OS admission is consumed without creating CXL authority inside CPU legality.
4. `03-replay-effect-model-and-invalidation.md` — connect SingNextOS lifecycle/effect receipts to replay and deterministic execution.
5. `04-lane7-l7-sdc-external-operation-bridge.md` — migrate L7-SDC to provider-neutral SingNextOS external execution.
6. `05-lane6-dmastreamcompute-integration.md` — align DSC with OS-mediated DMA/external execution without merging lane6 and lane7 semantics.
7. `06-commit-visibility-publication-and-fences.md` — refactor commit/fence behavior around DeviceComplete/Visible/Published.
8. `07-compiler-ir-semantic-intent.md` — add provider-neutral external-memory/execution intent to compiler IR.
9. `08-compiler-lowering-admission-and-bundling.md` — lower intent to existing carriers and preserve typed-slot legality.
10. `09-externalruntime-singnextos-adapter.md` — implement the runtime bridge and generation/effect receipts.
11. `10-cxl-memory-and-coherent-access-semantics.md` — define compiler/CPU behavior for CXL-backed memory without exposing CXL topology.
12. `11-fault-reset-reconfiguration-and-cancellation.md` — first-class stale/reset/fabric-change handling.
13. `12-validation-negative-tests-and-conformance.md` — executable acceptance matrix.
14. `13-migration-order-pr-slicing-and-exit-criteria.md` — implementation sequence and PR slicing.

## End state

The desired end state is:

```text
source program
  -> compiler emits semantic external-operation intent
  -> existing lane6/lane7 carriers where appropriate
  -> HybridCPU guards/replay determine CPU-side admissibility
  -> ExternalRuntime asks SingNextOS to admit/materialize operation
  -> SingNextOS returns opaque lifecycle/effect/generation receipts
  -> HybridCPU waits for visibility/publication semantics
  -> architectural retirement/publication occurs only when both CPU and OS contracts permit it
```

No CXL-specific application authority or topology is added to the CPU ISA.
