# R10 Independent Audit Evidence — Multi-Device/Replica/Fabric Semantics

Baseline HEAD before R10: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status recorded/preserved. Optional adapter plan absent. No destructive/remote Git, network, hardware, core, or QEMU action was used.

R4/R8 remediations were requalified against R10: enumeration/BDF/DSN/route replacement, same volume with different generations, exact duplicate replicas, protected selected-image generation conflicts and same-generation different image/digest split-brain all behave independent of discovery order. Exact duplicate rows are idempotent even when replica failover is disabled. Required physical pin and preference remain distinct.

Every handoff executes fresh OS discovery/provider query and mints a new importer admission sequence; provider device generation is current provider state, not firmware/image generation. Existing provider/Region paths remain the sole owners of live device/fabric/mapping/region generations and `OwnedRegion`/`RegionUse`.

No R10-specific defect remained after the earlier selector/importer fixes, so no additional product code was changed in this phase. Boot striping/interleave and fabric-manager authority dependency remain excluded; route/MLD/port/decoder/HPA/DPA are evidence only.

## Commands and results

1. Selector: `dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~CxlBootSelectionModelTests" --verbosity minimal` — passed 9, failed 0.
2. Importer/provider: `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~HybridBootInfoImporterTests|FullyQualifiedName~CxlType3MemoryProviderTests" --verbosity minimal` — passed 22, failed 0.

Claim level: `ModelValidated` for selector semantics and `AdapterQualified` for the local importer/provider boundary. No fabric/hardware claim.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed. SingNextOS did not adopt firmware evidence as authority.
