# Phase 0 — Rebase the baseline and freeze invariants

## Status

**Current / baseline hardening.** No new HybridCPU backend is required. The first implementation slice rebases the post-Track-A status, freezes authority terminology, strengthens provider identity typing, and adds negative architectural guards before Platform Contract vNext begins.

## Why this phase exists

The architecture audit was initially performed while SingNextOS master was moving. Current master now includes PR #15 / commit `e0387949...`, merged as `7977043...`, which completes the Track A client response adapter/teardown work. Existing roadmap text still contains older status statements that make response transport look like the first unfinished priority.

The first refactoring step is therefore to make the repository internally consistent before expanding the platform boundary.

## Current evidence to preserve

Current master proves these useful invariants:

- `contracts/SingPlus.Contracts/SipClientRuntimeTransport.cs` defines sync/async runtime invocation independent of service-specific clients;
- `sdk/SingPlus.Generators/ClientRuntimeAdapterGenerator.cs` adapts generated typed SIP clients to runtime transport;
- `src/Runtime/SingPlus.Runtime/Channels/ResponseRegistry.cs` correlates exact request sequence → completion and distinguishes published vs cancelled responses;
- `ChannelRegistry.ChannelClosed` tears down response state;
- `RuntimeKernel.CleanupProcess` closes channels for the exact process before domain-wide cleanup;
- `tests/SingPlus.Tests/Contracts/ResponseClientAdapterTeardownTests.cs` proves waiter cancellation, process-level teardown and publication persistence.

This becomes the new minimum IPC/publication baseline. Later platform completion work must compose with it rather than inventing a second response model.

## Authority glossary

These terms are normative for roadmap text and architectural tests:

```text
local authority       = Sing capability/owner/generation/protocol state
platform lease        = opaque bridge-private external authority instance
completion receipt    = evidence that a staged external operation reached a defined terminal state
publication           = caller-visible commit of a local protocol/result
reclaim               = local reuse/free after all external authority is closed
projection            = compatibility/read-only view that owns no authority
```

Identity namespaces remain distinct even when two identifiers happen to carry the same primitive representation. In particular, `CapabilityId`, `DomainId`, `RegionHandle`, `PlatformProviderId`, provider lease IDs and future HybridCPU identifiers are not interchangeable authority.

## Claim-status vocabulary

Until runtime feature discovery is introduced in Phase 1, documentation and tests use these status classes:

- `Current` — implemented and exercised in the current SingNextOS repository;
- `CurrentModelBound` — implemented only against the local/host model, with no real HybridCPU-backed effect claim;
- `BridgeRequired` — local semantics exist but the platform bridge contract still needs expansion;
- `ExternalBlocked` — a real external HybridCPU/provider mechanism is required and not yet integrated;
- `ProjectionOnly` — compatibility/read-only projection, not native authority;
- `FutureGated` — intentionally unavailable until stronger prerequisites are proven.

This vocabulary is metadata, not a runtime feature enum and not authority.

## Refactoring tasks

### 1. Rebase architecture status documents

Update the status portions of:

- `docs/whitebook/hybridcpu-ise/09_DEVELOPMENT_DIRECTION.md`;
- any Track A references in the whitebook that still say response/client closure is pending.

Do **not** rewrite normative capability/ownership/platform invariants simply because the baseline commit changed.

Expected status change:

```text
Track A: current / completed foundation
Track B: current local v1, needs vNext lifecycle/completion
Track C+: external/runtime integration work
```

### 2. Keep the authority glossary authoritative

Before adding more provider interfaces, code, tests and roadmap text must preserve the distinctions in the glossary above. Provider identities may be strongly typed in platform abstractions, but they must not become process-visible Sing capabilities or SIP authority tokens.

### 3. Freeze negative architectural tests

Promote or add tests that prevent accidental regressions:

- provider contracts must not accept `CapabilityId` as an external token;
- SIP/public contracts must not expose provider lease types;
- VMX/VMCS types must not become required by core platform abstractions;
- response cancellation on teardown must remain deterministic;
- a response published before peer teardown remains committed;
- process teardown closes its channels even when another process keeps the domain alive.

### 4. Preserve claim-status discipline

Use the common classification above in docs/tests until Phase 1 introduces typed runtime discovery. Do not upgrade a model-only descriptor into an executable or production-secure claim.

## Acceptance criteria

Phase 0 is complete when:

- repository docs no longer claim Track A is unfinished;
- all current response/client teardown tests remain green;
- at least one negative test guards each of the identifier-separation and no-raw-platform-authority invariants;
- later phases can cite one unambiguous baseline commit/status table.

## Do not do in this phase

- do not add DMA/VM/GPU APIs;
- do not change HybridCPU ISA/runtime internals;
- do not turn `ISipClientRuntimeTransport` into a kernel ABI;
- do not weaken cancellation/publication semantics to fit future devices;
- do not infer hardware zero-copy from current host-backed ownership transfer.
