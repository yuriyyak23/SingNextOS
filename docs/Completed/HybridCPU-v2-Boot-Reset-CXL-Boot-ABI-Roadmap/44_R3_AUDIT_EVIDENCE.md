# R3 Independent Audit Evidence — Trust and Protected State Model

Baseline HEAD before R3: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status was recorded/preserved. Optional adapter plan absent. No destructive/remote Git or network action was used.

## Requirement disposition and remediation

- Trust epoch, revocation, signature and rollback: pass in `TrustAndProtectedStateModelTests`.
- Production/development/recovery separation: defect fixed. Unsigned development acceptance previously occurred before key-role validation, allowing a production-role record to satisfy development mode. Role is now checked first.
- Duplicate KeyId: defect fixed. `SingleOrDefault` could throw; duplicate IDs now yield explicit `AmbiguousKey` fail-closed evidence.
- Atomic protected state: pass; prepared state is discarded on modeled power loss and never mixed with active state.
- Confirmation-bound rollback floor: defects fixed. `ProtectedBootState` now binds `RollbackDomainId`; wrong-domain confirmation and replay of an already confirmed nonce are rejected. Floor advances only once after matching domain/image/generation/nonce.
- Model store is not promoted to OTP/NVRAM. The HMAC verifier and in-memory store remain explicitly model-only.

No DTO/result provides region/provider authority. Trust epoch, image generation, protected-state sequence and rollback-domain floor remain distinct. Wrong role, duplicate key, revoked/stale key, bad signature, rollback, wrong domain/nonce and replay all fail closed.

## Command and actual result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~TrustAndProtectedStateModelTests" --verbosity minimal` — final run passed 4, failed 0. The first remediation run exposed one outdated expected decision (`UnsignedDenied` versus the stricter `UnknownKey` role mismatch); the regression assertion was corrected and product logic retained.

Claim level: `ModelValidated`. Production root, OTP/fuse, atomic monotonic protected storage and authenticated OS confirmation remain `FutureGatedRequiresHardware`; owner: platform security/protected-store firmware; missing prerequisite: qualified root/key storage, monotonic atomic persistence, and authenticated confirmation primitive.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed.
