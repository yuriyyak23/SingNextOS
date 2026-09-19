# R1 Independent Audit Evidence — Reset/Map Model Boundary

## Baseline and preservation

- Baseline HEAD before R1: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`.
- `git status --short` was captured; all pre-existing and R0 changes were preserved. No destructive or remote Git operation was used.
- The optional adapter plan is absent; README/TODO, guards, roadmap requirements, source, and tests were used.

## Requirement disposition

| Requirement | Evidence | Status |
|---|---|---|
| Reset model only; no real reset claim | `AdapterResetModel`, `ResetSnapshotV1` | `ModelValidated`; actual PC/register/pipeline behavior remains gated. |
| Reset-generation invalidation | `Reset_generation_invalidates_prior_adapter_snapshot` | Pass. Prior model generation is stale after every asserted reset. |
| ROM permissions and physical ranges | `AdapterPhysicalMap`; ROM read/execute/write, overlap, overflow, cross-boundary tests | Pass. Arithmetic is checked and access is fail closed. |
| Architecture guard | adapter project rejection target plus `Architecture_guard_keeps_adapter_free_of_direct_hybridcpu_core_references` | Pass. No core/ISE reference was introduced. |

No R1 implementation defect was found after source inspection and focused execution; therefore no working code was changed in this phase.

## Authority/failure semantics

The reset snapshot and map region are architecture-neutral values and expose no boot or OS authority. Reset generation is not image/provider/region generation. Unmapped, cross-boundary, overflow, overlap, and ROM-write cases fail deterministically. R1 has no timeout, CXL cleanup, or provider behavior to claim.

## Command and result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~ResetMemoryMapModelTests" --verbosity minimal` — passed 8, failed 0.

## Claim and gates

Claim level: `ModelValidated`. Actual atomic architectural/microarchitectural reset, ROM fetch, register initialization, late-completion invalidation, and retained-device behavior are `FutureGatedRequiresCore`; owner: HybridCPU core/reset/frontend owners; missing prerequisite: approved reset/fetch/quiesce integration.

No HybridCPU core, ISE, ISA/opcode, compiler, architectural-register reset, frontend fetch, pipeline/replay, memory controller, retire coordinator, physical-register model, runtime legality, scheduler, or microarchitecture file was changed.
