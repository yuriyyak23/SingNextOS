# Corrected P15 PR Sequence

Each slice must:

- build/test independently;
- have no dependency on a later slice;
- be revertible without corrupting durable state;
- state explicit non-goals;
- state maximum justified claim;
- carry external gates where required.

| PR | Dependencies | Files/projects | Exact objective | Explicit non-goals | Tests / exit | Maximum claim | External gates |
|---|---|---|---|---|---|---|---|
| `P15-00` | none | docs, policy inventory tooling, baseline artifact | regenerate current-state inventory; resolve repository drift; produce type/consumer/status matrix | production boot implementation | solution build + architecture inventory consistency | `ContractOnly` | none |
| `P15-01` | 00 | Boot.Contracts, project skeletons, solution/policy | establish projects; relocate/normalize wire contracts; architecture classifications | boot policy/hardware behavior | forbidden-edge tests, ABI layout/golden vectors | `ContractOnly` | none |
| `P15-02` | 01 | Boot.Core | add in-process ports, bounded primitives, deterministic failure/result types | capsule executable, CXL hardware implementation | unit/property/bounds tests; no forbidden refs | `ModelValidated` | none |
| `P15-03` | 02 | Boot.Core + differential tests | implement candidate selection, manifest/range verification, verified loader by differential rewrite | HDM/PCI/CXL programming | golden vectors + oracle differential tests | `ModelValidated` | none |
| `P15-04` | 02,03 | Boot.Core + protected-state tests | implement protected-state, separate capsule/image A/B, rollback, trial/recovery state machines | real protected-store backend | torn-write/split-brain/rollback/trial tests | `ModelValidated` | hardware store only for higher evidence |
| `P15-05` | 02 | HybridCpu boot adapter | adapter skeleton + clock/reset/recovery/DMA-isolation implementations or fail-closed stubs | complete CXL path | adapter contract/fault tests | `AdapterQualified` | exact platform transport availability |
| `P15-06` | 05 | HybridCpu boot adapter | bounded PCI/CXL discovery, capability-chain/DVSEC parsing, Type-3/BootVolume read path | runtime CXL ownership | capability loop, malformed DVSEC, link loss, timeout tests | `AdapterQualified` | ISE/hardware transport |
| `P15-07` | 06 | Core + platform adapter | temporary single-target HDM transaction, readback, reverse compensation, retirement handle | RegionAuthority / OwnedRegion | partial-commit/readback/reset tests | `AdapterQualified` | decoder semantics in target environment |
| `P15-08` | 03,04,07 | Capsule + BootInfo builder | create composition root; BootInfo construction; local recovery path | runtime capability minting | executable adapter E2E with deterministic fakes | `AdapterQualified` | executable image/entry support |
| `P15-09` | 08 | Admission/security profile/build targets | BootCapsule static admission + NativeAOT qualification evidence | calling NativeAOT an isolation boundary | positive/negative admission fixtures; AOT artifact | `AdapterQualified` | HybridCPU AOT/image/bootstrap gates |
| `P15-10` | 08 | kernel boot importer/entry path | validate/copy/own BootInfo; normalize as evidence only | adopting boot authority | malformed/range/alias/version tests | `AdapterQualified` | none |
| `P15-11` | 10 | runtime/platform integration | fresh CXL liveness/generation admission; create new provider generation through existing owner | new RegionAuthority owner | stale endpoint, provider refusal, generation mismatch | `AdapterQualified` | existing runtime provider support |
| `P15-12` | 07,11 | runtime/platform integration | aperture retirement/quarantine + runtime backend-reset/epoch integration | architectural reset implementation | ambiguous retirement, late old-epoch completion, reclaim blocking | `AdapterQualified` | environment teardown support |
| `P15-13` | 09,12 | eng qualification + artifacts | end-to-end HybridCPU ISE Direct Boot qualification lane | hardware claim | full mandatory fault matrix + reproducible qualification artifact | `IseValidated` | all required ISE gates |
| `P15-14` | 13 | hardware qualification profile/scripts | collect direct hardware evidence for exact platform | broad portability claim | hardware protocol/reset/DMA/order tests | `HardwareValidated` only with evidence | hardware gates |
| `P15-15` | 13 (14 only for HW docs) | models/docs/manifest | retire only proven-superseded models; finalize docs/traceability/claims | deleting useful oracle coverage | replacement coverage + traceability closure | preserves achieved claim | none |

## Revert safety

- No slice before `P15-13` changes the default production boot path without an explicit feature flag.
- Durable state is versioned; readers must fail closed on unknown/newer incompatible schema.
- A PR must not leave an aperture or device enabled when reverted.
- `P15-15` is the normal place for destructive model cleanup.

## Sequencing rationale

The old plan risked coupling project moves, platform code, and claims too early. The corrected sequence establishes contracts and Core semantics first, then executable hardware adapters, then composition/handoff, then runtime authority integration, then ISE/hardware evidence.
