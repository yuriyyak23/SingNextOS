# P15 — Managed admission, roots, durable identity and IFC scope

    **Depends on:** P13 (can run in parallel with P14/P16)  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Не создавать durable capability serialization; использовать fresh admission/generation. IFC и новый Managed Execution Contract не входят в critical path без конкретного gap.

    ## Live anchors
    - `tools/SingPlus.Admission/AdmissionVerifier.cs`
- `tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs`
- `src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs`
- `docs/Completed/SingCap-Refactoring/roadmap/PHASE_09_MANAGEDCAP_ADMISSION_AND_CLOSED_WORLD.md`

    ## Existing symbols/semantics
    - `AdmissionVerifier`
- `RestoreOrdinaryCheckpoint`
- `CapabilityAuthority`

    ## Corrections vs previous roadmap
    - No second root ledger.
- No serialization of ephemeral authority.
- ManagedCap name is not treated as live implementation evidence.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P15-01 | Specify managed execution assumptions only from live AdmissionVerifier/evidence | `PARTIALLY_EXISTING` |
| CD-P15-02 | Root/service mint policy uses existing CapabilityAuthority | `NEW_PROPOSED` |
| CD-P15-03 | Durable identity+policy+principal -> fresh ephemeral authority | `PARTIALLY_EXISTING` |
| CD-P15-04 | Keep IFC out of critical path | `DEFERRED` |

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
