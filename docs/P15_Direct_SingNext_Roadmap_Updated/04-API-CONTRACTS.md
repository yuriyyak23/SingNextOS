# API Contracts and Ownership

Before adding any API below, search current source for an authoritative equivalent. The default is **reuse or extend**, not parallel creation.

| API/type | Disposition | Owner/layer | Semantics |
|---|---|---|---|
| `HybridPlatformDescriptorV1` | `NewRequired` only if no equivalent exists | Boot.Contracts | immutable/versioned physical evidence descriptor; never capability |
| `BootCapsuleEntryAbiV1` | `NewRequired` or `MustExtendExisting` | Boot.Contracts | fixed-width entry ABI, size/version, bounded pointer/range fields |
| `KernelEntryAbiV1` | `MustExtendExisting` if entry contract exists | Boot.Contracts + kernel adapter | explicit calling convention and BootInfo pointer/length |
| `HybridBootInfo` / codec | `CanReuseExisting` or `MustExtendExisting` | Boot.Contracts | evidence-only; checksummed/versioned; no capability fields |
| `IBootPciConfiguration` | `NewRequired` unless existing boot port | Boot.Core port | bounded config-space access and typed failures |
| `IBootCxlTransport` | `NewRequired` unless equivalent | Boot.Core port | mailbox/CCI with deadlines/reset invalidation; no runtime lease |
| `IBootTemporaryMapping` | `NewRequired` | Boot.Core port | transactional prepare/commit/readback/retire semantics |
| `IBootProtectedState` | `NewRequired` | Boot.Core port | durable monotonic-state protocol |
| `IBootRecoverySource` | `NewRequired` | Boot.Core port | bounded local authenticated recovery reads |
| `IBootClock` | `NewRequired` or reuse monotonic clock abstraction | Boot.Core port | deadline source; no wall-clock trust |
| `IBootResetControl` | `NewRequired` or reuse platform reset abstraction | Boot.Core port | reset reason and terminal reset request |
| `IBootDmaIsolation` | `NewRequired` or extend existing IOMMU abstraction | Boot.Core port | deny-by-default before device enable |
| `IFreshCxlBootDiscovery` | prefer `MustExtendExisting` runtime discovery API | kernel/runtime integration | liveness/generation revalidation; does not itself mint capability |
| `IFirmwareApertureRetirement` | `NewRequired` only if no owner exists | kernel/platform boundary | `Released` / `Stale` / `Quarantined`; ambiguity => quarantine |

## Wire versioning rules

Every wire structure must have:

- magic/type discriminator where appropriate;
- major/minor or explicit version;
- total size;
- fixed endianness;
- overflow-safe offset/length encoding;
- reserved fields zero-checked if required;
- maximum size/count constraints;
- forward-compat rejection/ignore rules explicitly documented.

## Failure semantics

No bool-only hardware/protocol API. Failure types must distinguish at least:

- `Unsupported`;
- `Timeout`;
- `LinkLost`;
- `ResetObserved`;
- `Malformed`;
- `BoundsViolation`;
- `ReadbackMismatch`;
- `PartialCommit`;
- `AmbiguousState`;
- `SecurityPolicyDenied`.

`AmbiguousState`, `PartialCommit`, reset during mapping, or unknown teardown state must fail closed.

## Generation semantics

Boot wire structures may carry observed generation/evidence values, but they do not mint runtime authority.

Runtime `ProviderGeneration` and `RegionGeneration` are created only by the existing runtime authority owner after fresh admission.

## Boundedness

Every externally influenced count/length/offset is bounded by policy constants. No unbounded:

- PCI capability walk;
- device enumeration;
- mailbox retry;
- BootVolume candidate list;
- manifest entry list;
- recovery search;
- mapping list.

## Testability

All Core ports must have deterministic fakes suitable for fault injection. Production platform code must not share mutable test-model state.
