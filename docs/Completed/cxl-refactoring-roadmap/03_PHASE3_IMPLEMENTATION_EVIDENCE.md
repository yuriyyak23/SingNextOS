# Phase 3 — Provider Selection and Dependency Model Evidence

## Status and delta audit

**Complete for the provider-neutral Phase 3 planning foundation.** Existing
`ComputeService` and DSC1 Copy composition remain their proven bounded service
path; they were not generalized by leaking DSC1, lane, opcode or provider
transport identity into the new planning contracts.

The delta audit found no semantic `ComputeIntent`, provider-selection policy or
execution dependency DAG. Phase 3 adds those concepts over Phase 1 RegionUse
probing and Phase 2 ExternalOperation admission without introducing CXL roles.

## Planning route and decisions

```text
ComputeIntent
  exact logical input/output region operands
  + semantic operation
  + staged/direct publication preference
  + secure-evidence / virtual-domain requirements

RuntimeKernel.PlanCompute
  -> live principal/effect validation
  -> filter available, non-faulted provider capability facts
  -> enforce secure/virtual/capacity/accelerator requirements
  -> derive staged or direct RegionUse shape
  -> RegionAuthority.ProbeUse (no authority minted, no epoch changed)
  -> explicit policy ranking
  -> immutable ComputePlan + dependency DAG

ValidateComputePlanBeforeSubmit
  -> exact provider identity/generation/availability revalidation
  -> capability/path revalidation
  -> RegionUse feasibility revalidation
  -> DAG validation
  -> caller may proceed to Phase 2 admission/submission
```

Capability dimensions are independent flags for placement, coherent access,
device-local memory, DMA, accelerator execution, staged publication, direct
coherent output, secure-compute evidence and virtualized domains. There is no
`IsCxl` shortcut. Capability/evidence filters feasibility but never creates
region or device authority.

`StagedRequired` cannot select a direct-only provider.
`DirectPreferredWithStagedFallback` chooses direct when feasible and falls back
only when caller policy explicitly permits staging. `DirectRequired` cannot
fall back. The current semantic operations preserve the same logical result
under staging; provider transport remains outside the intent.

## Dependency DAG

Both plans include ordered nodes for input preparation, memory placement,
submission, device completion, visibility/acquire, publication, downstream
consumer readiness and release/reclaim. Staged plans contain
`StagedOutputReady`; direct plans contain `DirectOutputBinding`.

Graph validation rejects missing/duplicate nodes, invalid edges, cycles and any
graph without transitive ordering through completion -> visibility ->
publication -> downstream readiness. CXL fabric topology is not represented.

## Executable evidence

`ComputePlannerTests` proves:

- a second mock provider is selected without changing `ComputeIntent`;
- planning does not skip Phase 2 `Prepared`/`Admitted` boundaries;
- coherent capability cannot override an incompatible RegionUse;
- secure evidence chooses/filter providers but cannot authorize another owner’s
  region;
- provider disappearance and generation replacement require replanning before
  submit;
- staged/direct fallback direction is explicit and non-symmetric;
- staged and direct plans have distinct RegionUse and DAG shapes;
- memory-only providers are not accelerator providers;
- unsafe DeviceComplete-to-consumer DAGs fail closed;
- public planning contracts contain no CXL shortcut or physical/provider token.

Final Phase 3 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused planning/authority/lifecycle/dependency tests
  PASS — 155/155

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 789/789 total
    659 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Claims and Phase 4 entry

Current claim: deterministic provider-neutral planning is executable without
hardware. Mock provider selection is planning evidence only, not accelerator,
coherence, security or CXL hardware proof.

`FutureGated`: production compute adapters, direct coherent replay/publication,
CXL provider bindings, Type-2/Type-3 implementations and fabric policy.

Phase 4 may begin only after the final qualification is green. It may introduce
only narrow CXL discovery, CXL.io, memory, coherent-access, fabric and security
evidence roles below the existing authority/planner/lifecycle chain, with
narrow binding-generation invalidation and no public physical identity.
