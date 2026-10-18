# SingNextOS — Architecture Whitebook

**Source-verified English edition, 18 October 2026.** Baseline: `master` commit `4cbc8e36a54da7cf193e13e7c18d773b36da2c04`. This is an architecture snapshot, not an implementation roadmap, hardware certification, or production-readiness claim.

SingNextOS is a managed, authority-oriented operating-system research platform with typed SIP services, capability-based authorization, explicit resource ownership and budgeting, and provider-neutral heterogeneous execution. The runtime coordinates independently owned facts rather than treating an OS handle, provider receipt, or completion event as global authority.

The previous Russian Whitebook was present under this directory in the parent revision but was removed from the current `master` tree. This edition restores the documentation in English, updates code anchors and verified implementation status, and does not restore non-existent historical roadmap or artifact paths.

## How to read the evidence

- **Source-verified:** observable type, method, branch, project dependency, or path in the pinned commit.
- **Implemented contour:** a source-backed bounded route; not a universal feature.
- **Contract/model only:** a DTO, optional provider interface, model, or qualification seam, without a demonstrated physical producer.
- **Open / not qualified:** missing independently demonstrated property. Neither unit-test names nor past reports prove production qualification.

For system behavior, actual code, exact provider/package tuples, and executable evidence supersede this prose. No .NET build, hardware experiment, or fresh end-to-end test run was performed to produce this revision.

## Contents

1. [Architecture, ownership and admission](01-ARCHITECTURE.md)
2. [Runtime subsystems](02-RUNTIME-SUBSYSTEMS.md)
3. [Providers, execution, devices and virtualization](03-PLATFORM-EXECUTION.md)
4. [Developer surface, static admission and boot](04-DEVELOPER-AND-BOOT.md)
5. [Operation lifecycles and failure recovery](05-LIFECYCLES.md)
6. [Directions, gates and open obligations](06-DIRECTIONS.md)
7. [Verification and evidence limits](07-VERIFICATION.md)
8. [Current project catalog](08-PROJECT-CATALOG.md)
9. [Maintained source atlas](09-SOURCE-ATLAS.md)
10. [Glossary](10-GLOSSARY.md)
11. [Subsystem and closure matrix](11-SUBSYSTEM-MATRIX.md)
12. [Code verification / revision notes](12-CODE-VERIFICATION.md)

## Confirmed configuration

| Property | Pinned value / status | Evidence |
| --- | --- | --- |
| Runtime framework | `net11.0` | [Runtime project](../../../src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj) |
| SDK selection | `11.0.100-rc.2.26504.105`, roll-forward disabled | [global.json](../../../global.json) |
| Language baseline | C# 13, opt-in preview | [Directory.Build.props](../../../Directory.Build.props) |
| External runtime contracts | `HybridCPU.ExternalRuntime.Contracts` `[1.36.4]` | [Runtime project](../../../src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj) |
| V6 and vNext default gates | OFF in the inspected `IsEnabled` implementations | [V6 gates](../../../src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs), [vNext gates](../../../src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs) |
| Source inventory | 45 projects; 466 maintained `.cs` in `src/`, `contracts/`, `sdk/` | [Catalog](08-PROJECT-CATALOG.md), [Atlas](09-SOURCE-ATLAS.md) |

The `src/Kernel/Boot/SingPlus.Boot/Samples` tree is included for navigation, not evidence of a fully qualified hardware boot. `tools` and `tests` appear in the project catalog; generated artifacts are not counted in the source atlas.

**Caution:** `KernelEntryPoint.Run` currently prints a host-debug readiness message in the staged execution overload. Its presence does not prove a standalone physical OS image; [source](../../../src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs).

This book does not introduce a new prioritized development backlog. Historic phase labels describe intended semantics only.
