# P14-7 evidence — provider-neutral scheduling eligibility

## Repeat audit (2026-09-20)

Baseline HEAD/status were captured again and the pre-existing dirty worktree was preserved. The closed version-1 surface remains limited to `ManagedDefault` plus exact `None/0`; every named provider contract is still `FutureGatedRequiresProviderContract`, and unknown class/version remains fail closed. No provider bridge or claim was widened. The current focused eligibility, external-operation lifecycle, and default-gate lane passed 35/35.

Disposition: local managed eligibility is `StaticAdmission`; provider scheduling remains `FutureGatedRequiresProviderContract`. `FG-HYBRIDCPU-HINTS` and `FG-HYBRIDCPU-ACCEL` remain OFF.

Baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c`; Windows x64 JIT; SDK `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`. HEAD/status were captured before the phase and dirty state was preserved.

The live provider-neutral contracts were inspected. They contain external-effect, completion and visibility lifecycles, but no versioned semantic scheduling class that can losslessly represent a new SipJob hint. The platform/provider bridge was therefore not widened.

Added a closed version-1 local `StageExecutionClass` eligibility descriptor. Its only admitted value is `ManagedDefault` with provider contract `None/0`; it permits managed runtime eligibility and explicitly denies provider eligibility. Unknown version/class fails closed. Naming any provider contract returns `FutureGatedRequiresProviderContract`. The surface exposes no lane, opcode, typed slot, DSC/L7/VMCS/IOMMU token, DMA/CXL queue/topology, provider handle, or mutable reference.

Executed evidence:

- focused eligibility plus existing external-operation and CXL Type-2 lifecycle regressions: 40 passed, 0 failed. These support continued completion/visibility/publication separation but do not qualify a Job provider path;
- Runtime and full solution builds: 0 warnings, 0 errors;
- full non-GUI suite: main assembly 1315 passed, 8 unchanged unrelated failures, 2 skipped (1325 total); other assemblies 90/90, 58/58, and 60/60 passed. The failures remain seven absent historical JSON artifacts and one pre-existing security-profile inventory mismatch; no P14 test failed;
- architecture/default-gate policy: 7 passed, 0 failed;
- changed JSON parsed; `git diff --check` exited 0 with only LF-to-CRLF notices.

Owner: platform/provider contract maintainers. Missing prerequisite: a new explicitly versioned provider-neutral semantic scheduling contract and lossless mapping, followed by double local/platform admission, provider restart, unknown hint, completion-versus-visibility, Region/provider lifetime and unsupported-contour tests on an exact provider tuple. No workaround is safe because treating an internal hint or provider receipt as authority would merge intent, platform evidence, and local capability admission.

Ordinary SIP/provider lifecycle remains fallback/oracle. Local eligibility is neither authority nor provider evidence. No hardware/HybridCPU acceleration claim is made. No HybridCPU core/ISE/ISA/compiler/scheduler-legality/microarchitecture code changed; QEMU, firmware, CXL boot, confidential execution, NativeAOT, and production readiness remain unclaimed.
