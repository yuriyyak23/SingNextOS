> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/17-P08-PREEMPTIBLE-HETEROGENEOUS-EXECUTION.md).

# P08 — Preemptible and Resumable Heterogeneous Execution

## Scope

Keep cancel/preempt/drain/capture/suspend/resume/restart/contain distinct. Qualify each provider independently; stateful resume is a stronger separate feature.

### Target semantic requirements

- Provider-neutral preemption capability/guarantee vocabulary.
- Safe-point latency guarantee only where enforceable.
- Opaque captured-state digest + provider/runtime generation binding.
- Fresh re-admission and legality on resume.

### CONTRADICTIONS

- A cancel receipt cannot be interpreted as containment.
- Checkpoint/captured state cannot preserve authority.

### Excluded designs

- Do not merge P08 with P07 locality.
- Do not make captured state a resumability capability.

### Candidate contract shapes

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

### Applicable behavior and qualification tests

- Cancel-vs-retire race.
- Preempt-vs-publication race.
- Provider reset at capture/resume.
- Stale generation resume rejection.
- Restart-only duplicate effect tests.
- Budget charging across suspend/resume/restart.
- MatrixTile/DSC/L7 provider-specific negative tests.

### Property-specific model options

- TLA+ lifecycle model including effect-possible and quarantine.
- Trace equivalence for restart/resume contours.

### DEPENDENCIES
P03 for service/charging semantics; P04 for DMA/device-backed contours; P05 failure/refinement core. P10 informs containment after provider failure.

### EXIT CRITERIA

- Each provider advertises only executable preemption class.
- Resume performs fresh SingNext, provider and HybridCPU legality gates.
- No cancellation path claims absence of effect without evidence.

## Consumer requirements

- Add preemption guarantee sidecar.
- Add lifecycle transitions and trace events.
- Add provider safe-point/capture adapters.
- Add fresh resume admission path.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-PREEMPTION`; `V6-STATEFUL-RESUME` stronger and separate  
**Property-specific claim boundaries:** `ExecutableAdapter per provider; stateful resume FutureGated until exact provider closure`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P08-A vocabulary.
- P08-B CPU/reference preemption contour.
- P08-C MatrixTile/DSC/L7 capability inventory.
- P08-D capture/resume optional contour.
- P08-E race/fault qualification.

## Rollback

Disable preemption gate; new operations use current cancellation/restart semantics. Captured state is discarded, never converted into permission.
