# External HybridCPU Feature Gates

HybridCPU-v2 is read-only. No row authorizes a source change in that repository.

## Current external state

- Observed HybridCPU-v2 master: `794c4a53494f503855ac8cf209efab23fde083b2`.
- Current SingNext qualification pin: `9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9`.
- Compiler contract version at both relevant baselines is/was `6`, but contract-version equality does not waive requalification.
- `PlatformExternalGateTable.ExtHcpu001` is currently `ExternalBlocked` for `ManagedAssemblyToHybridCpuAot`.
- `ExtHcpu002` is `ExternalBlocked` for real hardware timer/MMIO/IRQ/DMA.
- `ExtHcpu004` is `ExternalBlocked` for executable external mapping/custody/coherence.

| Gate | Required external behavior | Current status | Existing gate mapping | SingNext behavior until satisfied | Max claim |
|---|---|---|---|---|---|
| `HC-PIN` | selected HybridCPU SHA has a fresh SingNext qualification artifact | **Blocked by SHA drift** | qualification recorder/scripts | keep old pin as historical evidence; do not promote new master | prior qualified claim only |
| `HC-AOT-IMAGE` | capsule closure becomes a reset/loadable HybridCPU image | `ExternalBlocked` in current SingNext gate vocabulary | `ExtHcpu001` where semantically sufficient | Direct Boot image lane disabled | `AdapterQualified` |
| `HC-ENTRY` | stable capsule entry/calling convention/input descriptor | revalidate exact current contract | add narrow P15 gate only if no existing requirement covers it | build/ISE feature gate fails closed | `AdapterQualified` |
| `HC-BOOTSTRAP` | runtime bootstrap supports selected capsule memory profile, including GC/type/unwind only if needed | revalidate | may share `ExtHcpu001` evidence but requires explicit profile record | choose stricter supported profile or remain blocked | `AdapterQualified` |
| `HC-RESET-ROM` | architectural reset transfers from immutable ROM to authenticated local capsule | unverified | new external behavior gate if absent from current table | no Direct Boot reset claim | `AdapterQualified` |
| `HC-ISE-CXL` | ISE exposes enough PCI/CXL/HDM behavior for claimed end-to-end lane | unverified | no silent reuse of HW gate | CXL remains adapter/model qualified | `AdapterQualified` |
| `HC-HW-IO-DMA` | exact platform supplies real timer/MMIO/IRQ/DMA isolation | `ExternalBlocked` | `ExtHcpu002` | no hardware promotion | `IseValidated` at most |
| `HC-HW-MAPPING` | exact platform supplies executable mapping/custody/coherence/decoder behavior | `ExternalBlocked` | `ExtHcpu004` | no hardware HDM/retirement claim | `IseValidated` at most |
| `HC-PROTECTED-STORE` | monotonic/power-loss-safe protected state for rollback floors/trials | hardware dependency | add only if no existing platform requirement fits | model/adapter state only | `IseValidated` at most |

## Gate-table rule

Do not create a second global external-gate registry when `PlatformExternalGateTable` already owns the same requirement. P15-specific documents may use aliases, but CI must map aliases to one authoritative gate owner.

## Fail closed

Missing gate => feature disabled for that lane; qualification artifact records `ExternalBlocked`/`Unavailable`; no optimistic fallback and no HybridCPU source PR.
