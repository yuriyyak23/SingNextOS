# Phase 13 — Migration Order, PR Slicing, and Exit Criteria

## Goal

Sequence implementation so HybridCPU-v2 and compiler integration can be reviewed and landed incrementally without mixing authority, replay, compiler, and provider changes in one large patch.

## Recommended PR sequence in HybridCPU-v2

### PR A — Contract-only foundation

- add provider-neutral ExternalRuntime contract types;
- add lifecycle/effect/generation enums/records;
- add serialization/equality/version tests;
- no lane/runtime behavior change.

### PR B — ExternalRuntime adapter skeleton

- add SingNextOS adapter interface and fake implementation;
- add lifecycle state machine and correlation checks;
- keep production backend disabled by default.

### PR C — Legality/guard integration

- add external admission/binding checks after existing CPU legality/GuardPlane gates;
- add separate external rejection taxonomy;
- add stale-generation and guard-before-reuse tests.

### PR D — Replay/effect integration

- attach effect/lifecycle identity to external tokens;
- prevent replay duplicate submit;
- add barrier/idempotence policy and invalidation tests.

### PR E — L7-SDC migration

- route L7 external accelerator execution through ExternalRuntime;
- preserve lane7 `SystemSingleton`;
- extend token lifecycle for admission/visibility/publication;
- retain staged output as default.

### PR F — DSC migration

- route relevant lane6 DSC execution through ExternalRuntime;
- preserve distinct DSC ABI/runtime/retire semantics;
- add DMA/mapping-generation tests.

### PR G — Commit/fence semantics

- require visibility before staged publication;
- consume publication-validity receipts;
- refactor fence wait/cancel/drain modes.

## Recommended PR sequence in `Compilers/`

### PR H — Semantic IR

- add external execution/memory/publication intent;
- propagate through analyses;
- no new CXL resource or slot classes.

### PR I — Lowering and bundle admission

- lower intent to lane6/lane7 existing carriers;
- extend structural descriptor metadata;
- preserve runtime-only provider checks.

### PR J — Platform/runtime handshake

- wire emitted semantic contract version and descriptor identity through `CpuInterfaceBridge`, managed runtime/platform contracts as required;
- keep live SingNextOS provider selection in ExternalRuntime/OS.

### PR K — End-to-end conformance

- compile-to-runtime fake SingNextOS tests;
- real SingNextOS service tests;
- CXL Type-3 ordinary-memory tests;
- Type-2 staged accelerator path tests when backend exists.

## Dependency rules

Do not start L7/DSC production migration before contract, legality separation, and replay duplicate-submit prevention are merged.

Do not enable direct coherent output before:

- staged path is stable;
- visibility/publication receipts exist;
- reset/reconfiguration tests pass;
- replay effect policy is explicit;
- SingNextOS provider proves required access/coherence semantics.

Do not make compiler CXL-specific to work around an incomplete runtime contract. Extend semantic runtime contracts first.

## Compatibility strategy

During migration:

- retain existing fake/local backends behind explicit test/development configuration;
- add contract version negotiation;
- allow old descriptors only where semantics are unambiguous;
- reject rather than guess when descriptor/runtime contract versions disagree;
- preserve lane widths/slot classes unless a separate ISA proposal proves a real architectural need.

## Project-level exit criteria

The HybridCPU-v2/CXL integration is considered complete for the first production contour when all of the following hold:

1. compiler emits only provider-neutral semantic intent;
2. CPU core contains no raw CXL topology identities;
3. L7-SDC uses SingNextOS external admission/execution for the supported accelerator contour;
4. DSC uses OS-mediated bindings for the supported DMA/external contour;
5. CPU legality and OS authority are independent gates;
6. replay cannot duplicate external side effects;
7. `DeviceComplete`, `Visible`, `Published`, and `Released` are distinguishable;
8. stale generation/reset/reconfiguration fail closed;
9. staged publication is the default write path;
10. CXL Type-3 memory works through ordinary memory semantics;
11. direct coherent output remains disabled unless all stronger proof gates pass;
12. negative/conformance suites pass against fake and supported real SingNextOS backends.

## Non-goal at roadmap completion

Completion does not require every CXL 4.x optional topology/performance feature to become visible to HybridCPU. New fabric capabilities should remain OS/provider concerns unless they create a genuinely new program-visible semantic requirement.