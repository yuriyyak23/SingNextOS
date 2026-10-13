# P17 — Qualify one MatrixMultiply staged-output vertical using existing HybridCPU execution substrate

    **Depends on:** P12 + P13 safety subset; independent of P14/P15/P16 optional branches  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Собрать один end-to-end contour без ISA change. Primary staged provider contour is L7-SDC MatMul because live code exposes guarded staging/commit/fence; MatrixTile remains an executable alternate/reference and is eligible only after its publication semantics independently refine the staged obligations.

    ## Live anchors
    - `contracts/SingPlus.Contracts/ComputePlanning.cs`
- `src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs`
- `HybridCPU_ISE/CloseToHSL/Core/ISA/Instructions/NonVmx/Lanes00_03Vector/MatrixTile/MtileMaccInstruction.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/MatrixTileFullPipelineHarness.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.MatrixTileRetireState.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Backends/ProviderNeutralExternalAcceleratorBackend.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`

    ## Existing symbols/semantics
    - `ComputeOperationKind (currently Copy/Transform/Reduce only)`
- `MatrixTile runtime/retire substrate`
- `MatMulCapabilityProvider`

    ## Corrections vs previous roadmap
    - MatrixTile/L7 executable presence is not automatic ProductionQualified claim.
- First qualified contour uses L7-SDC MatMul staged output; MatrixTile is alternate/reference until its publication semantics are separately proven.
- ISA impact NONE; existing ISA is reused unchanged.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P17-01 | Add MatrixMultiply semantic operation and A/B/C Region operands in SingNext | `VERIFIED_GAP` |
| CD-P17-02 | Map Matrix obligations to proven HybridCPU guarantees and exact binding | `NEW_PROPOSED` |
| CD-P17-03 | Execute via current runtime legality and staged provider publication path | `PARTIALLY_EXISTING` |
| CD-P17-04 | Collect bound usage/visibility/publication/settlement evidence | `NEW_PROPOSED` |

    ## Exit criteria
    - Every task has exact live-code anchors or an explicit VERIFIED_GAP/NEW_PROPOSED boundary.
    - No descriptor/evidence becomes authority and no existing owner is duplicated.
    - Unknown versions/classes fail closed for Mandatory semantics.
    - Tests are recorded as exists/static/run; only actual runs promote claim level.
    - HybridCPU changes stay contract/runtime/provider-specific as stated; **ISA impact NONE**.
    - Feature-gated rollout preserves baseline behavior while OFF.

    ## Formal/performance
    - Formal: As required by task cards.
    - Performance: No claim without measurement.
