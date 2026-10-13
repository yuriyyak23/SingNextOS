# API Contracts and Ownership

Default rule: reuse/extend an existing authoritative API before adding a parallel API.

| API/type | Current disposition | Owner | Required correction |
|---|---|---|---|
| `HybridBootInfoV1` / `HybridBootInfoCodec` | `AlreadyExists` | existing Boot.Contracts | Freeze wire layout; reuse parser/runtime codec |
| allocation-safe BootInfo writer | `NewRequired` if capsule is no-heap/bounded-arena | Boot.Core/Capsule helper | Span/bounded-buffer writer producing exact existing V1 wire bytes; no second format |
| `KernelEntryAbiV1` | existing model/contract material must be inventoried | Boot.Contracts | Reuse/extend only; no duplicate ABI |
| `BootCapsuleEntryAbiV1` | `NewRequired` only if external entry contract does not already cover it | Boot.Contracts | fixed-width evidence/input ABI only |
| `HybridPlatformDescriptorV1` | `NewRequired` only if no current descriptor exists | Boot.Contracts | physical evidence only, no authority |
| `IBootPciConfiguration` | `NewRequired` unless equivalent exists | Boot.Core port | bounded config access + typed failures |
| `IBootCxlTransport` | `NewRequired` unless equivalent exists | Boot.Core port | deadline/reset-invalidated mailbox/CCI |
| `IBootTemporaryMapping` | `NewRequired` | Boot.Core port | prepare/commit/readback/retire transaction |
| `IBootProtectedState` | `NewRequired` | Boot.Core port | monotonic durable protocol; no BootVolume-owned floor |
| `IBootRecoverySource` | `NewRequired` | Boot.Core port | bounded local authenticated source |
| `IBootClock` | reuse if suitable | Boot.Core port | monotonic deadline only |
| `IBootResetControl` | reuse if suitable | Boot.Core port | reset evidence + terminal reset request |
| `IBootDmaIsolation` | extend existing platform/IOMMU abstraction if possible | Boot.Core port | deny-by-default before device enable |
| `IFreshCxlBootDiscovery` | `AlreadyExists` | `HybridBootInfoImporter.cs` | do not duplicate; add production implementation/wiring |
| `IFirmwareApertureRetirement` | `AlreadyExists` | `HybridBootInfoImporter.cs` | do not duplicate; add production implementation/wiring |

## Important correction: provider generation ownership

`HybridBootInfoImporter` must not mint a provider generation. It may require a **current** generation-bearing snapshot from the authoritative runtime provider/discovery owner. If that owner creates a new generation as part of rediscovery/rebind, that happens in the existing owner, not inside P15 importer code.

## Failure semantics

Hardware/protocol ports return typed failures including at least:

`Unsupported`, `Timeout`, `LinkLost`, `ResetObserved`, `Malformed`, `BoundsViolation`, `ReadbackMismatch`, `PartialCommit`, `AmbiguousState`, `SecurityPolicyDenied`.

Ambiguous/partial/reset-with-unknown-cleanup => `Stale`/`Quarantined`/fail-stop, never optimistic release.

## Wire rules

All externally influenced lengths/counts/offsets are fixed-width, overflow-checked, bounded, versioned, and have explicit unknown-field behavior.
