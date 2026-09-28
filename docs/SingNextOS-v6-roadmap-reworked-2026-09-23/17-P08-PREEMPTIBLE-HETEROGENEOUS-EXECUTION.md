# P08 — Preemptible and Resumable Heterogeneous Execution

## Structured verdict

**PHASE:** P08 — Preemptible and Resumable Heterogeneous Execution  
**BASELINE:** Cancellation, replay, retire and external operation lifecycles exist; uniform safe-point/capture/resume guarantees differ across CPU, MatrixTile, DSC, L7 and providers.  
**VERDICT:** Keep cancel/preempt/drain/capture/suspend/resume/restart/contain distinct. Qualify each provider independently; stateful resume is a stronger separate feature.

### VERIFIED_EXISTING
- HybridCPU has retire/replay/runtime-legality seams.
- ExternalRuntime has operation/session/generation lifecycles.
- ResourceBudgetAuthority can account for consumption/settlement.

### PARTIAL
- CPU-like preemption may be available, but MatrixTile/DSC/L7/external provider semantics are not uniform.
- Cancellation does not prove no effect.

### GAPS
- Provider-neutral preemption capability/guarantee vocabulary.
- Safe-point latency guarantee only where enforceable.
- Opaque captured-state digest + provider/runtime generation binding.
- Fresh re-admission and legality on resume.

### CONTRADICTIONS
- A cancel receipt cannot be interpreted as containment.
- Checkpoint/captured state cannot preserve authority.

### REMOVE_OR_MERGE
- Do not merge P08 with P07 locality.
- Do not make captured state a resumability capability.

### NEW_REQUIRED
- PreemptionGuaranteeV1 sidecar with NonPreemptible/RestartOnly/SafePoint/StateCapture classes.
- ResumeBindingV1 correlation record with operation/provider/runtime generations.

### AUTHORITY IMPACT
Invocation/external operation owner remains lifecycle owner; provider/HybridCPU owns machine execution state; budget owner accounts consumption.

### HYBRIDCPU IMPACT
`RUNTIME_ONLY`/`SIDEBAND_CONTRACT` for safe points, drain/capture/resume evidence. No authority migration.

### COMPILER IMPACT
`COMPILER_CONTRACT` only if safe-point maps are compiler-generated and validated; optional for first preemption contour.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Stale captured state replayed after Region/provider generation change.
- Resume bypasses fresh authority or runtime legality.

### CORRECTNESS RISKS
- Publication race with cancel/preempt.
- Provider reset between capture and resume.
- Partial output survives restart unexpectedly.

### PERFORMANCE RISKS
- Safe points and state capture may add latency/storage overhead; quantify per provider.

### REQUIRED TESTS
- Cancel-vs-retire race.
- Preempt-vs-publication race.
- Provider reset at capture/resume.
- Stale generation resume rejection.
- Restart-only duplicate effect tests.
- Budget charging across suspend/resume/restart.
- MatrixTile/DSC/L7 provider-specific negative tests.

### FORMAL WORK
- TLA+ lifecycle model including effect-possible and quarantine.
- Trace equivalence for restart/resume contours.

### DEPENDENCIES
P03 for service/charging semantics; P04 for DMA/device-backed contours; P05 failure/refinement core. P10 informs containment after provider failure.

### EXIT CRITERIA
- Each provider advertises only executable preemption class.
- Resume performs fresh SingNext, provider and HybridCPU legality gates.
- No cancellation path claims absence of effect without evidence.

### ROADMAP PATCH
Keep P08 independent, provider-specific and generation-bound. Split stateful resume from basic preemption by gate.

## Objective

Keep cancel/preempt/drain/capture/suspend/resume/restart/contain distinct. Qualify each provider independently; stateful resume is a stronger separate feature.

## Live baseline and existing mechanisms
- ExternalOperationAuthority.
- HybridCPU replay/retire/legality.
- ResourceBudgetAuthority settlement.

## Required implementation changes
- Add preemption guarantee sidecar.
- Add lifecycle transitions and trace events.
- Add provider safe-point/capture adapters.
- Add fresh resume admission path.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-PREEMPTION`; `V6-STATEFUL-RESUME` stronger and separate  
**Claim ceiling at emulator/runtime freeze:** `ExecutableAdapter per provider; stateful resume FutureGated until exact provider closure`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P08-A vocabulary.
- P08-B CPU/reference preemption contour.
- P08-C MatrixTile/DSC/L7 capability inventory.
- P08-D capture/resume optional contour.
- P08-E race/fault qualification.

## Rollback

Disable preemption gate; new operations use current cancellation/restart semantics. Captured state is discarded, never converted into permission.
