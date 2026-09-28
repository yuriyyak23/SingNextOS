# Corrected P15 PR Sequence v3

Every PR must build/test independently, be revertible, state non-goals, and cap its claim to its evidence.

| PR | Dependencies | Exact objective | Explicit non-goals | Required tests / exit | Maximum claim | External gates |
|---|---|---|---|---|---|---|
| `P15-00` | none | regenerate repository/external baseline on current master; emit `DirectSingNextBootBaselineV2.json` | implementation changes | source-path/type/status/pin inventory consistent with solution/policy | `ContractOnly` | none |
| `P15-01` | 00 | freeze/reuse existing Boot.Contracts; add Core/PlatformAdapter/Capsule skeletons; update architecture/security inventories | moving Boot.Contracts for aesthetics; boot behavior | ABI vectors; solution build; architecture-policy negative fixtures | `ContractOnly` | none |
| `P15-02` | 01 | Boot.Core ports, bounded arithmetic/results, exact limits, allocation policy primitives | PCI/MMIO/HDM implementation | bounds/property tests; forbidden refs | `ModelValidated` | none |
| `P15-03` | 02 | differential rewrite of selection/manifest/range/verified loader | protected state; hardware | model-vs-production golden vectors; corrupt manifest/hash/overlap | `ModelValidated` | none |
| `P15-04` | 02,03 | durable-state semantics: separate capsule/image A/B, rollback floors, trial nonce/attempts/confirmation, recovery | real protected hardware store | torn write, split brain, rollback, exhaustion, confirmed-image failure, recovery failure | `ModelValidated` | protected-store backend for higher claim |
| `P15-05` | 02 | BootPlatformAdapter skeleton; clock/reset/recovery/protected-state/DMA-isolation ports or fail-closed stubs | complete CXL path | deny-by-default DMA; timeout/reset stubs; no runtime authority refs | `AdapterQualified` for local adapter contour | target transport availability |
| `P15-06` | 05 | bounded PCI enumeration, capability walk, DVSEC/Type-3, mailbox/CCI, BootVolume read/selection input | HDM programming; runtime CXL ownership | capability loop, malformed DVSEC, timeout, link loss, duplicate/permutation | `AdapterQualified` | ISE/hardware PCI/CXL transport |
| `P15-07` | 06 | temporary single-target HDM transaction with readback/reverse compensation/reset invalidation | RegionAuthority/OwnedRegion | partial commit, readback mismatch, reset during mapping, ambiguous compensation | `AdapterQualified` | decoder behavior in environment |
| `P15-08` | 01,02 | define/enforce capsule static-admission profile and memory-allocation strategy before full composition | claiming AOT isolation | positive/negative admission fixtures; dependency allowlist; bounded/no-heap proof decision | `ContractOnly`/`AdapterQualified` for admission tooling | none |
| `P15-09` | 03,04,07,08 | implement Capsule composition root, allocation-safe BootInfo writer, local recovery, mock-adapter end-to-end | runtime authority adoption | exact existing BootInfo V1 vectors; corrupt capsule/image; deterministic fake E2E | `AdapterQualified` | image/entry/bootstrap for ISE |
| `P15-10` | 09 | integrate existing `HybridBootInfoImporter`; kernel-owned handoff buffer; provide production `IFreshCxlBootDiscovery` and `IFirmwareApertureRetirement` adapters | new importer API; provider-generation minting | malformed/alias/range/evidence-classification; endpoint disappear; retirement ambiguity | `AdapterQualified` | runtime/platform hooks must exist |
| `P15-11` | 10 | bind importer result to existing CXL/provider/Region authority owners; require current generation/liveness and no authority inheritance | new RegionAuthority/provider ledger | same-numeric-generation collision, provider refusal, stale generation, no BootInfo->OwnedRegion path | `AdapterQualified` | none beyond existing runtime provider |
| `P15-12` | 07,10,11 | reset/retirement/quarantine closure; late old-epoch completion; reclaim blocking | architectural reset implementation in runtime epoch code | runtime backend reset tests + architectural reset separation + ambiguous teardown | `AdapterQualified` | environment retirement support |
| `P15-13` | 09,12 | requalify current HybridCPU baseline, build reset-loadable capsule image, execute end-to-end ISE Direct Boot | HybridCPU source changes; hardware claim | pin-coherence; ISE entry/reset/CXL path; full mandatory fault matrix where ISE can represent it | `IseValidated` only if all gates pass | AOT/ENTRY/BOOTSTRAP/RESET/ISE-CXL |
| `P15-14` | 13 | exact-platform hardware qualification for PCI/CXL/HDM/DMA/reset/order/protected state | portability/general hardware claim | direct traces and fault injection on named profile | `HardwareValidated` only for named profile | hardware gates |
| `P15-15` | 13; 14 only for HW claims | retire proven-superseded model code; finalize docs/manifest/traceability | deleting still-needed oracle assets | replacement coverage + differential closure + docs/code drift check | preserve achieved claim | none |

## Sequencing corrections vs previous plan

- Security/admission policy is moved before full capsule composition.
- Existing importer is integrated, not reimplemented.
- Concrete fresh-discovery and retirement implementations are explicit deliverables.
- HybridCPU pin requalification is a qualification slice, not a documentation SHA edit.
- Boot.Contracts path relocation is not a prerequisite.
