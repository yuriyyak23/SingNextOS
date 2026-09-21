# Minimal Cross-Repository Delta

## SingNextOS — required
- Add provider-neutral obligation/refinement vocabulary to `SingPlus.Contracts` after P02 semantics freeze.
- Extend existing ExternalOperation record with semantic binding reference; do not add a second external-operation ledger.
- Extend existing resource admission path for vector envelopes and measurement contracts.
- Add explicit staged publication decision record/epoch tied to exact operation generation; it is not a capability.
- Extend cancellation/failure/visibility taxonomy only where current enums cannot express the required class.
- Add default-off feature gates and evidence matrices.

## HybridCPU-v2 — required only for Level-2/3 contours that need stronger claims
Current 1.14.0 already supplies exact external operation requests, generation snapshots, CPU guard+provider admission binding, staged publication validation, replay non-authority and cancellation acknowledgement. Additive delta:
- provider-neutral guarantee descriptor/version;
- exact semantic binding correlation;
- explicit measurement/retire evidence contracts where enforceable;
- bounded preemption/containment capability only for actually implemented execution classes;
- provider conformance tests and public API baseline updates.

## HybridCPU-v2 — optional
Strict effect-epoch closure, minimum capacity guarantees, contention isolation and hard cancellation bounds may remain Unsupported. SingNext then rejects obligations requiring them or uses a different provider.

## ISA
**NONE.** No changes to VLIW carrier, pointer model, capability registers, load/store/fetch, lane IDs or OS handles.
