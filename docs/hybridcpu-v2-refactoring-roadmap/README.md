# SingNextOS ↔ HybridCPU-v2 Refactoring Roadmap

## Purpose

This folder turns the cross-repository architecture audit into an
implementation-ordered refactoring plan for SingNextOS. It is intentionally
**not** a feature wish list and not a redesign around Win32, POSIX, VMX, a
universal HAL, global shared memory, or a giant syscall ABI.

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

Compatibility projections such as VMX/VMCS, POSIX, Win32 or Wine stay
downstream personalities. They do not define the authority model.

## Baseline used by this roadmap

The original finalized plan was based on:

- `SingNextOS/master` = `35c66f18e2cabcc1695d64c8ccb834faa9551b6a`;
- Track A merge = `7977043e98db50673d4d7053c6ddcb9f1beea91b`;
- merged Track A feature commit = `e0387949a1ec2aa9f6858de059de7b18b91e66c7`;
- `HybridCPU-v2/master` = `38bf0614d8a58e2543b4a956ccc23bb22e1a8170`.

Those SHAs remain historical planning context. Every implementation slice must
re-audit current `master`. Phase-7 Slice 5 starts from exact merged SingNextOS
base `6bbdffeb82ea91ec14203b482557ab2ef4ea28d5` (PR #46 merged). The relevant
external HybridCPU-v2 audit remains pinned at
`9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9` because this slice makes no
HybridCPU/compiler/ISE change.

Track A already provides typed SIP response publication, generated runtime
client adapters, exact correlated async response waiting and deterministic
channel/process teardown. Later platform/compute work composes with those
authorities rather than creating a second publication mechanism.

`docs/whitebook/hybridcpu-ise/09_DEVELOPMENT_DIRECTION.md` retains historical
context and carries a current-status overlay. This roadmap plus current
source/tests are authoritative for delivered phase status.

## Architectural verdict carried forward from the audit

The two projects are architecturally compatible only if the integration
boundary remains narrow:

```text
local Sing capability
AND local resource/generation/protocol state
AND live opaque platform lease
AND HybridCPU runtime admission
  -> hardware/runtime-visible effect
```

No one term substitutes for another. Intent, authority, evidence and published
state remain distinct.

## Phase order

| Phase | File | Primary outcome |
|---|---|---|
| 0 | [`00_BASELINE_AND_INVARIANTS.md`](00_BASELINE_AND_INVARIANTS.md) | Rebase docs/tests on post-Track-A master and freeze non-negotiable authority invariants. |
| 1 | [`01_PLATFORM_CONTRACT_VNEXT.md`](01_PLATFORM_CONTRACT_VNEXT.md) | Versioned feature discovery, opaque leases and completion receipts without creating a universal HAL. |
| 2 | [`02_REVOCATION_TEARDOWN_COMPLETION.md`](02_REVOCATION_TEARDOWN_COMPLETION.md) | Make local-revoked vs platform-draining vs platform-closed explicit and reclaim-safe. |
| 3 | [`03_REAL_HYBRIDCPU_DOMAIN_BINDING.md`](03_REAL_HYBRIDCPU_DOMAIN_BINDING.md) | First real Sing domain ↔ neutral HybridCPU execution/memory/I/O binding. |
| 4 | [`04_MEMORY_OWNERSHIP_COHERENCE.md`](04_MEMORY_OWNERSHIP_COHERENCE.md) | Exact region slices, non-coherent-safe handoff, revoke/rebind and zero-copy-as-optimization semantics. |
| 5 | [`05_DEVICE_DMA_IRQ_IO.md`](05_DEVICE_DMA_IRQ_IO.md) | Device/MMIO/IRQ/DMA authority as bounded grants, never ambient device access. |
| 6 | [`06_EXECUTION_SCHEDULER_EVENTS_BOOT.md`](06_EXECUTION_SCHEDULER_EVENTS_BOOT.md) | **Complete locally:** domain lifecycle, model policy and reusable exact event/wait integration; reproducible AOT/ISE attempt stops honestly at `EXT-HCPU-001`. |
| 7 | [`07_COMPUTE_ACCELERATORS.md`](07_COMPUTE_ACCELERATORS.md) | **Local Host-model path complete through Slice 5:** bounded DSC1 Copy lifecycle, wakeup, DMA↔DSC1 interlock, generated typed ComputeService Borrow+Consume ingress, and service→platform→ownership-response composition. Real executable HybridCPU DSC1 remains `ExternalBlocked`. |
| 8 | [`08_VIRTUALIZATION_NESTED_DOMAINS.md`](08_VIRTUALIZATION_NESTED_DOMAINS.md) | Neutral `VirtualizationService` over child domains; VMX remains a compatibility projection. |
| 9 | [`09_EVIDENCE_SECURECOMPUTE.md`](09_EVIDENCE_SECURECOMPUTE.md) | Classified evidence and fail-closed SecureCompute feature gating. |
| 10 | [`10_NATIVE_API_SYSTEM_SERVICES.md`](10_NATIVE_API_SYSTEM_SERVICES.md) | Filesystem/network/process/device/virt APIs as source-familiar libraries over typed SIP. |
| 11 | [`11_GUI_GPU_DISPLAY_STRESS_TEST.md`](11_GUI_GPU_DISPLAY_STRESS_TEST.md) | Surface ownership/Present/compositor/display contracts as the cross-layer stress test. |
| 12 | [`12_CONFORMANCE_AND_MIGRATION.md`](12_CONFORMANCE_AND_MIGRATION.md) | Negative conformance, claim taxonomy, migration gates and Definition of Done. |

## Gap-to-phase map

| Area | Current SingNextOS | Current HybridCPU-v2 | Planned closure |
|---|---|---|---|
| execution domains | process-exact v2 platform binding with synchronous typed lifecycle plus Host `ModelOnly` execution policy | neutral execution lifecycle; no stable scheduler-policy API | Phases 1, 3, 6 |
| memory domains | strong `RegionAuthority` ownership/generation plus bounded platform mapping model | bounded memory/address-space domain model | Phases 1, 3, 4 |
| mapping/remap | exact local owned-region mapping plus conservative bridge-private DMA↔DSC1 active-use exclusion | translation/invalidation mechanisms; no generic atomic ownership remap or cross-engine coherence proof | Phases 4, 7 |
| ownership | `OwnedRegion`/`OwnedBuffer`, MOVE, generation, revocable borrow; generated ComputeService transports one exact Borrow+Consume pair and composes it back through correlated ownership response | domain/mapping authority, not a duplicate OS ownership object | Preserve Sing ownership; Phases 4/7 local path complete |
| borrow/shared grants | local revocable borrow; ComputeService keeps caller source borrow live through DSC1/mapping closure and uses bounded service staging instead of inventing cross-owner external authority | bounded platform permissions; no generic Sing borrow object needed externally | Phase 4 foundation; executable direct-borrow remains external if ever required |
| DMA | bounded local submit/completion/visibility/closure model plus completion notification and conservative exclusion from simultaneous DSC1 use of one mapping; no executable HybridCPU DMA | neutral admission/visibility evidence; no neutral submit/completion/cancel or cross-engine interlock API | Phases 5–7; external closure remains |
| interrupts/events | exact IRQ plus model DMA/DSC1 terminal completion feed one generation-bound `KernelEventEndpoint` | neutral IRQ binding exists; no generic timer/event, DMA-completion or compute-completion surface | Phases 5–7 |
| accelerators | complete local typed `IComputeService.CopyAsync` → service-owned bounded staging → Host `ModelOnly` DSC1 submit/observe/cancel → mapping closure → source borrow return → destination ownership response; no Add/Mul/Fma/Reduce | DSC1 is code-confirmed internally, but no stable neutral executable compute/custody/visibility facade is supplied | Local Phase 7 through Slice 5; executable closure is `EXT-HCPU-005` |
| coherence/fences | explicit model DMA visibility boundaries and conservative whole-mapping DMA↔DSC1 exclusion; no CPU-alias mutation epoch or global-coherence assumption | explicit non-coherent requirements; cross-engine/universal coherence not proven | `EXT-HCPU-004`/`005` for executable reuse |
| virtualization | local target only | neutral domain substrate + VMX projection; backend incomplete | Phase 8 |
| nested domains | no provider | neutral nested-domain composition exists | Phase 8 |
| SecureCompute | fail-closed target | descriptor/policy architecture, not production-positive backend | Phase 9 |
| evidence | no production provider API | evidence/visibility concepts exist; hardware-rooted attestation not proven | Phase 9 |
| GPU/display | no materialized service/provider | generic DMA/accelerator primitives, no proven display path | Phase 11 |
| boot/AOT | deterministic managed kernel build/admission qualification; host boot harness is not a HybridCPU image | no published managed-assembly AOT/loader path at audited `9e001bf...` | Phase 6 complete locally with `EXT-HCPU-001` ExternalBlocked |
| scheduler interaction | semantic budget/priority/latency/throughput intent accepted only by Host `ModelOnly`; no HybridCPU enforcement claim | scheduling budget/lane legality remain HybridCPU-owned; external policy admission unavailable | Phase 6 |
| feature discovery | versioned semantic manifest and phase-specific availability classes | richer internal runtime state exists | Phase 1 foundation; version bumps remain phase-local |

The Phase-6 qualification artifacts, generated protocol digests, provider
receipts and events are evidence/reproducibility artifacts only. They do not by
themselves authorize execution, ownership or platform effects.

## Current Phase-7 local composition boundary

Phase-7 Slice 5 closes the complete Sing-local/Host-model chain:

```text
caller Compute/Execute capability
+ source ownership -> exact SIP read BorrowLease to service
+ destination ownership -> exact SIP MOVE to service
-> bounded snapshot into service-owned staging
-> service platform domain
-> exact staging Read + destination Write mappings
-> existing bounded Submit / Observe / Cancel DSC1 lifecycle
-> exact terminal settlement
-> close both mappings
-> revoke temporary mapping capabilities
-> release staging
-> return original source borrow
-> Completed: correlated destination ownership response
   Cancelled/ordinary rejection: cancelled response + local destination release
```

The bounded staging step is required because the public source remains caller-
owned while current DSC1 platform submission is single-subject/owner-bound. It
preserves the ownership model instead of reinterpreting a borrow as ownership or
inventing a cross-owner provider grant.

This closure is explicitly **Host `ModelOnly`**. It does not prove direct use of
the borrowed caller region, physical zero-copy, accelerator execution, output
cache visibility or a real neutral HybridCPU compute facade.

## Remaining Phase-7 gate

No further local operation-set expansion is justified merely because the Host
vertical path is complete. The next Phase-7 closure is externally blocked under
`EXT-HCPU-005`:

```text
real neutral Executable DSC1 Copy feature
+ exact neutral memory custody
+ bounded submit identity
+ observe/cancel/drain
+ output CPU-visibility/acquire proof
+ close-before-rebind/reclaim
```

Until that facade exists, `Add/Mul/Fma/Reduce`, DSC2 and queues remain outside
the delivered service rather than becoming speculative local breadth.

## Minimal high-value implementation slice

The highest architectural return came from completing the authority substrate
before broad system-service work:

```text
Platform Contract vNext
  -> explicit revocation/completion lifecycle
  -> neutral HybridCPU domain binding
  -> exact non-coherent-safe region mapping
  -> bounded DMA lifecycle
  -> scheduler/event integration
  -> one narrow typed ComputeService / DSC1 local vertical path
```

That order prevents filesystem, GUI or compatibility work from creating
alternative authority paths that later have to be removed.

## Global rules for every phase

1. **Code/tests outrank documentation.** Documentation may describe target shapes, but it must not upgrade a model-only or projection-only mechanism into a production claim.
2. **No identifier collapse.** `CapabilityId`, `DomainId`, `RegionHandle`, SIP request identity, platform mapping IDs, local DSC1 submission IDs, provider leases and HybridCPU epochs remain distinct namespaces.
3. **No raw platform authority in SIPs.** Provider tokens, physical addresses, VMCS state, lane IDs, raw opcodes and descriptor/queue identities stay bridge-private.
4. **MOVE means authority transfer, not guaranteed no-copy.** Physical zero-copy/remap is a negotiated implementation optimization. Slice-5 bounded source staging is an explicit example of authority semantics surviving a copy adaptation.
5. **No global coherence premise.** Every CPU↔device/accelerator/display handoff needs explicit visibility/completion semantics.
6. **Evidence is not authority.** Diagnostics, events, receipts and attestation objects are not accepted as grants unless a separately defined authority contract says so.
7. **Compatibility is downstream.** VMX/VMCS, POSIX, Win32 and Wine are projections/personalities over native contracts.
8. **Kernel ABI stays minimal.** Domains, capabilities, ownership, channels/events, platform mappings/grants and completion belong near the privileged boundary; pathname/socket/window/GPU/VMCS policy does not.
9. **Do not generalize a proven narrow transport.** `OwnershipPair` exists for exactly one Borrow plus one Consume requirement and is not permission to grow a universal multi-object ABI.
10. **Do not generalize a model provider into an executable claim.** Host `ModelOnly` completion proves local lifecycle composition only; external execution remains blocked until an independent neutral facade exists.
