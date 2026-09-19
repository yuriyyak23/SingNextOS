# Phase 08 Implementation Evidence

Date: 2026-09-19

## 1. Baseline and preserved status

- HEAD at phase entry: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- `git status --short` confirmed the pre-existing dirty P00-P07/unrelated roadmap work. It was preserved; no reset, checkout, clean, commit, push, or force operation was used.

## 2. Audited dependencies and reused types

P08 reused `EndpointSessionRegistry`, session invocation correlation, generated SIP protocol/dispatcher/client adapters, `ChannelRegistry` ownership transfer, `CapabilityAuthority`, P04 effect admission, P06 sealed socket identity, P07 Region uses/borrow lifetime, and response publication validation. No second RPC, session, capability, sealing, Region, ownership, or publication registry was added.

The audit found two live gaps: generated bounded payload validation trusted a shallow marker, and `InvokeSession*` performed `Resolve -> Send -> Register` without pinning session lifecycle across admission.

## 3. Design decisions

- Generator diagnostic `SINGGEN011` and analyzer diagnostic `SING3003` recursively admit only primitives, enums, outer-bounded strings, ownership families, and explicitly readonly value nodes. Arrays, `List<T>`, `object`, dynamic, pointers, type parameters, mutable classes, and unclassified nodes fail closed.
- The generator emits deterministic per-message `_ValueSchema` metadata and binds it into the canonical contract digest.
- Controlled bounded containers defensively copy caller arrays and expose no mutable backing: `ProcessManifestValue`, `InitialCapabilitySet`, and `SurfacePlaneSet`.
- Process manifests are reconstructed for runtime use from the defensive value projection. Initial capability delegation cardinality is fixed at 16; manifest capability/contract cardinalities and canonical bytes are bounded.
- Generated session invocation acquires and revalidates an exact `EndpointSessionPin` before request publication, registers exact invocation correlation, and releases the pin immediately after admission. It is not held during service/provider code or response waiting.
- Caller cancellation requests cancellation but does not imply provider cancellation, ownership return, Region reclaim, or response publication.

## 4. Authority/evidence/provider split

Generated metadata, value schemas, contract digests, request envelopes, and response envelopes are evidence. Capability authority remains in `CapabilityAuthority`; session lifecycle in `EndpointSessionRegistry`; sealed object identity in `SealedObjectAuthority`; Region ownership/use in `RegionAuthority`; publication in the existing response/session invocation registries. Provider completion remains provider evidence only.

## 5. Single-ledger/non-duplication proof

No generated authority bag or resolver exists. Generated clients pass one declared message payload to `ISipClientRuntimeTransport`; runtime resolves the exact session and existing protocol descriptor. `_ValueSchema` is a deterministic string projection used in contract identity, not an authority or runtime registry.

## 6. Lifecycle and composition semantics

- Session admission linearizes while its exact pin is active. Close either precedes admission and denies it, or observes the pin/draining state after admission.
- Socket calls continue to compose P06 seal pins with exact capability/session effect admission and final seal revalidation.
- Ownership-pair calls continue to use existing Channel/Region MOVE-or-borrow machinery; failure and cancellation settle through existing reverse cleanup/publication paths.
- Stale session/service/seal/Region generations fail closed. Revocation blocks new exact effect admission and does not fabricate external cancellation.
- No service, provider, callback, blocking wait, or user implementation executes while an EndpointSession, seal, capability, or Region authority lock is held.

## 7. Public/SIP/non-leak status

The public SIP surface contains declared bounded copied values, opaque handles, and ownership payloads only. Raw manifest lists and GUI plane arrays were replaced by defensive bounded projections. No `AsyncLocal` authority root, service locator, enumerable capability bag, generic resolver, provider token, or mutable authority record was exposed.

## 8. Changed files/projects

- `sdk/SingPlus.Generators/SingPlusGenerator.cs`
- `sdk/SingPlus.Analyzers/SingPlusAnalyzer.cs`
- `contracts/SingPlus.Contracts/GuiContracts.cs`
- `src/Sip/SingPlus.Sip/Process/IProcessService.cs`
- `src/Sip/SingPlus.Sip/Gui/ICompositorService.cs`
- `src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs`
- `src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs`
- `src/Runtime/SingPlus.Runtime/Gui/RuntimeCompositorServiceHost.cs`
- generator/analyzer/P08 tests and this evidence file

## 9. Qualification

- P08 generator/native/session focused set: 58 passed, 0 failed.
- P04/P06/P08, generator, cancellation, ownership-ingress, response-publication related set: 83 passed, 0 failed.
- Dedicated analyzer negative test: 1 passed, 0 failed.
- First full run had one existing 1-second GUI timing assertion fail under parallel load; isolated replay passed in 331 ms. It was not accepted as qualification.
- `dotnet build SingNextOS.slnx`: 0 warnings, 0 errors.
- Final `dotnet test SingNextOS.slnx`: 12 adapter + 58 neutral runtime + 60 platform + 1154 SingPlus passed, 2 skipped, 0 failed (1284 passed total).
- `git diff --check`: exit 0; only existing LF-to-CRLF notices.

## 10. Claim and limitations

Claim: `RuntimeEnforced` for generated contract deep-value rejection, deterministic schema binding, defensive bounded copied-value containers, and exact session pinning during generated invocation admission. This is not a claim that cancellation rolls back arbitrary service or provider effects.

FutureGated: broader performance/deadlock scaling remains P13; ManagedCap full-module/BCL closure remains P09; audit binding of schema/policy digests remains P10.

## 11. HybridCPU boundary

HybridCPU core, ISE, ISA/opcodes, register file, load/store, retire, scheduler, compiler, legality, microarchitecture, and architecture were not changed. The absent `tools\HybridCpu_ExecutableAdapter\refctor master plan2.md` remains a recorded limitation; no content was invented.
