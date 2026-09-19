# R2 Independent Audit Evidence — Stage-0 Recovery Model

## Baseline and preservation

Baseline HEAD before R2 was `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; `git status --short` was captured and all dirty work was preserved. The optional adapter plan remains absent. No destructive/remote Git or network action was used.

## Requirement disposition

- Deterministic Stage-0 recovery model: pass in `Stage0RecoveryModelTests`.
- ROM budget/dependency allowlist: pass; 256 KiB envelope, 128 KiB executable/read-only budget, 16 allowlisted dependencies.
- Checked RAM copy/hash/entry: pass; SHA-384 is computed over destination bytes and entry is aligned/contained.
- No partial executable publication: pass; every failure result carries empty `LoadedBytes` and partial copy is cleared.
- Semantic deterministic fault ordinal: pass after remediation below.

## Defect and remediation

Malformed fault plans were previously accepted: ordinal zero or an unknown operation silently never triggered, while `Failure=None` could produce `State=Failed` with no failure code. `Stage0RecoveryModel.Run` now rejects any fault plan without a known semantic operation, positive ordinal, and non-`None` failure. Three regression cases cover the malformed plans.

The result is model evidence only and exposes no `OwnedRegion`, `RegionUse`, capability, provider lease, mapping handle, or other authority. Addresses are checked load/entry facts, not authority. R2 has no provider generation; reset/image/mapping domains are not conflated.

## Command and result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~Stage0RecoveryModelTests" --verbosity minimal` — passed 7, failed 0 after remediation. An intermediate compile failure in the new theory (public test parameter used an internal enum) was corrected by using the underlying integer in test data; no product behavior was affected.

## Claim and gates

Claim level: `ModelValidated`. Real reset-to-ROM fetch and HybridCPU instruction execution are `FutureGatedRequiresCore` (owner: core/frontend); immutable ROM/local recovery media are `FutureGatedRequiresHardware` (owner: platform firmware/hardware). Missing prerequisites are approved executable ROM/reset integration and a qualified immutable recovery backend.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed.
