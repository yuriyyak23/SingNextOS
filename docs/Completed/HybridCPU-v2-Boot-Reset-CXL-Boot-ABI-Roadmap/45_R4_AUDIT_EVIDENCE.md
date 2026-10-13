# R4 Independent Audit Evidence — Semantic CXL Selection

Baseline HEAD before R4: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty state was recorded/preserved. Optional adapter plan absent. No destructive/remote Git or network action was used.

## Audit and remediation

- Semantic `BootVolumeId`/replica/image selection, invalid media/signature filtering, rollback floor, required properties, DSN replacement and enumeration permutations are runnable in `CxlBootSelectionModelTests`.
- Same-generation different ImageId/digest fails as split-brain.
- Defect fixed: a protected `SelectedImageId` appearing with conflicting generations previously used `eligible[0]`, making selection discovery-order dependent. It now fails split-brain for every permutation.
- Defect fixed: required physical matching with no configured DSN could match a candidate whose DSN was also absent. It now reports `RequiredPhysicalSelectorMissing`.
- Defect fixed: exact duplicate discovery rows were counted as multiple replicas when failover was disabled. Exact semantic duplicates are collapsed before replica policy is applied.

BDF, DSN, route and endpoint observation remain physical evidence only. `BootVolumeId` is identity, not capability. The selector returns no authority-bearing object. Image generation remains separate from every reset/mapping/provider/region generation.

## Command and result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~CxlBootSelectionModelTests" --verbosity minimal` — passed 9, failed 0.

Claim level: `ModelValidated`. Real PCI/CXL discovery, signed-manifest crypto execution and hardware replacement/fabric behavior remain outside this claim and are gated to later model/hardware phases.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed.
