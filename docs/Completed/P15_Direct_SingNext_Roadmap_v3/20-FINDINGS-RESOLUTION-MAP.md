# Audit Findings Resolution Map

## Architecture/repository corrections incorporated

| Audit issue | v3 correction |
|---|---|
| roadmap treated Boot.Contracts as absent/new | current Boot.Contracts is explicitly reused/frozen first |
| possible duplicate importer/fresh APIs | existing `HybridBootInfoImporter`, `IFreshCxlBootDiscovery`, `IFirmwareApertureRetirement` are authoritative interfaces |
| production handoff gap hidden by test fakes | concrete production fresh-discovery/retirement adapters are explicit P15-10 deliverables |
| BootInfo risk | existing importer remains evidence-only; candidate list comes only from fresh discovery |
| provider generation wording could imply second owner | importer does not mint generations; existing provider lifecycle owner remains authoritative |
| target tree could cause unnecessary Boot.Contracts move | path move is optional mechanical cleanup, not sequencing prerequisite |
| architecture policy plan duplicated existing `BootContracts` | reuse current `Layer.BootContracts`; add only genuinely missing layers |
| existing `SingPlus.Boot` could be confused with capsule | explicitly preserved as host-debug ManagedGc executable; capsule is new project |
| capsule no-heap claim too early | memory profile is an evidence decision; no-heap requires allocation-safe BootInfo writer |
| existing codec assumed capsule-safe | v3 calls out allocation-heavy Encode path and requires byte-compatible bounded writer if no-heap |
| NativeAOT overclaim | retained as evidence only |
| reset-domain confusion | existing `ObservePlatformBackendReset()` is runtime-only; architectural reset remains external boot domain |
| stale HybridCPU pin | exact current drift recorded; SHA update requires requalification |
| duplicate external gate registries | P15 aliases must map to `PlatformExternalGateTable` when semantics overlap |
| model promotion/deletion | rewrite/split + differential proof; deletion deferred to P15-15 |
| qualification gaps | full 25-scenario matrix retained and extended with authority/allocation properties |

## Corrections to inaccurate audit assertions

The audit correctly identified architectural risks but some negative implementation assertions were stale/overbroad. v3 does not repeat them:

- Boot.Contracts is not absent.
- `HybridBootInfoImporter` and the two handoff interfaces are not test-only definitions; only their concrete implementations remain test-local at the observed master.
- `RegionAuthority`, `CxlAuthorityBridge`, `CxlType3MemoryAuthority`, and runtime backend epoch handling already exist.
- `SingPlus.Boot` exists, but it is the wrong security/composition contour for Direct SingNext.
- `RepositoryArchitecturePolicyTests` already knows `BootContracts`.

Implementation work is therefore integration/refactoring around existing owners, not a greenfield replacement of those owners.
