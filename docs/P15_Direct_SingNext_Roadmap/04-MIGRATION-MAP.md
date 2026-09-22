# P15.3 — Migration map from `HybridCpu_ExecutableAdapter/Boot`

This file is the explicit disposition of every current boot-related source file.

## `Boot.Contracts` project

Current:

```text
tools/HybridCpu_ExecutableAdapter/Boot.Contracts/
```

Target:

```text
contracts/HybridCpu.Boot.Contracts/
```

### Move essentially unchanged, then namespace-clean

| Current file | Target | Action |
|---|---|---|
| `BootContracts.cs` | `contracts/HybridCpu.Boot.Contracts/BootContracts.cs` | move; split enums/records later only if useful |
| `BootWire.cs` | same project | move; preserve checked-range and endian helpers |
| `BootPolicyCodec.cs` | same project | move |
| `BootVolumeHeaderCodec.cs` | same project | move |
| `BootManifestCodec.cs` | same project | move |
| `HybridBootInfoCodec.cs` | same project | move |
| `BootEvidencePayloadCodec.cs` | same project | move |

### Add to contracts

- `HybridPlatformDescriptorV1.cs`
- `KernelEntryAbiV1.cs`
- exact capsule manifest/header definitions;
- explicit boot-capsule generation / rollback-domain records.

## Current `Boot/` models

### `CxlBootSelectionModel.cs`

**Disposition:** promote algorithm into `SingNext.Boot.Core/Selection`.

Move:

- logical identity filtering;
- rollback-floor filtering;
- physical-selector optional policy;
- replica failover semantics;
- split-brain checks;
- deterministic ordering.

Do not move:

- `Model` suffixes;
- test-specific physical observation strings.

New production types should be immutable and contract-oriented.

### `TrustAndProtectedStateModel.cs`

**Disposition:** split.

Move to Boot Core:

- trust decision state machine;
- rollback comparison;
- confirmation preconditions;
- trust epoch checks.

Keep in test/model project:

- `DeterministicModelSignatureVerifier` using HMAC;
- `AtomicProtectedStateModel` in-memory backend.

Add production adapters in `SingPlus.Platform.HybridCpu.Boot/Trust`:

- asymmetric signature verifier;
- protected monotonic state backend.

### `PciCxlTransportModel.cs`

**Disposition:** split protocol parsing from modeled transport.

Move to Boot Core or a small protocol namespace:

- bounded PCI capability-chain validation rules;
- `HybridBootLocatorParser` if locator remains supported as optional hint.

Keep model-only:

- `DeterministicMailboxModel`.

Replace with production interfaces/adapter:

- `IBootPciConfiguration`;
- `IBootCxlTransport`;
- `HybridCpuBootPciConfig`;
- `HybridCpuBootCxlTransport`.

### `TemporaryApertureModel.cs`

**Disposition:** promote state-machine semantics, replace register effects.

Move to Boot Core:

- single-active-mapping rule;
- no-interleave baseline;
- reserved-range validation;
- generation/staleness semantics;
- reverse compensation ordering;
- ambiguous failure -> quarantine policy.

Keep model-only:

- fake `DecoderHop` availability/commit behavior.

Add production implementation:

- actual root/switch/endpoint decoder transaction;
- readback/commit validation;
- reset/link-loss fencing;
- teardown/quarantine.

### `Stage1LoadModel.cs`

**Disposition:** promote almost entirely into Boot Core `Loading/VerifiedComponentLoader`.

Preserve:

- bounded component count and size;
- aligned destination ranges;
- overlap exclusion;
- copy then hash destination;
- entry containment;
- all-or-nothing publication/zeroing on failure.

Move `KernelEntryAbiV1` to contracts.

Replace `byte[] Source` with a streaming source abstraction so real CXL capacity is not first copied into a host-managed array.

### `Stage0RecoveryModel.cs`

**Disposition:** do **not** turn into the CXL capsule. Split it into ROM requirements and recovery tests.

P15 Direct architecture uses ROM only for local capsule verification/copy/entry. Retain this model as a ROM/recovery oracle and adapt it to a local `SingNextBootCapsule` image rather than Stage-1-on-CXL.

Production ROM implementation belongs primarily in HybridCPU/platform repository, not SingNext Boot Core.

### `AbRecoveryModel.cs`

**Disposition:** promote state machine to `SingNext.Boot.Core/Recovery`.

Preserve:

- payload-before-manifest ordering;
- trial attempt budget;
- explicit confirmation;
- rollback-floor advance on confirmation only;
- replica fallback;
- signed local recovery fallback.

Replace in-memory dictionaries with narrow media/protected-state interfaces.

### `ResetAndMemoryMapModel.cs`

**Disposition:** split specification tests from platform implementation.

Move pure validation into Boot Core/contracts:

- physical-range checks;
- reserved memory rules;
- boot aperture exclusion from normal RAM.

Keep reset architectural behavior as cross-repo HybridCPU qualification; do not duplicate CPU reset state in SingNextOS.

### `BootBackendCapabilityProfile.cs`

**Disposition:** replace as architecture, retain as qualification helper only.

The current `IBootPlatformBackendV1` only advertises/open-checks capabilities. It is not a real IO backend. P15 introduces narrow executable interfaces. Keep capability-profile logic in qualification tooling to prove a composition contains all required services, but do not use it as the runtime API.

## Tests migration

Every model test should become one of:

1. pure Boot Core unit/property test;
2. model-adapter test;
3. differential test comparing old model oracle with new production algorithm;
4. ISE integration test;
5. hardware-gated qualification test.

Old tests MUST NOT be deleted until the corresponding new implementation has a differential equivalence test or an explicitly reviewed semantic change.
