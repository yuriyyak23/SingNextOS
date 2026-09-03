# SingNextOS ↔ HybridCPU-v2 Refactoring Roadmap

## Purpose

This folder turns the cross-repository architecture audit into an implementation-ordered refactoring plan for SingNextOS. It is intentionally **not** a feature wish list and not a redesign around Win32, POSIX, VMX, a universal HAL, global shared memory, or a giant syscall ABI.

The target remains:

```text
.NET-like source-familiar API
  -> generated typed SIP contracts
  -> capabilities + ownership IPC
  -> minimal privileged Sing+ authority
  -> Platform Authority Bridge
  -> neutral HybridCPU execution / memory / I/O domains
  -> concrete CPU / MMU / IOMMU / DMA / accelerator mechanisms
```

Compatibility projections such as VMX/VMCS, POSIX, Win32 or Wine stay downstream personalities. They do not define the authority model.

## Baseline used by this roadmap

This plan is finalized against:

- `SingNextOS/master` = `35c66f18e2cabcc1695d64c8ccb834faa9551b6a`;
- Track A merge = `7977043e98db50673d4d7053c6ddcb9f1beea91b`;
- merged Track A feature commit = `e0387949a1ec2aa9f6858de059de7b18b91e66c7` (`runtime: complete Track A client response adapter teardown`);
- `HybridCPU-v2/master` = `38bf0614d8a58e2543b4a956ccc23bb22e1a8170`.

`35c66f18...` directly descends from the Track A merge and adds the subsequent `fix 33` solution-file update. The roadmap therefore incorporates the requested feature-branch teardown state and is rebased on the newer master that appeared while the plan was being assembled.

The Track A merge matters. Current master already contains:

- `ISipClientRuntimeTransport`;
- generated runtime client adapters;
- exact correlated async response waiting;
- channel-close cancellation of pending response waiters;
- process-level channel teardown even when a `DomainId` is still shared;
- tests that preserve committed publication when a peer terminates after publication.

Therefore this roadmap treats SIP response/client teardown as **current foundation**, not as an unimplemented first phase.

`docs/whitebook/hybridcpu-ise/09_DEVELOPMENT_DIRECTION.md` still describes Track A as incomplete from an older baseline. Its invariants remain useful, but its status ordering must be rebased to the current master as part of Phase 0.

## Architectural verdict carried forward from the audit

The two projects are architecturally compatible if the integration boundary remains narrow:

```text
local Sing capability
AND local resource/generation/protocol state
AND live opaque platform lease
AND HybridCPU runtime admission
  -> hardware/runtime-visible effect
```

The main missing element is not a new ISA or a new OS object model. It is a production-quality authority bridge that materializes Sing-local authority into live HybridCPU execution/memory/I/O authority and returns trustworthy completion/revocation state.

## Phase order

| Phase | File | Primary outcome |
|---|---|---|
| 0 | [`00_BASELINE_AND_INVARIANTS.md`](00_BASELINE_AND_INVARIANTS.md) | Rebase docs/tests on post-Track-A master and freeze non-negotiable authority invariants. |
| 1 | [`01_PLATFORM_CONTRACT_VNEXT.md`](01_PLATFORM_CONTRACT_VNEXT.md) | Versioned feature discovery, opaque leases and completion receipts without creating a universal HAL. |
| 2 | [`02_REVOCATION_TEARDOWN_COMPLETION.md`](02_REVOCATION_TEARDOWN_COMPLETION.md) | Make local-revoked vs platform-draining vs platform-closed explicit and reclaim-safe. |
| 3 | [`03_REAL_HYBRIDCPU_DOMAIN_BINDING.md`](03_REAL_HYBRIDCPU_DOMAIN_BINDING.md) | First real Sing domain ↔ neutral HybridCPU execution/memory/I/O binding. |
| 4 | [`04_MEMORY_OWNERSHIP_COHERENCE.md`](04_MEMORY_OWNERSHIP_COHERENCE.md) | Exact region slices, non-coherent-safe handoff, revoke/rebind and zero-copy-as-optimization semantics. |
| 5 | [`05_DEVICE_DMA_IRQ_IO.md`](05_DEVICE_DMA_IRQ_IO.md) | Device/MMIO/IRQ/DMA authority as bounded grants, never ambient device access. |
| 6 | [`06_EXECUTION_SCHEDULER_EVENTS_BOOT.md`](06_EXECUTION_SCHEDULER_EVENTS_BOOT.md) | Domain lifecycle/budget/event integration plus external AOT/ISE qualification. |
| 7 | [`07_COMPUTE_ACCELERATORS.md`](07_COMPUTE_ACCELERATORS.md) | One narrow semantic DSC1/MatrixTile/L7 provider over owned regions and completion. |
| 8 | [`08_VIRTUALIZATION_NESTED_DOMAINS.md`](08_VIRTUALIZATION_NESTED_DOMAINS.md) | Neutral `VirtualizationService` over child domains; VMX remains a compatibility projection. |
| 9 | [`09_EVIDENCE_SECURECOMPUTE.md`](09_EVIDENCE_SECURECOMPUTE.md) | Classified evidence and fail-closed SecureCompute feature gating. |
| 10 | [`10_NATIVE_API_SYSTEM_SERVICES.md`](10_NATIVE_API_SYSTEM_SERVICES.md) | Filesystem/network/process/device/virt APIs as source-familiar libraries over typed SIP. |
| 11 | [`11_GUI_GPU_DISPLAY_STRESS_TEST.md`](11_GUI_GPU_DISPLAY_STRESS_TEST.md) | Surface ownership/Present/compositor/display contracts as the cross-layer stress test. |
| 12 | [`12_CONFORMANCE_AND_MIGRATION.md`](12_CONFORMANCE_AND_MIGRATION.md) | Negative conformance, claim taxonomy, migration gates and Definition of Done. |

## Gap-to-phase map

| Area | Current SingNextOS | Current HybridCPU-v2 | Planned closure |
|---|---|---|---|
| execution domains | process-exact v2 platform binding with synchronous typed Start/Park/Resume plus binding-scoped `ExecutionPolicy` v1 on the host model | neutral execution lifecycle; no stable scheduler-policy API | Phases 1, 3, 6 |
| memory domains | strong region ownership/generation; minimal platform mapping | bounded memory/address-space domain model | Phases 1, 3, 4 |
| mapping/remap | host-backed exact owned-region mapping abstraction | translation/invalidation mechanisms; no generic atomic ownership remap proof | Phase 4 |
| ownership | `OwnedRegion`/`OwnedBuffer`, MOVE, generation | domain/mapping authority, not a duplicate OS ownership object | Preserve Sing ownership; adapt in Phase 4 |
| borrow/shared grants | local revocable borrow | bounded platform permissions, no generic Sing borrow object needed | Phase 4 |
| DMA | bounded local v5 submit/completion/visibility/closure model plus generation-bound completion notification; no executable HybridCPU DMA | neutral admission-only grant and separately pinned visibility evidence; no neutral submit/completion/cancel API | Phases 5, 6 |
| interrupts/events | exact HybridCPU IRQ binding feeds the local `KernelEventEndpoint`; model DMA completion is the second producer | neutral IRQ binding exists; no generic timer/event or DMA-completion surface | Phases 5, 6 |
| accelerators | external requirement only | MatrixTile, DSC1 and scoped L7 paths are code-confirmed | Phase 7 |
| coherence/fences | no global-coherence assumption | explicit non-coherent fence requirements exist; universal coherence not proven | Phases 1, 4, 5 |
| virtualization | local target only | neutral domain substrate + VMX projection; backend incomplete | Phase 8 |
| nested domains | no provider | neutral nested-domain composition exists | Phase 8 |
| SecureCompute | fail-closed target | descriptor/policy architecture, not production-positive backend | Phase 9 |
| evidence | no provider API | evidence/visibility concepts exist; hardware-rooted attestation not proven | Phase 9 |
| GPU/display | no materialized service/provider | generic DMA/accelerator primitives, no proven display path | Phase 11 |
| boot/AOT | native entry/build/admission only | external toolchain/ISE is required | Phase 6 |
| scheduler interaction | typed budget/priority/latency/throughput intent produces an exact local registration only after host `ModelOnly` acceptance; no HybridCPU enforcement claim | scheduling budget and lane legality remain HybridCPU-owned; external policy admission is unavailable | Phase 6 |
| feature discovery | versioned semantic manifest plus legacy flags; `NeutralDomains` is v2 | richer hardware/runtime/domain states exist internally | Phase 1 foundation; version bumps remain phase-local |

## Minimal high-value implementation slice

The highest architectural return comes from completing **Phases 1–5 before broad system-service work**:

```text
Platform Contract vNext
  -> explicit revocation/completion lifecycle
  -> real neutral HybridCPU domain binding
  -> exact non-coherent-safe region mapping
  -> one bounded DMA vertical slice
```

That slice proves the hardest shared invariants: authority intersection, stale generation rejection, drain-before-reclaim, memory visibility, device isolation and provider truthfulness. Filesystem, GUI or VM management built before this slice would otherwise risk creating alternative authority paths that later have to be removed.

## Global rules for every phase

1. **Code/tests outrank documentation.** Documentation may describe target shapes, but it must not upgrade a model-only or projection-only mechanism into a production claim.
2. **No identifier collapse.** `CapabilityId`, `DomainId`, `RegionHandle`, provider lease IDs, HybridCPU domain IDs/epochs, IOMMU bindings and accelerator tokens remain distinct namespaces.
3. **No raw platform authority in SIPs.** Provider tokens, physical addresses, VMCS state, lane IDs and raw opcode encodings stay bridge-private.
4. **MOVE means authority transfer, not guaranteed no-copy.** Physical zero-copy/remap is a negotiated implementation optimization.
5. **No global coherence premise.** Every CPU↔device/accelerator/display handoff must have explicit visibility/completion semantics.
6. **Evidence is not authority.** Diagnostics/attestation objects are read-only observations and are never accepted as grants.
7. **Compatibility is downstream.** VMX/VMCS, POSIX, Win32 and Wine are projections/personalities over native contracts.
8. **Kernel ABI stays minimal.** Domains, capabilities, ownership, channels/events, platform mappings/grants and completion belong near the privileged boundary; pathname/socket/window/GPU/VMCS policy does not.
