# R0 Independent Audit Evidence — Executable Boot Contracts

## Baseline and preservation

- Baseline HEAD before R0: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`.
- `git status --short` was captured before the phase. The pre-existing dirty worktree, including the untracked R0–R13 implementation artifacts and unrelated SingCap work, was preserved. No reset, checkout, clean, commit, push, deletion, remote Git, or network operation was used.
- `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent. The adapter README/TODO, project guards, roadmap documents, source, and tests were used instead.

## Requirement audit

| R0 requirement | Source and runnable evidence | Audit disposition |
|---|---|---|
| Explicit-width little-endian ABI and RFC-4122 UUID order | `BootWire`, `BootPolicyCodec`, `BootVolumeHeaderCodec`, `BootManifestCodec`, `HybridBootInfoCodec`; `Uuid_and_little_endian_golden_vector_is_canonical` | Pass. No object serialization or host `Guid` layout is used. |
| Fixed layouts, maxima, CRC/hash/algorithm IDs | Codec constants, `BootAbiV1`, CRC32C/SHA-384 and algorithm enums; fixed-size and round-trip tests | Pass at `ContractOnly`; crypto strength/signature execution is not claimed. |
| Bounds, truncation and overflow | `BootWire.TryRange` plus codec envelope/range checks and mutation corpus | Pass after remediation below. |
| Duplicate/unknown-required behavior | Manifest/BootInfo required-record checks and policy target uniqueness tests | Pass after adding required BootInfo duplicate rejection and unknown record-flag rejection. |
| Golden and reproducible mutation vectors | deterministic seed `0x51A6B007` in `BootContractV1Tests` | Pass; this is a bounded seeded mutation corpus, not a production fuzzing claim. |
| No production behavior change | dedicated provider-neutral `HybridCpu.Boot.Contracts` assembly | Pass. No core/runtime execution path was changed. |

## Defects found and remediated

1. Manifest TLVs and BootInfo records accepted unknown flag bits. Both parsers now reject them as `UnknownRequiredFeature`.
2. Required BootInfo record kinds could be duplicated. The parser now returns `DuplicateRequiredField`.
3. Manifest TLV, BootInfo record, and policy physical-selector padding could be nonzero, allowing non-canonical encodings. Parsers now reject nonzero padding as `InvalidReserved`.
4. HBV manifest regions could exceed the v1 64 KiB manifest limit, or use a nonzero offset with zero length. `BootVolumeHeaderCodec` now rejects both before exposing the range.

Regression tests cover every remediation. No working execution implementation was rewritten.

## Authority, identity, generations, and failure semantics

The audited DTOs contain no `OwnedRegion`, `RegionUse`, provider lease, capability, live mapping, or provider-private authority. `BootVolumeId` remains logical identity only. Image generation and firmware mapping generation remain separately named; provider and region generations are absent from the boot contract assembly. Parser failure is typed and fail closed; no malformed record is partially published.

R0 does not establish timeout, stale-generation, cleanup, ROM execution, or hardware behavior. Those are phase-owned and remain unclaimed here.

## Commands and actual results

1. `git rev-parse HEAD` — `472b7c9345605f1558958f0d00e4e2f1b4177fa4`.
2. `git status --short` — completed; existing dirty files were recorded and preserved.
3. Pre-remediation: `dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~BootContractV1Tests" --verbosity minimal` — passed 11, failed 0.
4. Post-remediation: the same command — passed 15, failed 0.

## Claim and gates

R0 claim level is `ContractOnly`. Actual reset/register/pipeline behavior and executable ROM fetch remain `FutureGatedRequiresCore`, owned by the HybridCPU core/reset/frontend owners and requiring approved reset/fetch hooks. Production fuzz qualification remains part of R13 and is not inferred from the seeded test corpus.

No HybridCPU core, ISE, ISA/opcode, compiler, architectural register reset, frontend fetch, pipeline/replay, memory controller, retire coordinator, physical-register model, runtime legality, scheduler, or microarchitecture file was changed.
