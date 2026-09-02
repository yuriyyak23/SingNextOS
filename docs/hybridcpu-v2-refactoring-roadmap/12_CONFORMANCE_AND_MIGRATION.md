# Phase 12 — Conformance, migration and claim discipline

## Status

**Continuous finalization phase.** Start the conformance scaffolding in Phase 1 and require it for every later provider/service.

## Goal

Make architectural properties executable: every claim about a HybridCPU-backed service must be backed by positive and negative tests, exact versioned artifacts and teardown proof.

## Provider conformance layers

Use the same semantic test suite against:

1. deterministic host provider;
2. fault-injection host provider;
3. real `HybridCpuPlatformAuthorityProvider`;
4. optional compatibility/projection providers where relevant.

A provider can pass only the feature families it advertises. Unsupported families must fail truthfully rather than being skipped as success.

## Mandatory negative matrix

Every stateful provider family must test:

- missing local capability;
- wrong subject/process generation;
- wrong resource identity;
- wrong region owner/generation;
- out-of-range access;
- unsupported feature/profile;
- provider denied;
- provider stale/revoked;
- malformed/mismatched provider lease;
- stale completion receipt;
- duplicate completion/cancel/revoke;
- local revoke while external work is active;
- process/domain termination with active authority;
- provider fault during drain;
- no reclaim before terminal closure;
- no provider token/evidence leakage to SIP/public API.

## Cross-layer Definition of Done

A feature is “HybridCPU integrated” only when all applicable rows are positive:

```text
local contract shape proven
+ generated/static contract checks
+ local capability/ownership negative tests
+ external feature discovery truthful
+ real provider binding positive
+ external denial/stale tests
+ exact domain/range/direction binding
+ execution/result semantics
+ memory visibility/fence semantics
+ publication/commit semantics
+ cleanup/revocation proof
+ deterministic integration artifact
```

If any row is missing, use a weaker status such as:

- `LocalHostBacked`;
- `ModelOnly`;
- `ProjectionOnly`;
- `BridgeRequired`;
- `ExternalBlocked`;
- `FutureGated`.

## Claim examples

Allowed when only local host tests exist:

```text
“MOVE transfers exclusive Sing ownership without duplicating the logical payload in the host runtime.”
```

Not allowed without hardware evidence:

```text
“MOVE is physical zero-copy across HybridCPU address spaces.”
```

Allowed when DMA provider path is proven:

```text
“An exact region slice can be granted to this device/domain and reclaimed only after provider completion.”
```

Not allowed without coherence evidence:

```text
“DMA is globally cache coherent.”
```

Allowed for VMX projection:

```text
“VMX/VMCS compatibility state is projected from neutral domain authority.”
```

Not allowed until backend is executable:

```text
“HybridCPU VM hosting is production-ready.”
```

## Migration order in code

Keep changes reviewable and avoid broad simultaneous rewrites:

```text
A. add vNext feature/completion types + host tests
B. refactor bridge lifecycle to explicit draining/closed receipts
C. adapt current domain/mapping v1 paths to vNext without changing public SIP semantics
D. add real HybridCPU domain provider
E. add exact memory visibility/range mapping
F. add one DMA vertical slice
G. add one compute vertical slice
H. add neutral virtualization
I. add evidence/SecureCompute gating
J. grow native services and GUI
```

Each step should leave the previous host provider operational so failures can be localized to contract vs external integration.

## Documentation maintenance

When a phase changes status, update:

- `docs/whitebook/hybridcpu-ise/07_PLATFORM_BRIDGE_AND_EXTERNAL_CONTRACTS.md`;
- `docs/whitebook/hybridcpu-ise/09_DEVELOPMENT_DIRECTION.md`;
- relevant `EXT-HCPU-*` requirement status/evidence;
- this roadmap phase status.

Do not mark an external requirement closed merely because SingNextOS added an adapter interface. Close it only when the required external behavior is positively exercised.

## What should never be implemented as a “shortcut”

The following changes would damage the architecture and are excluded from the roadmap:

- numeric aliasing of Sing capability/domain/region IDs to HybridCPU tokens/owner IDs;
- VMCS as the authoritative Sing kernel VM object;
- Win32/POSIX/Wine as privileged system substrate;
- global mutable shared-memory IPC as the default communication model;
- universal coherence as an ABI precondition;
- unconditional physical zero-copy guarantee;
- giant syscall/HAL covering filesystem, network, GUI, VMX and accelerators;
- raw physical addresses, page-table pointers, IOMMU IDs or lane IDs in ordinary SIP contracts;
- exact-cycle scheduling as a general OS guarantee without timing proof;
- automatic SecureCompute activation from descriptor/model presence;
- evidence/attestation object accepted as authority.

## Minimal end-state architecture

The desired trusted boundary is:

```text
Applications / personalities
        |
source-familiar libraries
        |
generated typed SIP
        |
isolated system services
        |
Sing minimal trusted authority
  DomainId/process generations
  capabilities
  ownership/regions
  channels/events
  platform authority ledger
        |
Platform Authority Bridge
  opaque leases
  exact mappings/grants
  completion/revocation receipts
        |
HybridCPU neutral runtime
  execution + memory + I/O domains
  typed grants / nested domains
        |
concrete CPU/MMU/IOMMU/DMA/accelerator effects
```

VMX, diagnostics/evidence projections and compatibility personalities sit to the side, not underneath the authority root.

## Final acceptance criteria

The refactoring program is successful when:

- one real HybridCPU-backed domain can be created, run and destroyed safely;
- one owned region can be mapped/revoked with explicit non-coherent-safe semantics;
- one DMA path proves exact authority intersection and drain-before-reclaim;
- one semantic accelerator path reuses the same contracts;
- virtualization reuses the domain/memory/I/O substrate instead of introducing VMX authority;
- GUI Present can reuse region/grant/completion semantics without new privileged memory rules;
- SecureCompute remains unavailable unless production-positive evidence exists;
- all provider identities remain opaque and distinct from Sing capabilities;
- documentation claims match testable current behavior.
