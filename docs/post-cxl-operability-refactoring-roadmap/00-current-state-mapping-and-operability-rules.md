# Phase 00 — Current-State Mapping and Shared Operability Rules

Status: implemented baseline for subsequent phases.

## Scope and architectural position

Phase 00 establishes vocabulary and ownership. It does not create an operability authority, a universal handle, a second lifecycle registry, or a common mutable closure store. Each effect remains authorized and closed by its owning subsystem.

The only shared executable primitive introduced by this phase is `OperabilityGeneration.Compare`. It compares two non-zero generation values and returns `Exact`, `Stale`, or `Invalid`. The caller must obtain the authoritative value from the existing owning registry. The result is validation evidence, not authority. Unequal values are intentionally not ordered: an older handle and a fabricated future handle are both stale.

## Current-state internal mapping

| Term | Current authoritative source | Operability use | Explicit non-authority |
| --- | --- | --- | --- |
| `ServiceId` / `ServiceGeneration` | `ServiceRegistry` owns the current endpoint descriptor generation; `ProcessHandle` remains the exact process generation | discovery correlation and stale rejection | service name, manifest, health and discovery descriptor do not grant invocation authority |
| Process/domain generation | kernel process/domain registries and exact `ProcessHandle`/domain handles | bind every long-lived control-plane object to its owner generation | persisted metadata and checkpoint data cannot revive the handle |
| Manifest | `SingProcessManifestV1` and `ServiceManifestV1` are immutable requests validated before admission | request identity, entry point, resource/features and normalized evidence | manifest and digest are neither capability nor grant |
| Dependency binding | Phase 02 will own exact service-generation bindings; current service sessions already bind exact provider/caller handles | readiness and replacement correlation | dependency declaration and readiness observation are not authority |
| Deadline/cancellation scope | Phase 04 will own a generation-bound monotonic scope registry; current endpoint invocation cancellation remains session/invocation-bound | stop waiting or request cancellation | timeout, disconnect or request does not prove external-effect closure |
| Budget reservation | Phase 05 will own one hierarchical ledger rooted at system budget | admission capacity and exact charge lifetime | budget is not permission to execute an effect |
| Trace correlation | Phase 06 will own session/producer/sequence identities and bounded buffers | causal history and replay correlation | trace and replay certificate are not capability or provider admission |
| IPC transfer | existing channel/session registry and `RegionAuthority` own channel state, MOVE, borrow, `RegionUse` and mutation epochs | Phase 07 adds transport-neutral intent over the same ownership truth | transport choice and zero-copy claim do not alter ownership |
| Checkpoint classification | Phase 08 will own one classification per resource kind and immutable image metadata | classify logical ordinary-domain state | checkpoint is data and never a live capability/provider lease |
| Telemetry projection | Phase 09 will project existing authoritative state under explicit visibility policy | observation and aggregates | telemetry and security evidence remain distinct and non-authoritative |
| Provider conformance/fault plan | Phase 10 will own scenario-bound test plans; provider effect truth stays in the provider bridge and `ExternalOperationAuthority` | deterministic qualification evidence | fault plan is not exposed as production effect authority |

## Identity and stale-validation rules

- Semantic identities remain typed (`ServiceId`, `ProcessId`, `ExternalOperationId`, region/device/domain identities). No universal authority identifier is introduced.
- Every generation comparison is equality-only against the value read from the owning registry.
- Zero is invalid. Any non-equal generation is stale; a larger presented value is not treated as newer or trusted.
- Restart/replacement creates a fresh service and process generation. Capability, session, provider lease, cancellation scope, subscription, reservation and receipt bindings from generation N are stale for N+1.
- A digest identifies normalized data for correlation; it never substitutes for a typed identity or generation.

## Lifecycle vocabulary

These terms are deliberately separate:

```text
Requested -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published
                                                     \
                                                      -> Closed/Contained -> Released
```

- `Admitted` means local policy and required authority checks passed for that stage; it does not prove a provider effect.
- `DeviceComplete` is provider completion evidence.
- `Visible` is satisfaction of the required memory/device visibility rule.
- `Published` means the result became architecturally/application visible under the owning publication policy.
- `ProviderClosed` is an exact provider-specific closure receipt.
- `ProviderEffectContained` is explicit proof that any possible effect can no longer escape its containment boundary. It is not inferred from unavailability.
- `Released` means the owning subsystem completed exact local unpin/reclaim after closure or permitted containment.
- `Unavailable`, timeout, cancellation, crash, restart, caller disconnect and malformed/unknown results prove none of closure, containment or release.

Resource-specific state machines and receipts remain authoritative. Shared terms are comparison and documentation vocabulary only; later phases must not replace `ExternalOperationAuthority`, `PlatformAuthorityBridge`, `RegionAuthority`, device/virtual/secure closure receipts, or their exact compensation rules with a generic mutable lifecycle.

## Locking and provider-call rule

An owning subsystem must establish the exact pre-call state under its local synchronization, release global/shared locks before an external provider call, then reacquire and revalidate identity, generation and expected state before committing the result. A partial materialization is compensated using the exact provider/resource receipt. Unknown, malformed, timed-out or post-acceptance failures retain pins/accounting and enter the owning subsystem's draining or quarantine path.

## Public/SIP boundary

Ordinary public and SIP contracts expose semantic provider-neutral identities only. They do not expose CXL topology, BDF, HDM, DPA/HPA, fabric routes or manager identities, raw mappings, provider-private leases/recovery tokens, secure backend internals, HybridCPU ISA/compiler metadata or replay certificates as authority.

## FutureGated boundaries

- A generic generation wrapper is not introduced until existing typed identities can adopt one without erasing domain distinctions.
- A common immutable closure/containment evidence shape may be considered only if at least two owning subsystems can project it without making it their source of truth.
- Persistent authority reconstruction, cross-host live migration, confidential checkpoint, generic live re-effect replay, mandatory zero-copy ABI and unprivileged production fault injection remain out of scope.
- Phase 00 makes no production-hardware, CXL-device, QEMU, FPGA or silicon qualification claim.
