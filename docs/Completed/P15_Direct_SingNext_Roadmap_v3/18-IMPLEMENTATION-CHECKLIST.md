# Implementation Checklist

Checked items below mean repository implementation or local deterministic evidence only. They do not imply `IseValidated`, `HardwareValidated`, an admitted capsule root, or a requalified HybridCPU revision.

## P15-00 baseline

- [x] Record current SingNextOS master SHA.
- [x] Record observed HybridCPU master separately from qualified HybridCPU SHA.
- [x] Confirm compiler contract version without treating version equality as qualification.
- [ ] Inventory every `tools/HybridCpu_ExecutableAdapter/Boot/*` file.
- [ ] Freeze existing Boot.Contracts paths/types/public surface.
- [ ] Locate existing `HybridBootInfoImporter`, `IFreshCxlBootDiscovery`, `IFirmwareApertureRetirement`.
- [ ] Confirm whether production implementations of the latter two exist; do not count test fakes.
- [ ] Locate `CxlAuthorityBridge`, `CxlType3MemoryAuthority`, `RegionAuthority`, `ObservePlatformBackendReset`.
- [ ] Record existing `SingPlus.Boot` as host-debug/ManagedGc and keep it separate.
- [x] Emit and machine-validate `DirectSingNextBootBaselineV2.json`.

## Project graph

- [ ] Reuse current Boot.Contracts before considering path move.
- [x] Add Core/PlatformAdapter/Capsule projects to `SingNextOS.slnx`.
- [ ] Extend architecture classification only where existing layers are insufficient.
- [ ] BootCore has no Runtime/Host/ExecutableAdapter implementation refs.
- [ ] Capsule has no Runtime/Region/capability/Host refs.
- [ ] PlatformAdapter does not reference Capsule.

## Core and protected state

- [ ] bounded parsers/range arithmetic;
- [ ] deterministic candidate selection;
- [ ] verified destination hash;
- [ ] separate capsule/image A/B generations/floors;
- [ ] trial nonce/attempts/confirmation;
- [ ] torn-write/split-brain semantics;
- [ ] bounded recovery;
- [ ] transactional mapping FSM.

## Security/admission

- [ ] Extend existing AdmissionVerifier; no second verifier.
- [ ] Decide no-heap vs bounded-managed capsule by evidence.
- [ ] If no-heap, provide allocation-safe BootInfo writer equivalent to existing V1 codec.
- [ ] Add dependency allowlist and negative fixtures.
- [ ] NativeAOT is not authority/isolation.

## Platform backend

- [ ] early DMA deny;
- [ ] bounded PCI/capability walk;
- [ ] DVSEC/Type-3 validation;
- [ ] bounded mailbox and reset/link invalidation;
- [ ] HDM transaction/readback/reverse compensation;
- [ ] cache/order policy;
- [ ] persistent read path;
- [ ] destination hash.

## Handoff/runtime

- [ ] Reuse existing `HybridBootInfoImporter`.
- [ ] Add production `IFreshCxlBootDiscovery` adapter.
- [ ] Add production `IFirmwareApertureRetirement` adapter.
- [ ] BootInfo candidate identity never drives endpoint enumeration.
- [ ] Runtime provider owner supplies current generation semantics.
- [ ] Aperture outcome is `Released`, `Stale`, or `Quarantined`.
- [ ] Existing CXL/Region owners create authority only after admission.

## Qualification

- [x] All mandatory faults mapped to stable test or external-blocker IDs (matrix currently contains P15-F01 through P15-F26).
- [x] External SHA drift forces requalification.
- [ ] P15 aliases map to existing `PlatformExternalGateTable` where applicable.
- [ ] ISE claim has direct requalified ISE evidence.
- [ ] Hardware claim has direct named-profile evidence.
- [x] Models retained because production equivalence and external qualification are incomplete.
