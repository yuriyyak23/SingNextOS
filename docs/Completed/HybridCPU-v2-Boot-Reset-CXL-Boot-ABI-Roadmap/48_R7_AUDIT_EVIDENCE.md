# R7 Independent Audit Evidence — Stage-1 / BootInfo Model

Baseline HEAD before R7: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status recorded/preserved. Optional adapter plan absent. No destructive/remote Git, network, hardware, core, or QEMU action was used.

## Audit and remediation

- Component count/size, aligned RAM containment, overlap rejection, destination SHA-384 and all-or-nothing publication are runnable.
- Defect fixed: executable entry accepted 4-byte alignment instead of the frozen 256-byte native bundle alignment. It now uses `ResetAbiProfileV1.BundleAlignment`.
- Defect fixed: overflowing RAM geometry/absolute component end could throw instead of returning `RangeOverflow`. End arithmetic is checked before addition and remains typed.
- Defect fixed: Stage-1 reused the R2 `SemanticFault` carrying `Stage0Failure` and ignored its failure value. A phase-specific `Stage1SemanticFault` now validates operation, ordinal and `CopyFault`; regression proves prior components are cleared.
- Evidence gap fixed: the former “CXL loss after copy” test performed no loss. It now invalidates an R6 mapping with `LinkLost` after verified copy and proves RAM-resident bytes remain unchanged.
- BootInfo corruption remains rejected through CRC/SHA-384 before use. Kernel entry ABI validation remains contract/model only.

BootInfo and load results contain no OS authority. Physical/loading facts and firmware mapping state remain evidence; image/mapping/provider/region generations are not conflated. A partial or later-failed load publishes no executable components.

## Command and result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~Stage1LoadModelTests" --verbosity minimal` — passed 6, failed 0.

Claim level: `ModelValidated` for load/handoff logic and `ContractOnly` for the entry descriptor. Actual register setup, cache/coherency transition and branch into kernel remain `FutureGatedRequiresCore`; owner: HybridCPU core/frontend/runtime; missing prerequisite: approved executable-entry/register ABI implementation and core execution evidence.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed.
