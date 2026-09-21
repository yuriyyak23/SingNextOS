# P14-7 evidence — provider-neutral scheduling eligibility

## Repeat audit (2026-09-20)

Baseline HEAD/status were captured again and the pre-existing dirty worktree was preserved. The closed version-1 surface remains limited to `ManagedDefault` plus exact `None/0`; every named provider contract is still `FutureGatedRequiresProviderContract`, and unknown class/version remains fail closed. No provider bridge or claim was widened. The current focused eligibility, external-operation lifecycle, and default-gate lane passed 35/35.

The repeat negative-path sweep strengthened executable evidence so every unknown version/class, malformed identity, nonzero `None` contract version, case-mismatched contract ID, and unmapped provider contract is required to return both `MayUseManagedRuntime=false` and `MayUseProvider=false`. Null, empty, leading/trailing-whitespace, case, and version permutations are covered. This found no runtime bypass and introduced no provider implementation; it prevents an error-only assertion from overlooking accidental eligibility flags.

Post-audit focused P14-7/external-operation/CXL lifecycle/P14-0/P14-8 tests passed 54/54. Test-project and full solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1347 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58.

The strict ordered re-audit at qualification HEAD `6227ea7cf258ef6ffce52001d4d2ffee07355b35` found evidence wording drift rather than an eligibility bypass. The live platform surface does contain version-1 `PlatformExecutionPolicy` with provider-neutral priority, latency and throughput intent. That contract is a process-level, budget-bearing policy attachment bound to a provider domain lease and configured before process execution; it is not a per-stage SipJob execution-class contract and cannot losslessly represent stage identity, stage lifetime or stage completion/publication. `ExistingProcessExecutionPolicyIsNotReinterpretedAsAStageContract` now pins that boundary: naming the real v1 policy in a stage descriptor returns `FutureGatedRequiresProviderContract` with both managed and provider eligibility denied. No provider bridge or platform contract was changed.

The current eligibility/platform-policy/external-operation/default-gate/closure lane passed 49/49. Full solution build succeeded with 0 warnings and 0 errors. The exact full non-GUI command recorded 1352 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90 and 58/58. The complete pre-existing dirty worktree was preserved; no reset, checkout, clean, commit, push, remote operation or user-file deletion occurred.

Disposition: local managed eligibility is `StaticAdmission`; provider scheduling remains `FutureGatedRequiresProviderContract`. `FG-HYBRIDCPU-HINTS` and `FG-HYBRIDCPU-ACCEL` remain OFF.

Baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c`; Windows x64 JIT; SDK `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`. HEAD/status were captured before the phase and dirty state was preserved.

The live provider-neutral contracts were inspected. They contain external-effect, completion and visibility lifecycles and a versioned process-level `PlatformExecutionPolicy`. The latter carries an execution budget plus process priority/latency/throughput intent and is immutable once execution starts; it is not a stage-scoped Job scheduling contract. No live contract can losslessly represent a new per-stage SipJob class with its stage identity/lifetime and independent completion/publication semantics, so the platform/provider bridge was not widened and no existing process policy is silently reinterpreted.

Added a closed version-1 local `StageExecutionClass` eligibility descriptor. Its only admitted value is `ManagedDefault` with provider contract `None/0`; it permits managed runtime eligibility and explicitly denies provider eligibility. Unknown version/class fails closed. Naming any provider contract returns `FutureGatedRequiresProviderContract`. The surface exposes no lane, opcode, typed slot, DSC/L7/VMCS/IOMMU token, DMA/CXL queue/topology, provider handle, or mutable reference.

Executed evidence:

- focused eligibility plus existing external-operation and CXL Type-2 lifecycle regressions: 40 passed, 0 failed. These support continued completion/visibility/publication separation but do not qualify a Job provider path;
- Runtime and full solution builds: 0 warnings, 0 errors;
- full non-GUI suite: main assembly 1315 passed, 8 unchanged unrelated failures, 2 skipped (1325 total); other assemblies 90/90, 58/58, and 60/60 passed. The failures remain seven absent historical JSON artifacts and one pre-existing security-profile inventory mismatch; no P14 test failed;
- architecture/default-gate policy: 7 passed, 0 failed;
- changed JSON parsed; `git diff --check` exited 0 with only LF-to-CRLF notices.

Owner: platform/provider contract maintainers. Missing prerequisite: either an explicitly versioned stage-scoped provider-neutral scheduling contract or a proven lossless stage mapping to a future compatible contract, followed by double local/platform admission, provider restart, unknown hint, completion-versus-visibility, Region/provider lifetime and unsupported-contour tests on an exact provider tuple. The existing process-level `PlatformExecutionPolicy` v1 is not that prerequisite. No workaround is safe because fabricating stage semantics from its process budget/lease or treating an internal hint/provider receipt as authority would merge distinct scopes, intent, platform evidence, and local capability admission.

Ordinary SIP/provider lifecycle remains fallback/oracle. Local eligibility is neither authority nor provider evidence. No hardware/HybridCPU acceleration claim is made. No HybridCPU core/ISE/ISA/compiler/scheduler-legality/microarchitecture code changed; QEMU, firmware, CXL boot, confidential execution, NativeAOT, and production readiness remain unclaimed.
