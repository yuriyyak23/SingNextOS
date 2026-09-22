# P15 — Direct SingNext Boot Architecture Refactor Roadmap

## Purpose

This package is the implementation roadmap for converting the current SingNextOS boot/CXL model into an executable **Direct SingNext Boot** contour:

```text
HybridCPU reset
  -> immutable ROM
  -> signed local SingNext Boot Capsule
  -> PCIe/CXL discovery
  -> temporary HDM boot aperture
  -> verified kernel/services load to normal RAM
  -> HybridBootInfo
  -> kernel entry
  -> fresh provider admission
  -> runtime CXL generations
  -> RegionAuthority / OwnedRegion
```

The roadmap assumes the architecture described in:

- `docs/Completed/HybridCPU-v2-Boot-Reset-CXL-Boot-ABI-Roadmap/26-ARCHITECTURE-ASSESSMENT-FIRMWARELESS-CXL.md`;
- the completed HybridCPU/CXL/SingCap roadmaps;
- the existing rule that boot evidence never becomes runtime authority.

## Repository baseline used for this plan

- SingNextOS `master`: `0f152a5502c58546eb0458b4be3e7cbce6c5ef3e`
- HybridCPU-v2 `master`: `794c4a53494f503855ac8cf209efab23fde083b2`

The existing SingNext HybridCPU qualification lane still pins an older HybridCPU revision and records `ManagedAssemblyToHybridCpuAot = ExternalBlocked`; P15 explicitly closes or replaces that gate rather than silently bypassing it.

## Documents

| File | Role |
|---|---|
| `01-DECISION-AND-SCOPE.md` | architecture decision, invariants, non-goals |
| `02-TARGET-PROJECT-TREE.md` | exact proposed repository/project tree and dependencies |
| `03-API-CONTRACTS.md` | proposed public/internal boot APIs and ownership rules |
| `04-MIGRATION-MAP.md` | exact disposition of current `HybridCpu_ExecutableAdapter/Boot` files |
| `05-P15-PR-SLICES.md` | implementation sequence as reviewable PR slices |
| `06-BOOT-CAPSULE-SECURITY-PROFILE.md` | TCB, no-ambient-authority profile and admission gates |
| `07-HYBRIDCPU-AOT-AND-ENTRY-ABI.md` | managed-to-HybridCPU image path, ROM/capsule/kernel entry ABI |
| `08-CXL-BOOT-BACKEND.md` | PCI/CXL/HDM/protected-store boot backend plan |
| `09-KERNEL-HANDOFF-AND-FRESH-ADMISSION.md` | BootInfo handoff and authority discontinuity |
| `10-QUALIFICATION-PLAN.md` | model/differential/ISE/hardware qualification strategy |
| `11-EXIT-CRITERIA.md` | phase and program exit criteria |
| `12-RISKS-AND-NON-GOALS.md` | major technical risks and intentionally deferred scope |
| `13-IMPLEMENTATION-CHECKLIST.md` | concrete file/task checklist |
| `14-CLAIM-LEVELS-AND-EVIDENCE.md` | allowed claim vocabulary and evidence promotion rules |

## Recommended execution order

```text
P15.0 contracts/location cleanup
  -> P15.1 shared Boot Core
  -> P15.2 capsule security profile
  -> P15.3 HybridCPU image/AOT bridge
  -> P15.4 boot platform descriptor
  -> P15.5 executable PCI/CXL boot backend
  -> P15.6 executable temporary aperture
  -> P15.7 capsule loader + BootInfo + kernel entry
  -> P15.8 fresh runtime takeover
  -> P15.9 protected state / A-B / recovery
  -> P15.10 ISE end-to-end qualification
  -> P15.11 optional QEMU companion lane
  -> P15.12 hardware qualification
```

The program MUST preserve the existing architectural rule:

```text
boot evidence != authority
physical mapping != ownership
same numeric HDM state != authority continuity
reset survival != generation survival
```
