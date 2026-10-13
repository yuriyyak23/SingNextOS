# R9 Independent Audit Evidence — A/B and Recovery Model

Baseline HEAD before R9: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status recorded/preserved. Optional adapter plan absent. No destructive/remote Git, network, hardware, core, or QEMU action was used.

## Audit and remediation

- Inactive payload is written first, read back and SHA-384 checked; manifest and protected trial follow. Power loss is injected after every modeled persistence barrier and confirmed/signed-recovery routes remain available.
- Trial attempts are bounded; fallback order is trial → confirmed → eligible replica → signed local recovery → halt.
- Defect fixed: protected trial did not bind rollback domain, so confirmation could advance an arbitrary domain. Trial now carries the model's primary rollback domain and confirmation must match it.
- Defect fixed: confirmation could occur before the trial was ever selected/booted. `BootAttempted` is set only by trial selection; pre-boot confirmation fails.
- Defect fixed: replica fallback and new install did not apply the advanced rollback floor. Both now reject below-floor generation.
- Wrong nonce/domain, replay after confirmation and downgrade install are covered. Floor advances only after matching domain/image/generation/nonce following a trial attempt.

The in-memory model is not OTP/NVRAM/hardware evidence. Media records do not own trial/confirmed state or rollback floor. No recovery/timeout/error outcome implies OS region reclaim, release or closure.

## Command and result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~AbRecoveryModelTests|FullyQualifiedName~TrustAndProtectedStateModelTests" --verbosity minimal` — passed 13, failed 0.

Claim level: `ModelValidated`. Production atomic monotonic storage, authenticated OS confirmation and local recovery hardware remain `FutureGatedRequiresHardware`; owner: platform security/protected-store/recovery firmware; missing prerequisite: qualified persistence/power-loss/root/confirmation contracts.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed.
